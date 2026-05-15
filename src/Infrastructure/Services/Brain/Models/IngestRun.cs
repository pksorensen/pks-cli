namespace PKS.Infrastructure.Services.Brain.Models;

/// History of ingest runs. Persisted as the rolling list inside
/// ~/.pks-cli/brain/meta/ingest-runs.json. The per-file cursor on the latest run
/// is what makes reruns O(new lines) instead of O(everything).
public sealed class IngestRunLog
{
    public List<IngestRun> Runs { get; set; } = new();

    /// Per-session-file ingest cursor (sessionId → cursor). Updated incrementally
    /// by the ingest command; survives across runs.
    public Dictionary<string, SessionCursor> SessionCursors { get; set; } = new();
}

public sealed class IngestRun
{
    public required string RunId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;

    public int FilesScanned { get; set; }
    public int FilesIngested { get; set; }
    public int FilesSkippedUpToDate { get; set; }
    public int FilesFailed { get; set; }

    public long PromptsAppended { get; set; }
    public long ToolCallsAppended { get; set; }
    public long FileOpsAppended { get; set; }
    public long ErrorsAppended { get; set; }

    public string? ErrorSummary { get; set; }
}

public sealed class SessionCursor
{
    public required string SessionId { get; set; }
    public required string SourcePath { get; set; }
    public DateTime SourceMtimeUtc { get; set; }
    public long LineCount { get; set; }
    public long Bytes { get; set; }
}
