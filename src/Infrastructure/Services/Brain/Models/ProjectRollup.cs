namespace PKS.Infrastructure.Services.Brain.Models;

/// Per-project rollup written to
/// ~/.pks-cli/brain/projects/&lt;slug&gt;/project.json.
/// Aggregates a project's sessions into top-N lists the AI step can read
/// without re-traversing the per-session metadata.
public sealed class ProjectRollup
{
    public required string Slug { get; set; }
    public string? Cwd { get; set; }
    public string? RealCwd { get; set; }

    public int SessionCount { get; set; }
    public DateTime? FirstSessionUtc { get; set; }
    public DateTime? LastSessionUtc { get; set; }

    public List<string> Branches { get; set; } = new();
    public List<string> Subagents { get; set; } = new();
    public List<string> Skills { get; set; } = new();

    public List<TopName> TopTools { get; set; } = new();
    public List<TopName> TopFiles { get; set; } = new();
    public List<TopName> TopErrors { get; set; } = new();

    public List<ModelTokenTotals> TokensByModel { get; set; } = new();
    public double EstimatedCostUsd { get; set; }

    /// Deterministic project-kind hint (monorepo | cli | webapp | library | unknown).
    /// AI refines this in Phase 3 — Phase 1 only sets it from cwd heuristics.
    public string Kind { get; set; } = "unknown";
}

public sealed class TopName
{
    public required string Name { get; set; }
    public long Count { get; set; }
}
