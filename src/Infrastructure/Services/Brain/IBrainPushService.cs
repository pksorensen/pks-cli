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

    /// Base URL; `/api/brain/v1/...` is appended. Trailing slashes are trimmed.
    public string Endpoint { get; init; } = DefaultEndpoint;

    /// Bearer credential — a Keycloak access token or a `bkt_` upload token.
    public string Token { get; init; } = "";

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
    public int BatchSize { get; init; } = 400;

    public int BlobBatchSize { get; init; } = 200;

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

    public long BytesUploaded { get; set; }

    public int BlobsConsidered { get; set; }
    public int BlobsKnown { get; set; }
    public int BlobsUploaded { get; set; }
    public int BlobsMissingLocally { get; set; }
    public long BlobBytesUploaded { get; set; }

    /// Sync sessions opened. More than one batch is normal; more than one per
    /// batch means something was retried.
    public int Syncs { get; set; }

    public long Accepted { get; set; }
    public long Enriched { get; set; }
    public long Duplicate { get; set; }
    public long Rejected { get; set; }

    /// Secrets the *server* masked that the client did not. Non-zero means the
    /// raw value was on the wire — surfaced loudly, not buried in a log.
    public long Masked { get; set; }

    public long StorageBytes { get; set; }
    public SortedSet<string> Days { get; } = new(StringComparer.Ordinal);
    public List<string> Failures { get; } = new();
}

public interface IPushProgress
{
    void Planned(int chunks, int blobs, long bytes);
    void SyncOpened(string syncId, int missingChunks, int missingBlobs);
    void Uploading(string kind, string hash, long bytes);
    void Uploaded(string kind, string hash, bool duplicate);
    void Committed(CommitResult result);
}

public sealed class NullPushProgress : IPushProgress
{
    public static readonly NullPushProgress Instance = new();
    public void Planned(int chunks, int blobs, long bytes) { }
    public void SyncOpened(string syncId, int missingChunks, int missingBlobs) { }
    public void Uploading(string kind, string hash, long bytes) { }
    public void Uploaded(string kind, string hash, bool duplicate) { }
    public void Committed(CommitResult result) { }
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

public sealed class CommitResult
{
    [JsonPropertyName("accepted")] public long Accepted { get; set; }
    [JsonPropertyName("enriched")] public long Enriched { get; set; }
    [JsonPropertyName("duplicate")] public long Duplicate { get; set; }
    [JsonPropertyName("rejected")] public long Rejected { get; set; }
    [JsonPropertyName("masked")] public long Masked { get; set; }
    [JsonPropertyName("days")] public List<string> Days { get; set; } = new();
    [JsonPropertyName("storageBytes")] public long StorageBytes { get; set; }
}

/// A refusal from the receiver that the client is not allowed to paper over.
/// `Code` is the spec's error table (`unauthorized`, `level_exceeded`,
/// `quota_exceeded`, …), so the command can say something useful rather than
/// printing a status number.
public sealed class BrainPushException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    /// Whether the whole push should stop. A per-chunk problem is not fatal —
    /// the chunk is dropped from the batch and the rest still lands.
    public bool IsFatal => Status is 401 or 403 or 507 || Code is "unauthorized" or "level_exceeded" or "quota_exceeded";
}
