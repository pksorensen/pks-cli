using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Infrastructure.Services.Brain;

/// Uploads what `brain export` sealed. Spec: docs/specs/asf/04-sync-protocol.md.
///
/// Three properties are worth stating because they are what make a daily cron
/// safe to run unattended:
///
///   - **Nothing is claimed that was not acknowledged.** A manifest row is only
///     stamped `uploadedAt` after the commit that covered it returned 200. A
///     crash mid-push re-offers the same chunks tomorrow, and the receiver
///     answers "already have it" for free.
///   - **One bad chunk does not sink the batch.** A 409 hash_mismatch or a
///     vanished file drops that chunk and the rest still lands; the failure is
///     reported rather than swallowed.
///   - **A different endpoint starts from zero.** Upload stamps are per-endpoint
///     by construction: pointing the manifest at a new server clears them, so
///     the new server is offered everything on disk instead of inheriting
///     someone else's cursor.
public sealed class BrainPushService(
    HttpClient http,
    IBrainExportService export,
    IBrainPathResolver paths) : IBrainPushService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PushRun> RunAsync(PushOptions options, IPushProgress progress, CancellationToken ct = default)
    {
        var endpoint = NormalizeEndpoint(options.Endpoint);
        var apiBase = ResolveApiBase(endpoint, options);
        var run = new PushRun { Endpoint = endpoint, StartedAtUtc = DateTime.UtcNow };
        var manifest = await export.LoadManifestAsync(ct);

        // A manifest that points somewhere else has upload stamps that mean
        // nothing here. Clearing them is the honest reading of "this server has
        // never seen my data" — and it is safe, because every re-offer is
        // deduped by hash on arrival.
        if (!string.IsNullOrEmpty(manifest.Endpoint) &&
            !string.Equals(manifest.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in manifest.Chunks) { c.UploadedAt = null; c.SyncId = null; }
            foreach (var b in manifest.Blobs) b.UploadedAt = null;
            run.EndpointChanged = true;
        }

        var chunks = SelectChunks(manifest, options, run);
        var blobs = SelectBlobs(manifest, options, run);

        run.ChunksConsidered = chunks.Count;
        run.BlobsConsidered = blobs.Count;
        progress.Planned(chunks.Count, blobs.Count, chunks.Sum(c => c.Chunk.Bytes) + blobs.Sum(b => b.Blob.StoredBytes));

        if (options.DryRun || (chunks.Count == 0 && blobs.Count == 0))
        {
            run.FinishedAtUtc = DateTime.UtcNow;

            return run;
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        var machine = MachineId();

        var chunkQueue = new Queue<PendingChunk>(chunks);
        var blobQueue = new Queue<PendingBlob>(blobs);

        while (chunkQueue.Count > 0 || blobQueue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var batchChunks = Take(chunkQueue, options.BatchSize);
            var batchBlobs = Take(blobQueue, options.BlobBatchSize);

            await PushBatchAsync(apiBase, machine, batchChunks, batchBlobs, options, progress, run, ct);
            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            manifest.Endpoint = endpoint;
            await export.SaveManifestAsync(manifest, ct);
        }

        if (options.WaitForProjection > TimeSpan.Zero)
            run.Projection = await WaitForProjectionAsync(apiBase, options, progress, run, ct);

        run.FinishedAtUtc = DateTime.UtcNow;

        return run;
    }

    /// Polls until the receiver has folded everything it holds, or the deadline
    /// passes. A timeout is not a failure and never fails the push: the bytes are
    /// stored, and the fold will finish on the receiver's own schedule whether we
    /// are watching or not.
    private async Task<ProjectionStatus?> WaitForProjectionAsync(
        string apiBase,
        PushOptions options,
        IPushProgress progress,
        PushRun run,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + options.WaitForProjection;
        ProjectionStatus? last = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var response = await SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/projection"), options, ct);
                last = await ReadAsync<ProjectionStatus>(response, ct);
            }
            catch (BrainPushException ex)
            {
                // A receiver that predates this endpoint, or a transient failure
                // after the data is already stored. Neither is worth failing on.
                run.Failures.Add($"projection: {ex.Code} — {ex.Message}");

                return last;
            }

            progress.Projecting(last);

            if (last.Done || DateTime.UtcNow + options.ProjectionPollInterval > deadline) return last;

            await Task.Delay(options.ProjectionPollInterval, ct);
        }
    }

    // ── One batch: sync → upload the missing → commit ─────────────────────────

    private async Task PushBatchAsync(
        string apiBase,
        string machine,
        List<PendingChunk> chunks,
        List<PendingBlob> blobs,
        PushOptions options,
        IPushProgress progress,
        PushRun run,
        CancellationToken ct)
    {
        // Two attempts: the first may lose chunks to a hash mismatch or an
        // expired session, and the second commits what is left. A third would
        // only repeat the second's outcome.
        for (var attempt = 0; attempt < 2 && (chunks.Count > 0 || blobs.Count > 0); attempt++)
        {
            var sync = await OpenSyncAsync(apiBase, machine, chunks, blobs, options, ct);
            run.Syncs++;
            progress.SyncOpened(sync.SyncId, sync.MissingChunks.Count, sync.MissingBlobs.Count);
            // Only on the first attempt. A retry re-offers chunks this batch
            // already uploaded, and the server now answers "already have it" for
            // every one of them — counting that would report the same chunk as
            // both uploaded and already-there, and push the totals past what was
            // offered.
            if (attempt == 0)
            {
                run.ChunksKnown += chunks.Count - sync.MissingChunks.Count;
                run.BlobsKnown += blobs.Count - sync.MissingBlobs.Count;
                // The bulk of a re-push settles right here, before a single byte
                // moves: progress that only tracked uploads would sit at zero
                // through the fastest part of the run.
                progress.Advanced(Snapshot(run));
            }

            var missingChunks = sync.MissingChunks.ToHashSet(StringComparer.Ordinal);
            var missingBlobs = sync.MissingBlobs.ToHashSet(StringComparer.Ordinal);
            var dropped = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pending in chunks.Where(c => missingChunks.Contains(c.Chunk.ChunkHash)))
            {
                ct.ThrowIfCancellationRequested();
                if (!await UploadAsync("chunk", apiBase, sync.SyncId, pending.Chunk.ChunkHash, pending.Path,
                        options, progress, run, ct))
                {
                    dropped.Add(pending.Chunk.ChunkHash);
                }
            }

            foreach (var pending in blobs.Where(b => missingBlobs.Contains(b.Blob.Sha)))
            {
                ct.ThrowIfCancellationRequested();
                if (!await UploadAsync("blob", apiBase, sync.SyncId, pending.Blob.Sha, pending.Path,
                        options, progress, run, ct))
                {
                    dropped.Add(pending.Blob.Sha);
                }
            }

            if (dropped.Count > 0)
            {
                // The server's copy of the manifest still lists them, so this
                // session can never commit. Drop them and open a clean one; the
                // chunks that did upload are already stored and come back as
                // known, so the retry costs a round trip, not a re-upload.
                run.ChunksDropped += chunks.Count(c => dropped.Contains(c.Chunk.ChunkHash));
                run.BlobsDropped += blobs.Count(b => dropped.Contains(b.Blob.Sha));
                chunks = chunks.Where(c => !dropped.Contains(c.Chunk.ChunkHash)).ToList();
                blobs = blobs.Where(b => !dropped.Contains(b.Blob.Sha)).ToList();
                progress.Advanced(Snapshot(run));
                continue;
            }

            CommitResult result;
            try
            {
                result = await CommitAsync(apiBase, sync.SyncId, options, ct);
            }
            catch (BrainPushException ex) when (ex.Code is "sync_expired" or "chunks_missing" && attempt == 0)
            {
                run.Failures.Add($"commit retried: {ex.Code} — {ex.Message}");
                continue;
            }

            progress.Committed(result);
            run.Queued += result.Queued;
            run.QueuedDuplicate += result.Duplicate;
            run.QueuedEvents += result.QueuedEvents;
            // Not accumulated: `pending` is the receiver's whole backlog, so the
            // last commit's answer is the current one. Summing it would count the
            // same unfolded chunks once per batch.
            run.Pending = result.Pending;
            run.StorageBytes = result.StorageBytes;
            foreach (var day in result.Days) run.Days.Add(day);

            var stamp = DateTimeOffset.UtcNow;
            foreach (var c in chunks) { c.Chunk.UploadedAt = stamp; c.Chunk.SyncId = sync.SyncId; }
            foreach (var b in blobs) b.Blob.UploadedAt = stamp;

            return;
        }
    }

    private async Task<SyncResponse> OpenSyncAsync(
        string apiBase,
        string machine,
        List<PendingChunk> chunks,
        List<PendingBlob> blobs,
        PushOptions options,
        CancellationToken ct)
    {
        var body = new SyncRequest
        {
            Client = $"pks-cli/{VersionString()}",
            Machine = machine,
            Chunks = chunks.Select(c => new SyncChunk
            {
                ChunkHash = c.Chunk.ChunkHash,
                Src = c.Chunk.Src,
                Level = c.Chunk.Level,
                Day = c.Chunk.Day,
                Ordinal = c.Chunk.Ordinal,
                Events = c.Chunk.Events,
                Bytes = c.Chunk.Bytes,
                UncompressedBytes = c.Chunk.UncompressedBytes,
                FirstTs = c.Chunk.FirstTs?.ToString("O"),
                LastTs = c.Chunk.LastTs?.ToString("O"),
                SessionIds = c.Chunk.SessionIds,
            }).ToList(),
            Blobs = blobs.Select(b => new SyncBlob
            {
                Sha = b.Blob.Sha,
                Kind = b.Blob.Kind,
                Src = b.Blob.Src,
                Bytes = b.Blob.Bytes,
                Origin = b.Blob.Origin,
                CapturedAt = b.Blob.CapturedAt.ToString("O"),
            }).ToList(),
        };

        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/sync")
            {
                Content = JsonContent.Create(body, options: Json),
            },
            options, ct);

        return await ReadAsync<SyncResponse>(response, ct);
    }

    private async Task<CommitResult> CommitAsync(string apiBase, string syncId, PushOptions options, CancellationToken ct)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/sync/commit")
            {
                Content = JsonContent.Create(new { syncId }, options: Json),
            },
            options, ct);

        return await ReadAsync<CommitResult>(response, ct);
    }

    /// Returns false when this artifact must be dropped from the batch. Fatal
    /// refusals (auth, level, quota) throw instead — retrying them with the same
    /// credential would just repeat the refusal.
    private async Task<bool> UploadAsync(
        string kind,
        string apiBase,
        string syncId,
        string hash,
        string path,
        PushOptions options,
        IPushProgress progress,
        PushRun run,
        CancellationToken ct)
    {
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (IOException ex)
        {
            run.Failures.Add($"{kind} {Short(hash)}: {ex.Message}");

            return false;
        }

        progress.Uploading(kind, hash, bytes);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, $"{apiBase}/{kind}/{hash}")
                    {
                        // Re-opened per attempt: a retried request cannot reuse a
                        // stream the failed one already drained.
                        Content = new StreamContent(File.OpenRead(path)),
                    };
                    request.Headers.TryAddWithoutValidation("X-Asf-Sync", syncId);
                    request.Content.Headers.ContentType =
                        new MediaTypeHeaderValue(kind == "chunk" ? "application/vnd.asf+ndjson" : "application/octet-stream");
                    request.Content.Headers.ContentEncoding.Add("zstd");

                    return request;
                },
                options, ct);
        }
        catch (BrainPushException ex) when (!ex.IsFatal)
        {
            run.Failures.Add($"{kind} {Short(hash)}: {ex.Code} — {ex.Message}");

            return false;
        }

        var duplicate = response.StatusCode == HttpStatusCode.OK;
        response.Dispose();

        // A 200 means the server already had these bytes — a race with another
        // machine, or a retry after a lost response. Counting it as uploaded
        // would overstate what actually crossed the wire.
        if (kind == "chunk")
        {
            if (duplicate) run.ChunksKnown++;
            else { run.ChunksUploaded++; run.BytesUploaded += bytes; }
        }
        else
        {
            if (duplicate) run.BlobsKnown++;
            else { run.BlobsUploaded++; run.BlobBytesUploaded += bytes; }
        }
        progress.Uploaded(kind, hash, duplicate);
        progress.Advanced(Snapshot(run));

        return true;
    }

    /// Chunks and blobs that are no longer in question, either way. Files that
    /// were already gone when the batch was selected are deliberately absent:
    /// they never counted towards `ChunksConsidered` either.
    private static PushProgressSnapshot Snapshot(PushRun run) => new(
        ChunksSettled: run.ChunksKnown + run.ChunksUploaded + run.ChunksDropped,
        ChunksTotal: run.ChunksConsidered,
        BlobsSettled: run.BlobsKnown + run.BlobsUploaded + run.BlobsDropped,
        BlobsTotal: run.BlobsConsidered,
        BytesUploaded: run.BytesUploaded + run.BlobBytesUploaded);

    // ── Transport ────────────────────────────────────────────────────────────

    /// Sends with the spec's retry policy: 5xx and 429 back off exponentially up
    /// to MaxAttempts; every other 4xx is final. The request is built by a
    /// factory rather than passed in because HttpRequestMessage — and any stream
    /// content it holds — cannot be sent twice.
    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> request,
        PushOptions options,
        CancellationToken ct)
    {
        var delay = options.RetryBaseDelay;
        var reauthenticated = false;

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request(), HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                // A refused connection or a dropped TLS handshake is exactly the
                // transient a laptop closing its lid produces; retry it like a 5xx.
                if (attempt >= options.MaxAttempts)
                    throw new BrainPushException(0, "unreachable", $"Could not reach the endpoint: {ex.Message}");

                await Task.Delay(delay, ct);
                delay += delay;
                continue;
            }

            if (response.IsSuccessStatusCode) return response;

            var status = (int)response.StatusCode;

            // A 401 halfway through a long push means the access token aged out,
            // not that the account is wrong. Swap in a fresh one and repeat the
            // request — once. A second 401 is a real rejection, and retrying it
            // would only hammer the login endpoint.
            if (status == 401 && !reauthenticated && options.Reauthenticate is { } reauth)
            {
                reauthenticated = true;
                response.Dispose();
                var fresh = await reauth(ct);
                if (fresh is { Length: > 0 })
                {
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
                    attempt--; // the expired credential should not cost a retry budget

                    continue;
                }

                throw new BrainPushException(401, "unauthorized",
                    "The credential expired and could not be renewed. Run `pks agentics init` again.");
            }

            var retryable = status >= 500 || status == 429;
            if (retryable && attempt < options.MaxAttempts)
            {
                var wait = response.Headers.RetryAfter?.Delta
                           ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null)
                           ?? delay;
                if (wait < TimeSpan.Zero) wait = delay;
                response.Dispose();
                // Cap the honoured Retry-After: an unattended daily job must not
                // be parked for an hour by one header.
                await Task.Delay(Min(wait, TimeSpan.FromMinutes(2)), ct);
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
        long? quotaBytes = null, quotaUsedBytes = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                var error = JsonSerializer.Deserialize<ErrorBody>(body, Json);
                if (!string.IsNullOrEmpty(error?.Code)) code = error.Code;
                if (!string.IsNullOrEmpty(error?.Message)) message = error.Message;
                quotaBytes = error?.QuotaBytes;
                quotaUsedBytes = error?.QuotaUsedBytes;
            }
        }
        catch (JsonException)
        {
            // A non-JSON error body (a proxy's HTML, say) still has a status.
        }
        finally
        {
            response.Dispose();
        }

        return new BrainPushException(status, code, message)
        {
            QuotaBytes = quotaBytes,
            QuotaUsedBytes = quotaUsedBytes,
        };
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);

            return value ?? throw new BrainPushException(
                (int)response.StatusCode, "bad_response", "The endpoint returned an empty body.");
        }
    }

    // ── Selection ────────────────────────────────────────────────────────────

    private List<PendingChunk> SelectChunks(ExportManifest manifest, PushOptions options, PushRun run)
    {
        var selected = new List<PendingChunk>();

        foreach (var chunk in manifest.Chunks.OrderBy(c => c.Day, StringComparer.Ordinal).ThenBy(c => c.Ordinal))
        {
            if (!options.Force && chunk.UploadedAt is not null) continue;
            if (options.LevelFilter is { Length: > 0 } level && chunk.Level != level) continue;
            if (options.SourceFilter is { Length: > 0 } src && chunk.Src != src) continue;
            if (chunk.Path is not { Length: > 0 } relative) continue;

            var full = Path.Combine(paths.ExportRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
            {
                run.ChunksMissingLocally++;
                continue;
            }

            if (TooLarge(chunk.Bytes, options.MaxChunkBytes))
            {
                run.Failures.Add(
                    $"chunk {Short(chunk.ChunkHash)}: {chunk.Bytes} bytes exceeds the receiver's {options.MaxChunkBytes}-byte limit");
                continue;
            }

            selected.Add(new PendingChunk(chunk, full));
        }

        return selected;
    }

    private List<PendingBlob> SelectBlobs(ExportManifest manifest, PushOptions options, PushRun run)
    {
        var selected = new List<PendingBlob>();
        if (!options.IncludeBlobs) return selected;

        // Blobs are the unmasked originals, so they only travel with `full`
        // data. Requiring a full-level chunk on disk rather than trusting the
        // token keeps the client honest even when the token would allow it: a
        // metrics-only export has no business shipping raw transcripts.
        var levelForRun = options.LevelFilter ?? manifest.Chunks.Select(c => c.Level).OrderByDescending(AsfLevel.Rank).FirstOrDefault();
        if (levelForRun != AsfLevel.Full) return selected;

        foreach (var blob in manifest.Blobs)
        {
            if (blob.PrunedAt is not null) continue;
            if (!options.Force && blob.UploadedAt is not null) continue;

            var path = paths.ExportBlobPath(blob.Sha);
            if (!File.Exists(path))
            {
                run.BlobsMissingLocally++;
                continue;
            }

            if (TooLarge(blob.StoredBytes, options.MaxBlobBytes))
            {
                run.Failures.Add(
                    $"blob {Short(blob.Sha)}: {blob.StoredBytes} bytes exceeds the receiver's {options.MaxBlobBytes}-byte limit");
                continue;
            }

            selected.Add(new PendingBlob(blob, path));
        }

        return selected;
    }

    // ── Small helpers ────────────────────────────────────────────────────────

    /// A limit of zero means the receiver never told us one, which is not the
    /// same as "no bytes allowed".
    private static bool TooLarge(long bytes, long limit) => limit > 0 && bytes > limit;

    /// Discovery wins; the conventional mount is the fallback. A discovered base
    /// was already checked to be same-origin with the server the user named, so
    /// there is nothing left to validate here.
    private static string ResolveApiBase(string endpoint, PushOptions options)
        => options.ApiBase is { Length: > 0 } discovered
            ? discovered.TrimEnd('/')
            : endpoint + PushOptions.ConventionalApiPath;

    public static string NormalizeEndpoint(string? endpoint)
    {
        var value = (endpoint ?? "").Trim().TrimEnd('/');
        if (value.Length == 0) return PushOptions.DefaultEndpoint;
        if (!value.Contains("://")) value = "https://" + value;

        return value;
    }

    /// A hashed hostname, so the profile can say "3 machines" without the
    /// hostname itself — often an employer's name — ever leaving the box.
    private static string MachineId()
    {
        var raw = Environment.MachineName + "|" + Environment.UserName;

        return "m_" + CanonicalJson.Hex(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).AsSpan(0, 32).ToString();
    }

    private static string VersionString() =>
        typeof(BrainPushService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static List<T> Take<T>(Queue<T> queue, int count)
    {
        var list = new List<T>(Math.Min(count, queue.Count));
        while (list.Count < count && queue.Count > 0) list.Add(queue.Dequeue());

        return list;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private sealed record PendingChunk(ChunkManifest Chunk, string Path);

    private sealed record PendingBlob(BlobRecord Blob, string Path);

    private sealed class ErrorBody
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }

        /// Present on `quota_exceeded`. The ceiling is per user, so "full" on its
        /// own does not tell you which number you hit.
        [JsonPropertyName("quotaBytes")] public long? QuotaBytes { get; set; }
        [JsonPropertyName("quotaUsedBytes")] public long? QuotaUsedBytes { get; set; }
    }

    private sealed class SyncRequest
    {
        [JsonPropertyName("v")] public int V { get; set; } = 1;
        [JsonPropertyName("client")] public string Client { get; set; } = "";
        [JsonPropertyName("machine")] public string Machine { get; set; } = "";
        [JsonPropertyName("chunks")] public List<SyncChunk> Chunks { get; set; } = new();
        [JsonPropertyName("blobs")] public List<SyncBlob> Blobs { get; set; } = new();
    }

    private sealed class SyncChunk
    {
        [JsonPropertyName("chunkHash")] public string ChunkHash { get; set; } = "";
        [JsonPropertyName("src")] public string Src { get; set; } = "";
        [JsonPropertyName("level")] public string Level { get; set; } = "";
        [JsonPropertyName("day")] public string Day { get; set; } = "";
        [JsonPropertyName("ordinal")] public int Ordinal { get; set; }
        [JsonPropertyName("events")] public int Events { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }
        [JsonPropertyName("uncompressedBytes")] public long UncompressedBytes { get; set; }
        [JsonPropertyName("firstTs")] public string? FirstTs { get; set; }
        [JsonPropertyName("lastTs")] public string? LastTs { get; set; }
        [JsonPropertyName("sessionIds")] public List<string> SessionIds { get; set; } = new();
    }

    private sealed class SyncBlob
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("src")] public string? Src { get; set; }
        [JsonPropertyName("bytes")] public long Bytes { get; set; }

        /// Basename and capture time travel with the blob because the receiver
        /// stores content-addressed bytes and nothing else. Without them a
        /// restore onto a fresh machine could prove a sha exists but not that it
        /// is last Tuesday's opencode tool output — and the machine that knew is
        /// the one being restored.
        [JsonPropertyName("origin")] public string? Origin { get; set; }
        [JsonPropertyName("capturedAt")] public string? CapturedAt { get; set; }
    }
}
