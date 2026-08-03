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

            await PushBatchAsync(endpoint, machine, batchChunks, batchBlobs, options, progress, run, ct);
            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            manifest.Endpoint = endpoint;
            await export.SaveManifestAsync(manifest, ct);
        }

        run.FinishedAtUtc = DateTime.UtcNow;

        return run;
    }

    // ── One batch: sync → upload the missing → commit ─────────────────────────

    private async Task PushBatchAsync(
        string endpoint,
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
            var sync = await OpenSyncAsync(endpoint, machine, chunks, blobs, options, ct);
            run.Syncs++;
            progress.SyncOpened(sync.SyncId, sync.MissingChunks.Count, sync.MissingBlobs.Count);
            run.ChunksKnown += chunks.Count - sync.MissingChunks.Count;
            run.BlobsKnown += blobs.Count - sync.MissingBlobs.Count;

            var missingChunks = sync.MissingChunks.ToHashSet(StringComparer.Ordinal);
            var missingBlobs = sync.MissingBlobs.ToHashSet(StringComparer.Ordinal);
            var dropped = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pending in chunks.Where(c => missingChunks.Contains(c.Chunk.ChunkHash)))
            {
                ct.ThrowIfCancellationRequested();
                if (!await UploadAsync("chunk", endpoint, sync.SyncId, pending.Chunk.ChunkHash, pending.Path,
                        options, progress, run, ct))
                {
                    dropped.Add(pending.Chunk.ChunkHash);
                }
            }

            foreach (var pending in blobs.Where(b => missingBlobs.Contains(b.Blob.Sha)))
            {
                ct.ThrowIfCancellationRequested();
                if (!await UploadAsync("blob", endpoint, sync.SyncId, pending.Blob.Sha, pending.Path,
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
                chunks = chunks.Where(c => !dropped.Contains(c.Chunk.ChunkHash)).ToList();
                blobs = blobs.Where(b => !dropped.Contains(b.Blob.Sha)).ToList();
                continue;
            }

            CommitResult result;
            try
            {
                result = await CommitAsync(endpoint, sync.SyncId, options, ct);
            }
            catch (BrainPushException ex) when (ex.Code is "sync_expired" or "chunks_missing" && attempt == 0)
            {
                run.Failures.Add($"commit retried: {ex.Code} — {ex.Message}");
                continue;
            }

            progress.Committed(result);
            run.Accepted += result.Accepted;
            run.Enriched += result.Enriched;
            run.Duplicate += result.Duplicate;
            run.Rejected += result.Rejected;
            run.Masked += result.Masked;
            run.StorageBytes = result.StorageBytes;
            foreach (var day in result.Days) run.Days.Add(day);

            var stamp = DateTimeOffset.UtcNow;
            foreach (var c in chunks) { c.Chunk.UploadedAt = stamp; c.Chunk.SyncId = sync.SyncId; }
            foreach (var b in blobs) b.Blob.UploadedAt = stamp;

            return;
        }
    }

    private async Task<SyncResponse> OpenSyncAsync(
        string endpoint,
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
            }).ToList(),
        };

        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/api/brain/v1/sync")
            {
                Content = JsonContent.Create(body, options: Json),
            },
            options, ct);

        return await ReadAsync<SyncResponse>(response, ct);
    }

    private async Task<CommitResult> CommitAsync(string endpoint, string syncId, PushOptions options, CancellationToken ct)
    {
        var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/api/brain/v1/sync/commit")
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
        string endpoint,
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
                    var request = new HttpRequestMessage(HttpMethod.Put, $"{endpoint}/api/brain/v1/{kind}/{hash}")
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

        return true;
    }

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
            // A non-JSON error body (a proxy's HTML, say) still has a status.
        }
        finally
        {
            response.Dispose();
        }

        return new BrainPushException(status, code, message);
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

            selected.Add(new PendingBlob(blob, path));
        }

        return selected;
    }

    // ── Small helpers ────────────────────────────────────────────────────────

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
    }
}
