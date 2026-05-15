namespace PKS.Infrastructure.Services.Brain;

public interface IBrainWikiPipeline
{
    Task<BrainWikiPlan> PlanAsync(BrainWikiOptions options, CancellationToken ct = default);
    Task<BrainWikiRun> RunAsync(BrainWikiOptions options, IBrainWikiProgress progress, CancellationToken ct = default);
}

public sealed class BrainWikiOptions
{
    public string? Model { get; init; }
    public double? MaxBudgetUsd { get; init; }
    public int MaxParallelism { get; init; } = 10;
    /// Skip AI calls entirely — write just an index of detected clusters.
    public bool NoAi { get; init; }
    /// Cap the number of clusters to AI-render. Others still appear in the index.
    public int? MaxClusters { get; init; }
    /// Minimum cluster size to surface as a wiki page (default 3 — wikis are for real themes).
    public int MinClusterSize { get; init; } = 3;
    public bool DryRun { get; init; }
}

public sealed class BrainWikiPlan
{
    public required string SynthesisDir { get; init; }
    public required string WikiDir { get; init; }
    public int ClustersDetected { get; init; }
    public int ClustersEligible { get; init; }
    public int ClustersToRender { get; init; }
    public double EstimatedCostUsd { get; init; }
    public required string EstimateBasis { get; init; }
    public TimeSpan? EstimatedDuration { get; init; }
    public List<string> TopClusters { get; init; } = new();
}

public sealed class BrainWikiRun
{
    public required string RunId { get; set; }
    public required DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
    public int ClustersRendered { get; set; }
    public int Failed { get; set; }
    public bool IndexWritten { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
    public long TotalCacheCreationTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public List<string> RenderedPages { get; set; } = new();
}

public sealed record WikiFinishedInfo(
    string Tag,
    bool Success,
    string? Model,
    long InputTokens,
    long OutputTokens,
    long CacheReadInputTokens,
    long CacheCreationInputTokens,
    double CostUsd,
    TimeSpan Duration,
    string? ErrorReason);

public interface IBrainWikiProgress
{
    void Discovered(int totalPages);
    void Started(string tag);
    void Finished(WikiFinishedInfo info);
}

public sealed class NullBrainWikiProgress : IBrainWikiProgress
{
    public static readonly NullBrainWikiProgress Instance = new();
    public void Discovered(int totalPages) { }
    public void Started(string tag) { }
    public void Finished(WikiFinishedInfo info) { }
}
