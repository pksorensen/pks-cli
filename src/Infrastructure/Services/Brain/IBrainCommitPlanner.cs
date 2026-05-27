using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Implements FT-12 (Brain) commit-plan command — group uncommitted files by
/// shared session origin to enable focused commits.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public interface IBrainCommitPlanner
{
    Task<BrainCommitPlanResult> PlanAsync(BrainCommitPlanOptions options, CancellationToken ct = default);
}

public sealed class BrainCommitPlanOptions
{
    public required IReadOnlyList<string> Files { get; init; }
    public required string ProjectsDir { get; init; }
    public bool IncludeBash { get; init; }
    public DateTime? SinceUtc { get; init; }
    public int MinFiles { get; init; } = 2;
    public bool IncludePrompts { get; init; }

    /// <summary>
    /// When true (default), the planner runs <c>brain ingest</c> before planning
    /// so the firehose graph is fresh. Set to false to skip refresh (e.g. when
    /// running against a stable fixture in tests, or when the user explicitly
    /// passes <c>--no-refresh</c>).
    /// </summary>
    public bool AutoRefresh { get; init; } = true;

    /// <summary>
    /// When true, force the legacy per-file scanner path even if the firehose
    /// exists. Useful for debugging / fallback verification.
    /// </summary>
    public bool ForceScan { get; init; }
}

public sealed class BrainCommitPlanResult
{
    public List<BrainCommitGroup> Groups { get; init; } = new();
    public List<string> Ungrouped { get; init; } = new();
    public int InputFiles { get; init; }
    public int ScannedSessions { get; init; }
}

public sealed class BrainCommitGroup
{
    public int GroupId { get; init; }
    public List<string> Files { get; init; } = new();
    public string PrimarySession { get; init; } = string.Empty;
    public DateTime LatestTimestampUtc { get; init; }
    public List<BrainCommitContributingSession> ContributingSessions { get; init; } = new();
    /// <summary>Files this session touched that were already assigned to earlier groups: file → group_id.</summary>
    public Dictionary<string, int> SharedFiles { get; init; } = new();
    public List<BrainCommitGroupPrompt> Prompts { get; init; } = new();
}

public sealed class BrainCommitContributingSession
{
    public string SessionId { get; init; } = string.Empty;
    public int FileCount { get; init; }
}

public sealed class BrainCommitGroupPrompt
{
    public DateTime TimestampUtc { get; init; }
    public string Text { get; init; } = string.Empty;
}
