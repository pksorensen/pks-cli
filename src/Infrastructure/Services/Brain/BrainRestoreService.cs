using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Brain.Asf;
using ZstdSharp;

namespace PKS.Infrastructure.Services.Brain;

/// Pulls raw blobs back and writes them out. Spec: docs/specs/asf/05-blob-backup.md §Restore.
///
/// Three rules, all of them about not making things worse than the loss that
/// prompted the restore:
///
///   - **Verify before writing.** Every blob is decompressed to a temp file
///     while its sha256 is computed; only a match is moved into place. The hash
///     is the file's identity, so this catches a truncated download, a corrupt
///     archive and a server mix-up with one check.
///   - **Never clobber by accident.** An existing destination is skipped unless
///     `--overwrite` says otherwise, and `--in-place` only touches directories
///     whose layout can be reconstructed from a basename.
///   - **Prefer the local copy.** A blob still in this machine's own store is
///     read from disk rather than downloaded. Identical bytes by construction,
///     and it means a restore after an accidental `rm` needs no network at all.
public sealed class BrainRestoreService(
    HttpClient http,
    IBrainExportService export,
    IBrainPathResolver paths) : IBrainRestoreService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// The server caps a page; asking for more than it will give makes the
    /// truncation visible instead of pretending the answer was complete.
    private const int CatalogLimit = 5000;

    public async Task<RestoreRun> RunAsync(RestoreOptions options, IRestoreProgress progress, CancellationToken ct = default)
    {
        var endpoint = BrainPushService.NormalizeEndpoint(options.Endpoint);
        // Discovery decides where the API lives; the conventional mount is only
        // the fallback. Same rule as push — see PushOptions.ApiBase.
        var apiBase = options.ApiBase is { Length: > 0 } discovered
            ? discovered.TrimEnd('/')
            : endpoint + PushOptions.ConventionalApiPath;
        var run = new RestoreRun { Endpoint = endpoint, StartedAtUtc = DateTime.UtcNow };

        var blobs = options.FromRemote
            ? await RemoteCatalogAsync(endpoint, apiBase, options, run, ct)
            : await LocalCatalogAsync(options, run, ct);

        run.BlobsListed = blobs.Count;
        progress.Cataloged(blobs.Count, blobs.Sum(b => b.Bytes), run.Catalog);

        foreach (var (blob, destination) in Placements(blobs, options, run, progress))
        {
            ct.ThrowIfCancellationRequested();

            var local = File.Exists(paths.ExportBlobPath(blob.Sha));
            run.Plan.Add(new RestorePlanItem(blob.Sha, blob.Kind, destination, blob.Bytes, local));

            if (options.DryRun) continue;

            if (File.Exists(destination) && !options.Overwrite)
            {
                run.SkippedExisting++;
                progress.Skipped(blob.Sha, "already on disk");
                continue;
            }

            progress.Restoring(blob.Sha, destination, local);
            try
            {
                var written = await RestoreOneAsync(blob, destination, local, apiBase, options, run, ct);
                if (written < 0) continue;

                run.Restored++;
                run.BytesWritten += written;
                if (local) run.FromLocalStore++; else run.Downloaded++;
                progress.Restored(blob.Sha, written);
            }
            catch (BrainPushException ex) when (!ex.IsFatal)
            {
                run.Failures.Add($"{Short(blob.Sha)}: {ex.Code} — {ex.Message}");
                progress.Skipped(blob.Sha, ex.Code);
            }
            catch (IOException ex)
            {
                run.Failures.Add($"{Short(blob.Sha)}: {ex.Message}");
                progress.Skipped(blob.Sha, "write failed");
            }
        }

        run.FinishedAtUtc = DateTime.UtcNow;

        return run;
    }

    /// Resolves each catalog row to a destination and settles collisions.
    ///
    /// Two rows can want the same file: a Claude transcript archived twice is
    /// stored as two blobs, the older a strict prefix of the newer, and both
    /// carry the same basename. Restoring them in catalog order would leave
    /// whichever came last on disk — often the shorter one. Newest capture wins,
    /// and the prefix is dropped rather than restored to a second name nothing
    /// would read.
    private List<(CatalogBlob Blob, string Destination)> Placements(
        List<CatalogBlob> blobs, RestoreOptions options, RestoreRun run, IRestoreProgress progress)
    {
        var chosen = new Dictionary<string, (CatalogBlob Blob, string Destination)>(StringComparer.Ordinal);

        foreach (var blob in blobs)
        {
            var destination = Destination(blob, options);
            if (destination is null)
            {
                run.SkippedNoLocation++;
                progress.Skipped(blob.Sha, $"{blob.Kind} has no in-place location");
                continue;
            }

            if (chosen.TryGetValue(destination, out var existing) && !Newer(blob, existing.Blob)) continue;

            chosen[destination] = (blob, destination);
        }

        return chosen.Values.ToList();
    }

    /// Later capture wins; on a tie the larger file does, since a growing
    /// transcript's successor is never shorter than its prefix.
    private static bool Newer(CatalogBlob candidate, CatalogBlob incumbent)
    {
        var a = Timestamp(candidate);
        var b = Timestamp(incumbent);

        return a != b ? a > b : candidate.Bytes > incumbent.Bytes;
    }

    private static DateTimeOffset Timestamp(CatalogBlob blob) =>
        DateTimeOffset.TryParse(blob.CapturedAt, out var value) ? value : DateTimeOffset.MinValue;

    // ── One blob: fetch, verify, place ───────────────────────────────────────

    /// Returns the bytes written, or -1 when the blob was rejected and counted.
    private async Task<long> RestoreOneAsync(
        CatalogBlob blob,
        string destination,
        bool local,
        string apiBase,
        RestoreOptions options,
        RestoreRun run,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".pks-restore." + Guid.NewGuid().ToString("N")[..8];

        try
        {
            string sha;
            long written;

            if (local)
            {
                await using var source = File.OpenRead(paths.ExportBlobPath(blob.Sha));
                (sha, written) = await DecompressAsync(source, tmp, ct);
            }
            else
            {
                using var response = await SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/blob/{blob.Sha}"),
                    options, ct);

                run.BytesDownloaded += response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                (sha, written) = await DecompressAsync(source, tmp, ct);
            }

            if (!string.Equals(sha, blob.Sha, StringComparison.OrdinalIgnoreCase))
            {
                run.HashMismatches++;
                run.Failures.Add(
                    $"{Short(blob.Sha)}: the restored bytes hash to {Short(sha)} — nothing was written.");

                return -1;
            }

            // Move last, so an interrupted restore leaves the original file
            // untouched rather than half-overwritten.
            File.Move(tmp, destination, overwrite: true);

            return written;
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (IOException)
            {
                // A leftover temp file is noise, not a failed restore.
            }
        }
    }

    /// Decompresses into `tmp` while hashing the decompressed bytes — the same
    /// bytes the sha was taken over when the blob was archived.
    private static async Task<(string Sha, long Bytes)> DecompressAsync(Stream source, string tmp, CancellationToken ct)
    {
        await using var file = File.Create(tmp);
        using var sha = SHA256.Create();

        await using (var crypto = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
        await using (var decompressor = new DecompressionStream(source, leaveOpen: true))
        {
            await decompressor.CopyToAsync(crypto, ct);
            await crypto.FlushFinalBlockAsync(ct);
        }

        return (CanonicalJson.Hex(sha.Hash!), file.Length);
    }

    // ── Where a blob lands ───────────────────────────────────────────────────

    /// Null when `--in-place` was asked for and this kind has nowhere to go.
    ///
    /// The catalog carries a basename, never a full path — deliberately, since a
    /// path names projects and people. That is enough to put an opencode spill
    /// file back exactly where opencode looks for it, and not enough for a
    /// Claude transcript, which lives under a per-project slug directory the
    /// server was never told about. Guessing that directory would write a
    /// transcript into the wrong project, so the honest answer is to say so.
    private string? Destination(CatalogBlob blob, RestoreOptions options)
    {
        var name = SafeName(blob);

        if (!options.InPlace) return Path.Combine(options.TargetDir, blob.Kind, name);

        var root = InPlaceRoot(blob.Kind);

        return root is null ? null : Path.Combine(root, name);
    }

    /// Kinds whose original location is reconstructible from a basename alone.
    private string? InPlaceRoot(string kind) => kind switch
    {
        // The urgent one: opencode's spill directory is flat, and `tool_<id>` is
        // the whole path below it. Restoring here re-attaches the dangling
        // pointers the database still holds.
        "opencode-tool-output" => paths.OpenCodeToolOutputRoot,
        _ => null,
    };

    /// A basename from the server is untrusted input. Anything with a separator
    /// or a traversal segment is replaced by the sha, which is always a legal
    /// filename and never escapes the target directory.
    private static string SafeName(CatalogBlob blob)
    {
        var origin = blob.Origin?.Trim();
        if (string.IsNullOrEmpty(origin) || origin is "." or ".." ||
            origin.Contains('/') || origin.Contains('\\') ||
            origin.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return blob.Sha + Extension(blob.Kind);
        }

        return origin;
    }

    private static string Extension(string kind) => kind switch
    {
        "claude-transcript" or "codex-rollout" or "codex-archived" => ".jsonl",
        "opencode-session" => ".json",
        _ => "",
    };

    // ── Catalogs ─────────────────────────────────────────────────────────────

    private async Task<List<CatalogBlob>> RemoteCatalogAsync(
        string endpoint, string apiBase, RestoreOptions options, RestoreRun run, CancellationToken ct)
    {
        run.Catalog = endpoint;

        var query = $"?limit={CatalogLimit}";
        if (options.Kind is { Length: > 0 } kind) query += $"&kind={Uri.EscapeDataString(kind)}";
        if (options.Since is { } since) query += $"&since={Uri.EscapeDataString(since.ToUniversalTime().ToString("O"))}";

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/blobs{query}"),
            options, ct);

        var body = await response.Content.ReadFromJsonAsync<CatalogResponse>(Json, ct)
                   ?? throw new BrainPushException(
                       (int)response.StatusCode, "bad_response", "The endpoint returned an empty catalog.");

        run.Truncated = body.Truncated;

        return body.Blobs;
    }

    /// The local manifest, for a restore that needs no server: the blobs are
    /// already on this disk and only the originals are gone.
    private async Task<List<CatalogBlob>> LocalCatalogAsync(
        RestoreOptions options, RestoreRun run, CancellationToken ct)
    {
        run.Catalog = paths.ExportManifestPath;
        var manifest = await export.LoadManifestAsync(ct);

        return manifest.Blobs
            .Where(b => b.PrunedAt is null)
            .Where(b => options.Kind is not { Length: > 0 } kind || b.Kind == kind)
            .Where(b => options.Since is not { } since || b.CapturedAt >= since)
            // A superseded blob is a strict prefix of the one that replaced it.
            // Restoring it would put a shorter transcript back under the same
            // name for no gain.
            .Where(b => b.SupersededBy is null)
            .OrderByDescending(b => b.CapturedAt)
            .Select(b => new CatalogBlob
            {
                Sha = b.Sha,
                Kind = b.Kind,
                Bytes = b.Bytes,
                Origin = b.Origin,
                CapturedAt = b.CapturedAt.ToString("O"),
            })
            .ToList();
    }

    // ── Transport ────────────────────────────────────────────────────────────

    /// Same policy as push — 5xx and 429 back off, every other 4xx is final —
    /// and the same exception type, because a restore hits the same API with the
    /// same credential and deserves the same error codes.
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> request,
        RestoreOptions options,
        CancellationToken ct)
    {
        var delay = options.RetryBaseDelay;

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request(), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= options.MaxAttempts)
                    throw new BrainPushException(0, "unreachable", $"Could not reach the endpoint: {ex.Message}");

                await Task.Delay(delay, ct);
                delay += delay;
                continue;
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;
            if ((status >= 500 || status == 429) && attempt < options.MaxAttempts)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? delay;
                if (wait < TimeSpan.Zero) wait = delay;
                response.Dispose();
                await Task.Delay(wait < TimeSpan.FromMinutes(2) ? wait : TimeSpan.FromMinutes(2), ct);
                delay += delay;
                continue;
            }

            throw await ErrorAsync(response, ct);
        }
    }

    private static async Task<BrainPushException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        string code = "http_" + status, message = response.ReasonPhrase ?? "Request failed.";
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                var error = JsonSerializer.Deserialize<ErrorBody>(body, Json);
                if (!string.IsNullOrEmpty(error?.Code)) code = error.Code;
                if (!string.IsNullOrEmpty(error?.Message)) message = error.Message;
            }
        }
        catch (JsonException)
        {
            // A non-JSON error body still has a status.
        }
        finally
        {
            response.Dispose();
        }

        // 404 on a single blob is a per-blob problem: the catalog and the store
        // disagreed, which the rest of the restore can survive.
        return status == (int)HttpStatusCode.NotFound
            ? new BrainPushException(status, "not_found", message)
            : new BrainPushException(status, code, message);
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private sealed class CatalogResponse
    {
        [JsonPropertyName("blobs")] public List<CatalogBlob> Blobs { get; set; } = new();
        [JsonPropertyName("totalBytes")] public long TotalBytes { get; set; }
        [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}

/// One row of a restore catalog, from either side. The server's `blobs.json`
/// and the local manifest carry the same four things that matter here.
public sealed class CatalogBlob
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("origin")] public string? Origin { get; set; }
    [JsonPropertyName("capturedAt")] public string? CapturedAt { get; set; }
}
