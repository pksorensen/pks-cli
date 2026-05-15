namespace PKS.Infrastructure.Services.Brain;

public interface IBrainSynthesisPipeline
{
    Task<BrainSynthPlan> PlanAsync(BrainSynthOptions options, CancellationToken ct = default);
    Task<BrainSynthRun> RunAsync(BrainSynthOptions options, IBrainSynthProgress progress, CancellationToken ct = default);
}

public sealed class BrainSynthOptions
{
    public string? Model { get; init; }
    public double? MaxBudgetUsd { get; init; }
    public int MaxParallelism { get; init; } = 10;
    /// Skip AI calls entirely — write just the deterministic cluster JSON.
    public bool NoAi { get; init; }
    /// Cap the number of AI-summarized clusters (others still appear in clusters.json
    /// but without an AI narrative). Habits pass is always one extra call when AI is on.
    public int? MaxClusters { get; init; }
    /// Minimum cluster size to surface in themes.md (singletons usually aren't a theme).
    public int MinClusterSize { get; init; } = 2;
    public bool DryRun { get; init; }
}

public sealed class BrainSynthPlan
{
    public required string ProjectSlug { get; init; }
    public required string SynthesisDir { get; init; }
    public int ExtractsFound { get; init; }
    public int ClustersFound { get; init; }
    public int ClustersAiSummarized { get; init; }
    public int TotalAiCalls { get; init; }
    public double EstimatedCostUsd { get; init; }
    public required string EstimateBasis { get; init; }
    public TimeSpan? EstimatedDuration { get; init; }
    public List<string> TopClusters { get; init; } = new();
}

public sealed class BrainSynthRun
{
    public required string RunId { get; set; }
    public required DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
    public int ExtractsRead { get; set; }
    public int ClustersFound { get; set; }
    public int ClustersWritten { get; set; }
    public int Failed { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
    public long TotalCacheCreationTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public bool ThemesWritten { get; set; }
    public bool HabitsWritten { get; set; }
    public bool ClustersJsonWritten { get; set; }
}

public sealed record SynthFinishedInfo(
    string Stage,
    bool Success,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    double CostUsd,
    TimeSpan Duration,
    string? ErrorReason);

public interface IBrainSynthProgress
{
    void Discovered(int totalCalls);
    void Started(string stage);
    void Finished(SynthFinishedInfo info);
}

public sealed class NullBrainSynthProgress : IBrainSynthProgress
{
    public static readonly NullBrainSynthProgress Instance = new();
    public void Discovered(int totalCalls) { }
    public void Started(string stage) { }
    public void Finished(SynthFinishedInfo info) { }
}

/// One entry of clusters.json — useful input for Phase 4 (wiki/ADR generation).
public sealed class ClusterRecord
{
    public required string Tag { get; set; }
    public required string ThemeName { get; set; }
    public int SessionCount { get; set; }
    public List<string> SessionIds { get; set; } = new();
    public List<string> SessionTitles { get; set; } = new();
    public List<string> RelatedTags { get; set; } = new();
    public List<string> HotFiles { get; set; } = new();
}
