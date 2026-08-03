using System.Security.Cryptography;
using System.Text;
using PKS.Infrastructure.Services.Brain.Asf;
using ZstdSharp;

namespace PKS.Infrastructure.Services.Brain;

/// <inheritdoc />
public sealed class BrainExportService : IBrainExportService
{
    private readonly IReadOnlyList<IAgentSessionSource> _sources;
    private readonly ISecretMasker _masker;
    private readonly IBrainPathResolver _paths;

    public BrainExportService(
        IEnumerable<IAgentSessionSource> sources,
        ISecretMasker masker,
        IBrainPathResolver paths)
    {
        _sources = sources.ToList();
        _masker = masker;
        _paths = paths;
    }

    /// Stamped into each chunk manifest so a receiver can tell which exporter
    /// produced a chunk. Outside the hashed content — see AsfChunkWriter.
    private static string Exporter =>
        "pks-cli/" + (typeof(BrainExportService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public async Task<ExportRun> RunAsync(
        ExportOptions options,
        IExportProgress progress,
        CancellationToken ct = default)
    {
        if (!AsfLevel.IsValid(options.Level))
            throw new ArgumentException($"Unknown ASF level '{options.Level}'.", nameof(options));

        var startedAt = DateTime.UtcNow;
        var run = new ExportRun
        {
            RunId = startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            Level = options.Level,
            StartedAtUtc = startedAt,
        };

        Directory.CreateDirectory(_paths.ExportRoot);
        var manifest = await LoadManifestAsync(ct);

        // ── discover ──────────────────────────────────────────────────────────
        // Sorted by (src, cursorKey) rather than by mtime: the order sessions are
        // visited decides the order events land in a chunk, and a chunk hash that
        // depended on filesystem enumeration order would never reproduce.
        var discovered = new List<(IAgentSessionSource Source, DiscoveredAgentSession Session)>();
        foreach (var source in _sources)
        {
            if (options.SourceFilter is { Length: > 0 } wanted &&
                !string.Equals(source.Kind, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (!source.IsAvailable) continue;

            foreach (var session in source.Discover(options.ProjectFilter))
                discovered.Add((source, session));
        }
        discovered.Sort((a, b) => string.CompareOrdinal(a.Session.CursorKey, b.Session.CursorKey));
        run.SessionsScanned = discovered.Count;
        progress.Discovered(discovered.Count);

        // ── filter against the cursors ────────────────────────────────────────
        var eligible = new List<(IAgentSessionSource Source, DiscoveredAgentSession Session, int StartSeq)>();

        // Sessions whose events are already exported but whose raw form is not.
        // Without this list, a session that fell quiet while its blob was being
        // deferred would never be archived at all: the event cursor is satisfied
        // forever after, so the session is never looked at again.
        var blobOnly = new List<(IAgentSessionSource Source, DiscoveredAgentSession Session)>();

        foreach (var (source, session) in discovered)
        {
            if (options.SinceUtc is { } since && session.LastModifiedUtc < since) continue;

            var startSeq = 0;
            if (!options.Force && manifest.Cursors.TryGetValue(session.CursorKey, out var cursor))
            {
                if (AsfLevelProjector.Enriches(cursor.Level, options.Level))
                {
                    // Upgrade: re-emit the whole session. Same ids, richer bodies.
                    startSeq = 0;
                }
                else if (cursor.Bytes == session.Bytes && cursor.MtimeUtc == session.LastModifiedUtc)
                {
                    run.SessionsSkipped++;
                    if (options.IncludeBlobs && cursor.BlobBytes != session.Bytes)
                        blobOnly.Add((source, session));

                    continue;
                }
                else
                {
                    // The source grew. Sources are append-only, so everything below
                    // NextSeq is byte-identical to what already left the machine.
                    startSeq = cursor.NextSeq;
                }
            }

            eligible.Add((source, session, startSeq));
        }
        if (options.Limit is { } cap && eligible.Count > cap) eligible = eligible.Take(cap).ToList();
        progress.Filtered(eligible.Count, run.SessionsSkipped);

        // ── write chunks ──────────────────────────────────────────────────────
        // One open writer per (day, src). Level is fixed for the whole run, and a
        // session's events can straddle midnight, so the bucket is chosen per event.
        var writers = new Dictionary<(DateOnly Day, string Src), AsfChunkWriter>();
        var sealedChunks = new List<ChunkManifest>();

        void SealWriter(AsfChunkWriter writer)
        {
            if (writer.IsEmpty) return;

            var dir = _paths.ExportChunkDir(writer.Day);
            var chunk = writer.Seal(
                dir, NextOrdinal(dir, writer.Src, writer.Level),
                DateTimeOffset.UtcNow, Exporter, _paths.ExportRoot);

            sealedChunks.Add(chunk);
            run.ChunksSealed++;
            run.ChunkBytes += chunk.Bytes;
            run.ChunkUncompressedBytes += chunk.UncompressedBytes;
            progress.Sealing(chunk);
        }

        foreach (var (source, session, startSeq) in eligible)
        {
            ct.ThrowIfCancellationRequested();

            var written = 0L;
            var maxSeq = startSeq - 1;
            var terminalSeq = -1;
            var failed = false;

            try
            {
                await foreach (var full in source.ReadAsync(session, _masker, ct))
                {
                    maxSeq = Math.Max(maxSeq, full.Seq);
                    terminalSeq = full.Kind == AsfKind.SessionEnd ? full.Seq : -1;
                    if (full.Seq < startSeq)
                    {
                        run.EventsSkipped++;

                        continue;
                    }

                    var day = BrainDayKey.Of(full.Ts);
                    var key = (day, source.Kind);
                    if (!writers.TryGetValue(key, out var writer))
                    {
                        writer = new AsfChunkWriter(day, source.Kind, options.Level, options.SealBytes);
                        writers[key] = writer;
                    }

                    writer.Add(AsfLevelProjector.Project(full, options.Level));
                    written++;

                    if (writer.ShouldSeal)
                    {
                        SealWriter(writer);
                        writers.Remove(key);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed = true;
                run.SessionsFailed++;
                run.Failures.Add($"{session.CursorKey}: {ex.Message}");
            }

            progress.Finished(session.CursorKey, written, failed);
            if (failed) continue;

            run.SessionsExported++;
            run.EventsWritten += written;

            var record = new ExportCursor
            {
                Level = options.Level,

                // Stop *at* the synthetic session_end rather than past it. It is not
                // part of the append-only body: when the session grows, the parser
                // re-emits it at a higher seq and the seq it used to occupy is taken
                // by the first genuinely new event. Advancing past it would skip that
                // event forever — silent loss, invisible in every counter.
                //
                // The cost is one re-emitted session_end per run per growing session.
                // Its content moves with the session, so it lands as a distinct id;
                // the receiver treats the newest session_end for a session as the
                // authoritative one.
                NextSeq = terminalSeq >= 0 ? terminalSeq : maxSeq + 1,
                Bytes = session.Bytes,
                MtimeUtc = session.LastModifiedUtc,
                ExportedAt = DateTimeOffset.UtcNow,
                BlobSha = manifest.Cursors.GetValueOrDefault(session.CursorKey)?.BlobSha,
                BlobBytes = manifest.Cursors.GetValueOrDefault(session.CursorKey)?.BlobBytes,
            };

            if (options.IncludeBlobs)
            {
                var blob = ArchiveSessionBlob(source, session, options, manifest, run);
                if (blob is not null)
                {
                    record.BlobSha = blob;
                    record.BlobBytes = session.Bytes;
                }
            }

            manifest.Cursors[session.CursorKey] = record;
        }

        foreach (var writer in writers.Values) SealWriter(writer);

        foreach (var (source, session) in blobOnly)
        {
            var blob = ArchiveSessionBlob(source, session, options, manifest, run);
            if (blob is not null && manifest.Cursors.TryGetValue(session.CursorKey, out var cursor))
            {
                cursor.BlobSha = blob;
                cursor.BlobBytes = session.Bytes;
            }
        }

        // ── the 7-day rescue ──────────────────────────────────────────────────
        if (options.IncludeBlobs)
        {
            ArchiveOpenCodeSpills(manifest, run);
            PruneSupersededBlobs(options, manifest, run);
        }

        manifest.Chunks.AddRange(sealedChunks);
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
        await WriteManifestAsync(manifest, ct);

        run.FinishedAtUtc = DateTime.UtcNow;

        return run;
    }

    // ── blobs ─────────────────────────────────────────────────────────────────

    private string? ArchiveSessionBlob(
        IAgentSessionSource source,
        DiscoveredAgentSession session,
        ExportOptions options,
        ExportManifest manifest,
        ExportRun run)
    {
        // A session still being written is archived on the next run instead. See
        // ExportOptions.BlobQuietPeriod for why.
        if (DateTime.UtcNow - session.LastModifiedUtc < options.BlobQuietPeriod)
        {
            run.BlobsDeferred++;

            return null;
        }

        var kind = source.Kind switch
        {
            AsfSource.Claude => "claude-transcript",
            AsfSource.Codex => "codex-rollout",
            _ => "opencode-session",
        };

        var previous = manifest.Cursors.GetValueOrDefault(session.CursorKey)?.BlobSha;

        try
        {
            var sha = StoreBlob(
                kind,
                source.Kind,
                session.BackingFile is { } f ? Path.GetFileName(f) : session.NativeSessionId,
                manifest,
                run,
                dest => source.WriteRawBackup(session, dest));

            if (sha is not null && previous is not null && previous != sha)
                MarkSuperseded(previous, sha, manifest, run);

            return sha;
        }
        catch (Exception ex)
        {
            run.Failures.Add($"blob {session.CursorKey}: {ex.Message}");

            return null;
        }
    }

    /// The old blob is a strict prefix of the new one — the sources are
    /// append-only — so it carries no information the successor lacks. It is kept
    /// for a grace period anyway, because "the successor is on disk" is a weaker
    /// claim than "the successor is safely uploaded", and a week of margin costs
    /// less than the one case where that difference mattered.
    private static void MarkSuperseded(string oldSha, string newSha, ExportManifest manifest, ExportRun run)
    {
        var record = manifest.Blobs.FirstOrDefault(b => b.Sha == oldSha);
        if (record is null || record.SupersededBy is not null) return;

        record.SupersededBy = newSha;
        record.SupersededAt = DateTimeOffset.UtcNow;
        run.BlobsSuperseded++;
    }

    private void PruneSupersededBlobs(ExportOptions options, ExportManifest manifest, ExportRun run)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var record in manifest.Blobs)
        {
            if (record.SupersededBy is not { } successor) continue;
            if (record.PrunedAt is not null) continue;
            if (record.SupersededAt is not { } at || now - at < options.BlobSupersededGrace) continue;

            // Never delete on the strength of a manifest entry alone.
            if (!File.Exists(_paths.ExportBlobPath(successor))) continue;

            var path = _paths.ExportBlobPath(record.Sha);
            try
            {
                if (File.Exists(path))
                {
                    run.BlobBytesPruned += new FileInfo(path).Length;
                    File.Delete(path);
                }

                record.PrunedAt = now;
                run.BlobsPruned++;
            }
            catch (IOException ex)
            {
                run.Failures.Add($"prune {record.Sha[..8]}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                run.Failures.Add($"prune {record.Sha[..8]}: {ex.Message}");
            }
        }
    }

    /// opencode spills tool output over 2000 lines / 50 KiB to `tool-output/` and
    /// deletes it after a hardcoded 7 days. Nothing in the database survives that
    /// — only a truncated head/tail and a pointer to a file that is gone. Copying
    /// the directory is the entire reason this job runs daily rather than weekly.
    private void ArchiveOpenCodeSpills(ExportManifest manifest, ExportRun run)
    {
        var root = _paths.OpenCodeToolOutputRoot;
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.EnumerateFiles(root, "tool_*", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var added = StoreBlob(
                    "opencode-tool-output", AsfSource.OpenCode, Path.GetFileName(file),
                    manifest, run,
                    dest =>
                    {
                        using var input = File.OpenRead(file);
                        input.CopyTo(dest);

                        return input.Length;
                    });

                if (added is not null) run.SpillFilesArchived++;
            }
            catch (IOException ex)
            {
                run.Failures.Add($"spill {Path.GetFileName(file)}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                run.Failures.Add($"spill {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    /// Content-addresses whatever `write` produces and stores it compressed at
    /// `blobs/<sha[0:2]>/<sha>.zst`. Returns the sha, or null when there was
    /// nothing to store.
    ///
    /// Hash first, compress second, and always over the raw bytes: identical
    /// content stored twice collapses to one file no matter which source produced
    /// it, and re-running the export is free.
    private string? StoreBlob(
        string kind,
        string src,
        string origin,
        ExportManifest manifest,
        ExportRun run,
        Func<Stream, long?> write)
    {
        var tmpDir = Path.Combine(_paths.ExportRoot, "tmp");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("n"));

        try
        {
            long? bytes;
            byte[] hash;
            using (var file = File.Create(tmp))
            using (var sha = SHA256.Create())
            {
                using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
                {
                    bytes = write(crypto);
                    crypto.FlushFinalBlock();
                }

                hash = sha.Hash!;
            }

            if (bytes is null or 0) return null;

            var sha256 = CanonicalJson.Hex(hash);
            var blobPath = _paths.ExportBlobPath(sha256);
            if (File.Exists(blobPath))
            {
                run.BlobsAlreadyPresent++;

                return sha256;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);

            using (var input = File.OpenRead(tmp))
            using (var output = File.Create(blobPath))
            using (var compressor = new CompressionStream(output))
            {
                input.CopyTo(compressor);
            }

            manifest.Blobs.Add(new BlobRecord
            {
                Sha = sha256,
                Kind = kind,
                Src = src,
                Bytes = bytes.Value,
                StoredBytes = new FileInfo(blobPath).Length,
                CapturedAt = DateTimeOffset.UtcNow,
                Origin = origin,
            });

            run.BlobsAdded++;
            run.BlobBytes += bytes.Value;

            return sha256;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (IOException)
            {
                // A leftover temp file is noise, not a failed export.
            }
        }
    }

    // ── manifest ──────────────────────────────────────────────────────────────

    public async Task<ExportManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        var path = _paths.ExportManifestPath;
        if (!File.Exists(path)) return new ExportManifest();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);

            return System.Text.Json.JsonSerializer.Deserialize<ExportManifest>(
                json, CanonicalJson.SerializerOptions) ?? new ExportManifest();
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt manifest must not wedge the daily job forever. Starting
            // over re-uploads chunks the server already has, which its own hash
            // check turns back into a no-op.
            return new ExportManifest();
        }
    }

    public Task SaveManifestAsync(ExportManifest manifest, CancellationToken ct = default) =>
        WriteManifestAsync(manifest, ct);

    private async Task WriteManifestAsync(ExportManifest manifest, CancellationToken ct)
    {
        var path = _paths.ExportManifestPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Atomic replace: a crash mid-write must not leave a manifest that
        // disagrees with the chunks already on disk.
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, CanonicalJson.Serialize(manifest) + "\n", new UTF8Encoding(false), ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// The next free ordinal for `<src>-<level>-<nn>` in a day directory. Sealed
    /// chunks are immutable, so a later run appends a new ordinal rather than
    /// rewriting the last one.
    private static int NextOrdinal(string dir, string src, string level)
    {
        if (!Directory.Exists(dir)) return 0;

        var prefix = $"{src}-{level}-";
        var used = Directory.EnumerateFiles(dir, prefix + "*.ndjson.zst")
            .Select(f => Path.GetFileName(f)[prefix.Length..])
            .Select(s => int.TryParse(s.Split('.')[0], out var n) ? n : -1)
            .Where(n => n >= 0)
            .ToList();

        return used.Count == 0 ? 0 : used.Max() + 1;
    }
}
