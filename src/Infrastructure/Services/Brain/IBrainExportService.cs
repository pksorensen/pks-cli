using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Brain.Asf;

namespace PKS.Infrastructure.Services.Brain;

/// Turns the ASF event streams into sealed, content-addressed chunks on disk,
/// plus a raw blob backup of the sources themselves.
///
/// Export is deliberately separate from ingest. Ingest maintains the local index
/// that `brain search`/`synth`/`wiki` read; export produces what leaves the
/// machine. Keeping them apart means the local brain works with no account, no
/// network and no upload token — and that turning the upload off later removes a
/// feature rather than breaking the tool.
///
/// Spec: docs/specs/asf/03-chunks-and-hashing.md, 05-blob-backup.md.
public interface IBrainExportService
{
    Task<ExportRun> RunAsync(ExportOptions options, IExportProgress progress, CancellationToken ct = default);

    /// Reads the export manifest, creating an empty one if this is the first run.
    Task<ExportManifest> LoadManifestAsync(CancellationToken ct = default);

    /// Writes the manifest back atomically. `brain push` owns the upload stamps
    /// on it, which is why persisting is part of the contract rather than an
    /// export-internal detail.
    Task SaveManifestAsync(ExportManifest manifest, CancellationToken ct = default);
}

public sealed class ExportOptions
{
    /// full | prompts | metrics. See docs/specs/asf/02-levels.md.
    public string Level { get; init; } = AsfLevel.Full;

    /// claude | codex | opencode. Null = every installed tool.
    public string? SourceFilter { get; init; }

    public string? ProjectFilter { get; init; }

    /// Only consider sessions whose source changed after this.
    public DateTime? SinceUtc { get; init; }

    public int? Limit { get; init; }

    /// Ignore export cursors and re-emit every event. Ids are unchanged, so the
    /// receiver dedupes — this costs bandwidth, never correctness.
    public bool Force { get; init; }

    /// Archive the raw sources alongside the chunks. On by default: it is the
    /// only copy that survives opencode's 7-day spill sweep.
    public bool IncludeBlobs { get; init; } = true;

    public int SealBytes { get; init; } = AsfChunkWriter.DefaultSealBytes;

    /// How quiet a session must be before its raw form is archived.
    ///
    /// A live Claude transcript grows all day, and a blob is addressed by the
    /// hash of its whole content — so archiving one every run stores a fresh
    /// copy of a half-gigabyte file that is merely a prefix of tomorrow's. Six
    /// hours means the daily job archives yesterday's work and leaves today's
    /// alone. Spec: docs/specs/asf/05-blob-backup.md §Growing files.
    public TimeSpan BlobQuietPeriod { get; init; } = TimeSpan.FromHours(6);

    /// How long a superseded blob stays on disk after its longer successor is
    /// stored. Nothing is deleted before the successor exists locally.
    public TimeSpan BlobSupersededGrace { get; init; } = TimeSpan.FromDays(7);
}

public sealed class ExportRun
{
    public string RunId { get; init; } = "";
    public string Level { get; init; } = "";
    public DateTime StartedAtUtc { get; init; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;

    public int SessionsScanned { get; set; }
    public int SessionsExported { get; set; }
    public int SessionsSkipped { get; set; }
    public int SessionsFailed { get; set; }

    /// Extra copies of an already-discovered session, dropped before export —
    /// typically the same session present both on the host and in a rescued
    /// docker volume. See AgentSessionDedupe.
    public int DuplicateCopiesSkipped { get; set; }

    public long EventsWritten { get; set; }

    /// Events the cursor had already exported at this level or higher. The number
    /// that makes a daily run cheap — it should dwarf EventsWritten in steady state.
    public long EventsSkipped { get; set; }

    public int ChunksSealed { get; set; }
    public long ChunkBytes { get; set; }
    public long ChunkUncompressedBytes { get; set; }

    public int BlobsAdded { get; set; }
    public int BlobsAlreadyPresent { get; set; }
    public long BlobBytes { get; set; }

    /// Sessions still being written, whose blob waits for the next run.
    public int BlobsDeferred { get; set; }

    /// Blobs replaced by a longer version of the same session this run.
    public int BlobsSuperseded { get; set; }

    /// Superseded blobs whose grace period expired and were deleted locally.
    public int BlobsPruned { get; set; }
    public long BlobBytesPruned { get; set; }

    /// Spill files rescued from opencode's tool-output directory this run.
    public int SpillFilesArchived { get; set; }

    public List<string> Failures { get; } = new();
}

public interface IExportProgress
{
    void Discovered(int sessions);
    void Filtered(int eligible, int skipped);
    void Finished(string sessionKey, long eventsWritten, bool failed);
    void Sealing(ChunkManifest chunk);

