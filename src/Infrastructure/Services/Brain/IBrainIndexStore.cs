using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// Read/write surface for the brain's persisted state. Phase 0 only needs the
/// init-time layout creation and master-index get/set; Phase 1 adds firehose
/// append, per-session writes, and per-project rollup writes.
public interface IBrainIndexStore
{
    /// Idempotent: creates all directories the brain expects under the global
    /// root, the firehose files (touch with zero bytes), and the empty
    /// index.json + meta/ingest-runs.json if they don't already exist.
    Task EnsureGlobalLayoutAsync(CancellationToken ct = default);

    /// Idempotent: creates the per-project synth tree under projectRoot and
    /// adds .pks/brain/ to the nearest .gitignore. Skips both if the project
    /// root is null (called for a non-git cwd).
    Task EnsureProjectLayoutAsync(string? projectRoot, CancellationToken ct = default);

    Task<BrainIndex> LoadIndexAsync(CancellationToken ct = default);

    /// Atomic write: write to ~/.pks-cli/brain/index.json.tmp then File.Move
    /// over the destination.
    Task SaveIndexAsync(BrainIndex index, CancellationToken ct = default);

    /// Append a batch of rows to a firehose file. Serialized per-firehose so
    /// parallel ingest workers don't corrupt the output. One line per row.
    Task AppendFirehoseAsync<T>(BrainFirehose firehose, IReadOnlyList<T> rows, CancellationToken ct = default);

    /// Write per-session metadata to projects/&lt;slug&gt;/sessions/&lt;sessionId&gt;.json
    /// using atomic tmp+rename. Creates the parent directory if missing.
    Task WriteSessionMetadataAsync(SessionMetadata metadata, CancellationToken ct = default);

    /// Write per-project rollup to projects/&lt;slug&gt;/project.json using atomic
    /// tmp+rename. Creates the parent directory if missing.
    Task WriteProjectRollupAsync(ProjectRollup rollup, CancellationToken ct = default);

    /// Walks projects/&lt;slug&gt;/sessions/*.json and aggregates them into a fresh
    /// rollup. Source of truth for project rollups — survives partial reruns.
    Task<ProjectRollup> BuildProjectRollupFromDiskAsync(string slug, CancellationToken ct = default);

    Task<IngestRunLog> LoadIngestRunLogAsync(CancellationToken ct = default);
    Task SaveIngestRunLogAsync(IngestRunLog log, CancellationToken ct = default);

    Task SavePlanIndexAsync(PlanIndex index, CancellationToken ct = default);
}
