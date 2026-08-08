using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Brain;

/// Sends sealed chunks and raw blobs to a brain endpoint.
///
/// Push is deliberately dumb: it never parses a session, never re-hashes an
/// event and never decides what a chunk contains. Everything it uploads was
/// decided by `brain export`, which means a failed push costs bandwidth and
/// nothing else — re-running it is free because chunk hashes are stable.
///
/// Spec: docs/specs/asf/04-sync-protocol.md.
public interface IBrainPushService
{
    Task<PushRun> RunAsync(PushOptions options, IPushProgress progress, CancellationToken ct = default);
}

public sealed class PushOptions
{
    public const string DefaultEndpoint = "https://agentics.dk";

    /// Scheme + host of the receiver. Identifies the target in the manifest's
    /// upload stamps, and is what the user sees.
    public string Endpoint { get; init; } = DefaultEndpoint;

    /// Where the four upload routes actually live. Discovered from the host's
    /// platform index when it has one, and `<endpoint>/api/brain/v1` when it
    /// does not — a receiver is allowed to mount the API somewhere else, and
    /// hardcoding the path here is what would stop it.
    public string? ApiBase { get; init; }

    /// The conventional mount, used when discovery found nothing.
    public const string ConventionalApiPath = "/api/brain/v1";

    /// Bearer credential — a Keycloak access token or a `bkt_` upload token.
    public string Token { get; init; } = "";

    /// Produces a fresh bearer after a 401. A Keycloak access token lives five
    /// minutes by default and a full push takes longer than that, so without
    /// this the credential dies mid-upload — the one failure that makes signing
    /// in feel worse than pasting a token. Null disables the retry; an upload
    /// token needs no such thing, since it does not expire.
    public Func<CancellationToken, Task<string?>>? Reauthenticate { get; init; }

    /// Only push chunks sealed at this level. Null = every level on disk.
    public string? LevelFilter { get; init; }

    /// Only push chunks from this tool.
    public string? SourceFilter { get; init; }

    /// Resolve what would be sent and print it, without opening a sync session.
    public bool DryRun { get; init; }

    /// Include the raw blob backup. Blobs only travel with `full` data — see
    /// RunAsync — so this is a veto, not a request.
    public bool IncludeBlobs { get; init; } = true;

    /// Re-send chunks the manifest already marks as uploaded. Costs bandwidth,
    /// never correctness: the receiver dedupes on the same hashes.
    public bool Force { get; init; }

    /// Chunks per sync session. The server caps a manifest at 2000; staying well
    /// under it keeps a failed batch small.
    public int BatchSize { get; init; } = DefaultBatchSize;

    public int BlobBatchSize { get; init; } = DefaultBlobBatchSize;

    public const int DefaultBatchSize = 400;
    public const int DefaultBlobBatchSize = 200;

    /// The receiver's own size limits, as it reported them. An artifact over the
    /// limit is dropped before the upload rather than after the 413: the request
    /// would fail anyway, and streaming half a gigabyte to learn that is the
    /// expensive way to find out. Zero means "no known limit".
    public long MaxChunkBytes { get; init; }

    public long MaxBlobBytes { get; init; }

    /// How long to keep polling `GET /projection` after the last commit, waiting
    /// for the receiver to fold what we sent. Zero returns as soon as the bytes
    /// are stored, which is the honest default for a cron job: the data is safe
    /// either way, and the fold is not this machine's business.
    public TimeSpan WaitForProjection { get; init; }

    /// Delay between projection polls.
    public TimeSpan ProjectionPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// First retry delay for 5xx and 429-without-Retry-After; doubles each attempt.
    /// Zero in tests.
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxAttempts { get; init; } = 5;
}

public sealed class PushRun
{
    public string Endpoint { get; set; } = "";
    public DateTime StartedAtUtc { get; init; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;

    /// Set when the manifest pointed at a different endpoint and the upload
    /// stamps were reset. The new server has none of the data, so everything on
    /// disk is offered to it again.
    public bool EndpointChanged { get; set; }

    public int ChunksConsidered { get; set; }

    /// Chunks the server already had — the manifest step answered "known".
    public int ChunksKnown { get; set; }
    public int ChunksUploaded { get; set; }

    /// Chunks whose file is gone from disk. The manifest row is left untouched:
    /// the export can be re-run, and pretending it was sent would be a lie.
    public int ChunksMissingLocally { get; set; }

    /// Chunks that were offered and could not be sent — a hash mismatch, or the
    /// file vanished mid-run. Each one is in Failures too; counted separately so
    /// progress can settle them instead of waiting forever on a chunk that will
    /// never arrive.
    public int ChunksDropped { get; set; }

    public long BytesUploaded { get; set; }

    public int BlobsConsidered { get; set; }
    public int BlobsKnown { get; set; }
    public int BlobsUploaded { get; set; }
    public int BlobsMissingLocally { get; set; }
    public int BlobsDropped { get; set; }
    public long BlobBytesUploaded { get; set; }

    /// Sync sessions opened. More than one batch is normal; more than one per
    /// batch means something was retried.
    public int Syncs { get; set; }

    /// Chunk references the receiver appended to its log.
    public long Queued { get; set; }

    /// Chunk references it already had. Committing twice is free.
    public long QueuedDuplicate { get; set; }

    /// Events those chunks claim to hold. A queue depth from our own manifest —
    /// the receiver's projector decides what actually lands.
    public long QueuedEvents { get; set; }

    /// Chunks the receiver still had to fold when the last commit returned.
    public long Pending { get; set; }

    public long StorageBytes { get; set; }
    public SortedSet<string> Days { get; } = new(StringComparer.Ordinal);
    public List<string> Failures { get; } = new();

    /// Filled in only when the caller waited for the fold (`--wait`). Without it
    /// a push says what it sent, never what the profile now shows.
    public ProjectionStatus? Projection { get; set; }
}

/// Where the run has got to, in absolute terms. Absolute rather than deltas on
/// purpose: a batch that loses a chunk is reopened and re-walked, and a renderer
/// fed increments would count those twice and show a bar past 100%.
///
/// "Settled" is the honest word for the numerator — a chunk the server already
/// had, one we uploaded, and one whose file turned out to be missing are all
/// resolved. Only the last is a problem, and the summary is where problems go.
public readonly record struct PushProgressSnapshot(
    int ChunksSettled,
    int ChunksTotal,
    int BlobsSettled,
    int BlobsTotal,
    long BytesUploaded);

public interface IPushProgress
{
    void Planned(int chunks, int blobs, long bytes);
    void SyncOpened(string syncId, int missingChunks, int missingBlobs);
    void Uploading(string kind, string hash, long bytes);
    void Uploaded(string kind, string hash, bool duplicate);