    /// Before a raw source file is read into the blob store. `bytes` is the size
    /// of the source: without it, a 600 MB transcript looks exactly like a 6 KB
    /// one that hung.
    void ArchivingBlob(string sessionKey, long bytes);

    /// After that write, whether or not it stored anything new.
    void BlobArchived(string sessionKey);

    /// The catch-up pass over sessions whose events shipped on an earlier run but
    /// whose raw bytes never did. On a first `--level all` run this is the
    /// longest phase of the export by an order of magnitude — and it used to run
    /// entirely off-screen, after the only progress bar had reached 100%.
    void ArchivingBacklog(int sessions);

    /// Spill rescue and prune. Bounded work, but it too runs after the last bar
    /// fills, so it needs to say so rather than look like a stall.
    void Finishing();
}

public sealed class NullExportProgress : IExportProgress
{
    public static readonly NullExportProgress Instance = new();
    public void Discovered(int sessions) { }
    public void Filtered(int eligible, int skipped) { }
    public void Finished(string sessionKey, long eventsWritten, bool failed) { }
    public void Sealing(ChunkManifest chunk) { }
    public void ArchivingBlob(string sessionKey, long bytes) { }
    public void BlobArchived(string sessionKey) { }
    public void ArchivingBacklog(int sessions) { }
    public void Finishing() { }
}

/// `~/.pks-cli/brain/export/manifest.json` — the authority on what has been
/// sealed and what has been pushed.
public sealed class ExportManifest
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }

    /// Where this machine last pushed, so `brain push` needs no arguments after
    /// the first run and a second endpoint cannot silently inherit the cursors.
    [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }

    [JsonPropertyName("chunks")] public List<ChunkManifest> Chunks { get; set; } = new();
    [JsonPropertyName("blobs")] public List<BlobRecord> Blobs { get; set; } = new();

    /// Keyed by DiscoveredAgentSession.CursorKey ("<src>:<native id>").
    [JsonPropertyName("cursors")]
    public Dictionary<string, ExportCursor> Cursors { get; set; } = new(StringComparer.Ordinal);
}

/// How far a session has been exported, and at what fidelity.
///
/// `Level` is the half that makes upgrades work. Re-exporting at a higher level
/// resets `NextSeq` to 0 so the whole session goes out again; because ids hash
/// the full content, the receiver enriches the existing rows instead of adding
/// new ones. Re-exporting at a *lower* level does nothing at all — data already
/// sent is not un-sent, and pretending otherwise would be a false promise.
public sealed class ExportCursor
{
    [JsonPropertyName("level")] public string Level { get; set; } = "";

    /// The next event seq to export. Equals the session's event count once fully
    /// exported; append-only sources make this exact rather than approximate.
    [JsonPropertyName("nextSeq")] public int NextSeq { get; set; }

    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("mtimeUtc")] public DateTime MtimeUtc { get; set; }
    [JsonPropertyName("exportedAt")] public DateTimeOffset ExportedAt { get; set; }

    /// sha256 of the raw source, when it has been archived as a blob.
    [JsonPropertyName("blobSha")] public string? BlobSha { get; set; }

    /// Source size the blob was taken at. Compared with the session's current
    /// size to notice that the archived copy is behind — a session whose blob was
    /// deferred while it was live, and which then fell silent forever, would
    /// otherwise never be archived: its event cursor is satisfied from then on,
    /// so nothing would ever look at it again.
    [JsonPropertyName("blobBytes")] public long? BlobBytes { get; set; }
}

public sealed class BlobRecord
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";

    /// claude-transcript | codex-rollout | opencode-session | opencode-tool-output
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("src")] public string? Src { get; set; }

    /// Uncompressed size; the hash is over these bytes.
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("storedBytes")] public long StoredBytes { get; set; }
    [JsonPropertyName("capturedAt")] public DateTimeOffset CapturedAt { get; set; }

    /// Basename only. The full path would leak the machine's directory layout.
    [JsonPropertyName("origin")] public string? Origin { get; set; }

    /// Set when a longer version of the same session was archived. The old blob
    /// is a strict prefix of the new one, so it is kept only long enough to be
    /// sure the successor made it — then pruned.
    [JsonPropertyName("supersededBy")] public string? SupersededBy { get; set; }
    [JsonPropertyName("supersededAt")] public DateTimeOffset? SupersededAt { get; set; }

    /// Set once this blob has been deleted locally. The record stays: it is the
    /// only evidence that the bytes existed and where they went.
    [JsonPropertyName("prunedAt")] public DateTimeOffset? PrunedAt { get; set; }

    [JsonPropertyName("uploadedAt")] public DateTimeOffset? UploadedAt { get; set; }
}
