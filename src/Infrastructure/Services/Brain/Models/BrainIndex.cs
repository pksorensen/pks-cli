namespace PKS.Infrastructure.Services.Brain.Models;

/// Master index for the global brain root (~/.pks-cli/brain/index.json).
/// Phase-0 scope: schema version + counts + last ingest pointer. The ingest pass
/// owns the count fields; init only writes a fresh empty index.
public sealed class BrainIndex
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public int ProjectCount { get; set; }
    public int SessionCount { get; set; }
    public long PromptCount { get; set; }
    public long ToolCallCount { get; set; }
    public long FileOpCount { get; set; }
    public long ErrorCount { get; set; }

    public string? LastIngestRunId { get; set; }
    public DateTime? LastIngestAt { get; set; }
    public TimeSpan? LastIngestDuration { get; set; }
}
