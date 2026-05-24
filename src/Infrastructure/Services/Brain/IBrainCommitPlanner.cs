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
}

public sealed class BrainCommitContributingSession
{
    public string SessionId { get; init; } = string.Empty;
    public int FileCount { get; init; }
}
