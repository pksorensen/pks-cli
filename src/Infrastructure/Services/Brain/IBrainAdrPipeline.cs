namespace PKS.Infrastructure.Services.Brain;

public interface IBrainAdrPipeline
{
    Task<BrainAdrPlan> PlanAsync(BrainAdrOptions options, CancellationToken ct = default);
    Task<BrainAdrRun> RunAsync(BrainAdrOptions options, IBrainAdrProgress progress, CancellationToken ct = default);
}

public sealed class BrainAdrOptions
{
    public string? Model { get; init; }
    public double? MaxBudgetUsd { get; init; }
    public int MaxParallelism { get; init; } = 10;
    public bool NoAi { get; init; }
    public int? MaxAdrs { get; init; }
    /// Decisions need more evidence than wiki pages — default min cluster size is higher.
    public int MinClusterSize { get; init; } = 5;
    /// Extra tags to count as architectural (added to the default allowlist).
    public List<string> IncludeTags { get; init; } = new();
    /// If provided, REPLACES the default allowlist entirely.
    public List<string>? Tags { get; init; }
    public bool DryRun { get; init; }
}

public sealed class BrainAdrPlan
{
    public required string AdrDir { get; init; }
    public int ClustersDetected { get; init; }
    public int ClustersEligible { get; init; }
    public int ClustersToRender { get; init; }
    public double EstimatedCostUsd { get; init; }
    public required string EstimateBasis { get; init; }
    public TimeSpan? EstimatedDuration { get; init; }
    public List<string> Candidates { get; init; } = new();
    public List<string> AllowedTags { get; init; } = new();
}

public sealed class BrainAdrRun
{
    public required string RunId { get; set; }
    public required DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public TimeSpan Duration => FinishedAtUtc - StartedAtUtc;
    public int AdrsWritten { get; set; }
    public int Failed { get; set; }
    public bool IndexWritten { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
    public long TotalCacheCreationTokens { get; set; }
    public double TotalCostUsd { get; set; }
    public List<string> Tags { get; set; } = new();
}

public sealed record AdrFinishedInfo(
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

public interface IBrainAdrProgress
{
    void Discovered(int totalCalls);
    void Started(string tag);
    void Finished(AdrFinishedInfo info);
}

public sealed class NullBrainAdrProgress : IBrainAdrProgress
{
    public static readonly NullBrainAdrProgress Instance = new();
    public void Discovered(int totalCalls) { }
    public void Started(string tag) { }
    public void Finished(AdrFinishedInfo info) { }
}
