namespace PKS.Infrastructure.Services.Brain;

/// Brings raw source blobs back — the escape hatch that makes the whole scheme
/// trustworthy. Spec: docs/specs/asf/05-blob-backup.md §Restore.
///
/// Restore is the mirror image of push and shares its two convictions: the
/// bytes are content-addressed, so every copy is verifiable and no copy is
/// authoritative; and nothing is claimed that was not proven. A file is only
/// written after its sha256 has been recomputed over the decompressed bytes and
/// matched — a truncated download or a corrupted blob is reported, never
/// silently restored.
public interface IBrainRestoreService
{
    Task<RestoreRun> RunAsync(RestoreOptions options, IRestoreProgress progress, CancellationToken ct = default);
}

public sealed class RestoreOptions
{
    public string Endpoint { get; init; } = PushOptions.DefaultEndpoint;

    /// Where the API is actually mounted, from discovery. Null falls back to the
    /// conventional `<endpoint>/api/brain/v1`. See PushOptions.ApiBase.
    public string? ApiBase { get; init; }

    /// Bearer credential. Only needed with FromRemote; the local catalog needs
    /// no account at all.
    public string Token { get; init; } = "";

    /// Ask the server for the catalog instead of reading this machine's export
    /// manifest. This is the case that matters: the machine asking is usually
    /// the one that lost its manifest.
    public bool FromRemote { get; init; }

    /// Blob kind filter, e.g. `opencode-tool-output`.
    public string? Kind { get; init; }

    /// Only blobs captured at or after this instant.
    public DateTimeOffset? Since { get; init; }

    /// Where restored files are written. Ignored for blobs placed in place.
    public string TargetDir { get; init; } = "";

    /// Write back into the live tool directories. Only honoured for kinds whose
    /// original location can be reconstructed from a basename — see
    /// BrainRestoreService.InPlaceRoot.
    public bool InPlace { get; init; }

    /// Resolve and print what would be written, touching no files.
    public bool DryRun { get; init; }

    /// Replace files that already exist. Off by default: a restore that
    /// clobbers a live transcript is a data-loss event, not a recovery.
    public bool Overwrite { get; init; }

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxAttempts { get; init; } = 5;
}

public sealed class RestoreRun
{
    public string Endpoint { get; set; } = "";
    public DateTime StartedAtUtc { get; init; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;

    /// Where the catalog came from, for the CLI to print.
    public string Catalog { get; set; } = "";

    public int BlobsListed { get; set; }

    /// The server had more than it would return in one page. The filters need
    /// narrowing; silently restoring a prefix would look complete.
    public bool Truncated { get; set; }

    public int Restored { get; set; }

    /// Served from this machine's own blob store instead of the network. Same
    /// bytes by construction — the sha is the identity.
    public int FromLocalStore { get; set; }
    public int Downloaded { get; set; }

    /// Already on disk at the destination and left alone.
    public int SkippedExisting { get; set; }

    /// Listed but not placeable in place. Reported so the count adds up.
    public int SkippedNoLocation { get; set; }

    public int HashMismatches { get; set; }

    public long BytesDownloaded { get; set; }
    public long BytesWritten { get; set; }

    public List<string> Failures { get; } = new();

    /// Destination paths, for the dry run and the summary.
    public List<RestorePlanItem> Plan { get; } = new();
}

/// <param name="Sha">Content address of the original bytes.</param>
/// <param name="Kind">Blob kind.</param>
/// <param name="Destination">Absolute path the bytes would be written to.</param>
/// <param name="Bytes">Uncompressed size as recorded when it was archived.</param>
/// <param name="Local">Whether this machine's blob store already holds it.</param>
public sealed record RestorePlanItem(string Sha, string Kind, string Destination, long Bytes, bool Local);

public interface IRestoreProgress
{
    void Cataloged(int blobs, long bytes, string source);
    void Restoring(string sha, string destination, bool local);
    void Restored(string sha, long bytes);
    void Skipped(string sha, string reason);
}

public sealed class NullRestoreProgress : IRestoreProgress
{
    public static readonly NullRestoreProgress Instance = new();
    public void Cataloged(int blobs, long bytes, string source) { }
    public void Restoring(string sha, string destination, bool local) { }
    public void Restored(string sha, long bytes) { }
    public void Skipped(string sha, string reason) { }
}
