using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public interface IBrainExtractPipeline
{
    /// Enumerate eligible sessions and estimate total cost before any claude
    /// call is made. Cheap (just stat'ing files in the brain layout).
    Task<BrainExtractPlan> PlanAsync(BrainExtractOptions options, CancellationToken ct = default);

    Task<BrainExtractRun> RunAsync(BrainExtractOptions options, IBrainExtractProgress progress, CancellationToken ct = default);
}

public sealed class BrainExtractPlan
{
    public required string ProjectSlug { get; init; }
    public required string ExtractsDir { get; init; }
    public int Eligible { get; init; }
    public int SkippedByCursor { get; init; }
    /// Estimated USD cost if we proceed with the full eligible list. Heuristic when
    /// no prior extracts are present; uses the project's prior sidecars when available.
    public double EstimatedCostUsd { get; init; }
    /// Short human-readable explanation of where the estimate came from.
    public required string EstimateBasis { get; init; }
    /// Estimated wall-clock time at the chosen parallelism. Null if we have no
    /// prior duration data to extrapolate from.
    public TimeSpan? EstimatedDuration { get; init; }
    /// First N eligible session ids (for --dry-run preview).
    public List<string> Preview { get; init; } = new();
}

public sealed class BrainExtractOptions
{
    /// Encoded project slug to filter sessions on. Null = derived from cwd at run-time.
    public string? ProjectSlug { get; init; }

    /// Override path for the brain-extract SKILL.md. Null = use the embedded default
    /// or any user-installed copy.
    public string? SkillPath { get; init; }

    /// Only extract sessions newer than this.
    public DateTime? SinceUtc { get; init; }

    /// Cap the number of sessions extracted in this run.
    public int? Limit { get; init; }

    /// Ignore the "extract already exists and is newer" check.
    public bool Force { get; init; }

    /// Don't invoke claude — just print the list of sessions that would be extracted.
    public bool DryRun { get; init; }

    public string? Model { get; init; }
    public double? MaxBudgetUsd { get; init; }
    public int MaxParallelism { get; init; } = 2;

    /// Summarizer backend: "pks" (built-in in-process agent, default) or "claude"
    /// (shell out to the claude CLI binary).
    public string Agent { get; init; } = "pks";

    /// Route the chosen agent through Azure AI Foundry (the user's Azure quota)
    /// instead of the agent's default Anthropic billing.
    public bool UseFoundry { get; init; }
}

public sealed class BrainExtractRun
{
    public required string RunId { get; set; }
    public required DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
    public int Eligible { get; set; }
    public int SkippedUpToDate { get; set; }
    public int Extracted { get; set; }
    public int Failed { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
    public long TotalCacheCreationTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public List<string> ExtractedSessions { get; set; } = new();
}

/// Per-session result reported back to the progress sink so the UI can
/// aggregate tokens, cost, and ETA live as each extract completes.
public sealed record ExtractFinishedInfo(
    string SessionId,
    bool Success,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    double CostUsd,
    TimeSpan Duration,
    string? ErrorReason);

public interface IBrainExtractProgress
{
    void Discovered(int eligibleSessions);
    void Started(string sessionId);
    void Finished(ExtractFinishedInfo info);
}

public sealed class NullBrainExtractProgress : IBrainExtractProgress
{
    public static readonly NullBrainExtractProgress Instance = new();
    public void Discovered(int eligibleSessions) { }
    public void Started(string sessionId) { }
    public void Finished(ExtractFinishedInfo info) { }
}

/// Sidecar metadata persisted next to each &lt;sessionId&gt;.md as &lt;sessionId&gt;.meta.json.
/// Captures what was extracted, by whom, with what skill, and what it cost.
public sealed class ExtractMetadata
{
    public required string SessionId { get; set; }
    public required DateTime ExtractedAtUtc { get; set; }
    public string? Model { get; set; }
    public required string SkillHash { get; set; }
    public required string SkillSource { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadInputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public double CostUsd { get; set; }
    public long DurationMs { get; set; }
}
