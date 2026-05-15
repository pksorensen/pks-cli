namespace PKS.Infrastructure.Services.Brain.Models;

/// Index of ~/.claude/plans/*.md cross-referenced to the session that produced
/// each plan. Written to ~/.pks-cli/brain/plans.json.
public sealed class PlanIndex
{
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<PlanEntry> Entries { get; set; } = new();
}

public sealed class PlanEntry
{
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public DateTime FileMtimeUtc { get; set; }
    public long FileBytes { get; set; }
    public required string BodyHash { get; set; }
    public string? FirstHeading { get; set; }

    /// "exact" | "probable" | "unresolved"
    public string MatchKind { get; set; } = "unresolved";
    public string? MatchedSessionId { get; set; }
    public string? MatchedProjectSlug { get; set; }
    public string? MatchedToolUseId { get; set; }
    public string? MatchReason { get; set; }
}