    /// Called whenever the counters move. The one callback a progress bar needs.
    void Advanced(PushProgressSnapshot snapshot);

    void Committed(CommitResult result);

    /// One poll of the receiver's fold, when the caller asked to wait.
    void Projecting(ProjectionStatus status);
}

public sealed class NullPushProgress : IPushProgress
{
    public static readonly NullPushProgress Instance = new();
    public void Planned(int chunks, int blobs, long bytes) { }
    public void SyncOpened(string syncId, int missingChunks, int missingBlobs) { }
    public void Uploading(string kind, string hash, long bytes) { }
    public void Uploaded(string kind, string hash, bool duplicate) { }
    public void Advanced(PushProgressSnapshot snapshot) { }
    public void Committed(CommitResult result) { }
    public void Projecting(ProjectionStatus status) { }
}

// ── Wire shapes ──────────────────────────────────────────────────────────────

public sealed class SyncResponse
{
    [JsonPropertyName("syncId")] public string SyncId { get; set; } = "";
    [JsonPropertyName("missingChunks")] public List<string> MissingChunks { get; set; } = new();
    [JsonPropertyName("missingBlobs")] public List<string> MissingBlobs { get; set; } = new();
    [JsonPropertyName("knownChunks")] public int KnownChunks { get; set; }
    [JsonPropertyName("expiresAt")] public string? ExpiresAt { get; set; }
}

/// Commit is a pointer flip on the receiver: it appends chunk references to an
/// append-only log and returns, and a background projector folds them into the
/// numbers the profile shows. So every count here is a **queue depth taken from
/// our own manifest**, not a verdict — what actually landed is only known once
/// the projection catches up (`GET /projection`).
public sealed class CommitResult
{
    /// Chunks this commit appended to the log.
    [JsonPropertyName("queued")] public long Queued { get; set; }

    /// Chunks the log already held. A re-push, and a no-op.
    [JsonPropertyName("duplicate")] public long Duplicate { get; set; }

    /// Events those chunks claim to hold, per our manifest.
    [JsonPropertyName("queuedEvents")] public long QueuedEvents { get; set; }

    /// Chunks the receiver has still to fold, ours included.
    [JsonPropertyName("pending")] public long Pending { get; set; }

    [JsonPropertyName("days")] public List<string> Days { get; set; } = new();
    [JsonPropertyName("storageBytes")] public long StorageBytes { get; set; }

    /// The projection run this commit started. Absent when one was already going.
    [JsonPropertyName("runId")] public string? RunId { get; set; }
}

/// `GET /projection` — how far the receiver's fold has got, plus the totals it
/// has actually produced.
public sealed class ProjectionStatus
{
    [JsonPropertyName("logLines")] public long LogLines { get; set; }
    [JsonPropertyName("projected")] public long Projected { get; set; }
    [JsonPropertyName("pending")] public long Pending { get; set; }
    [JsonPropertyName("dirty")] public long Dirty { get; set; }
    [JsonPropertyName("done")] public bool Done { get; set; }
    [JsonPropertyName("events")] public long Events { get; set; }
    [JsonPropertyName("storageBytes")] public long StorageBytes { get; set; }

    /// Secrets the *receiver* masked that this client did not. Non-zero means
    /// the raw value was on the wire — surfaced loudly, not buried in a log.
    [JsonPropertyName("serverMasked")] public long ServerMasked { get; set; }
}

/// A refusal from the receiver that the client is not allowed to paper over.
/// `Code` is the spec's error table (`unauthorized`, `level_exceeded`,
/// `quota_exceeded`, …), so the command can say something useful rather than
/// printing a status number.
public sealed class BrainPushException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// Set on `quota_exceeded`. The ceiling is per user on the receiver, so
    /// "quota exceeded" alone cannot tell the user *which* ceiling they hit —
    /// or whether the grant they were promised has actually been applied.
    public long? QuotaBytes { get; init; }
    public long? QuotaUsedBytes { get; init; }

    /// Whether the whole push should stop. A per-chunk problem is not fatal —
    /// the chunk is dropped from the batch and the rest still lands.
    public bool IsFatal => Status is 401 or 403 or 507 || Code is "unauthorized" or "level_exceeded" or "quota_exceeded";
}
