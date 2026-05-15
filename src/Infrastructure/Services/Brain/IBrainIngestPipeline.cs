using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public interface IBrainIngestPipeline
{
    Task<IngestRun> RunAsync(IngestOptions options, IIngestProgress progress, CancellationToken ct = default);
}

public sealed class IngestOptions
{
    /// Match against the encoded project-slug substring. Null = every project.
    public string? ProjectFilter { get; init; }

    /// Only ingest sessions whose source file mtime is newer than this.
    public DateTime? SinceUtc { get; init; }

    /// Cap the number of files processed (after filtering). Useful for smoke tests.
    public int? Limit { get; init; }

    /// Ignore the per-session cursor and re-parse every matched file.
    public bool Force { get; init; }

    public int MaxParallelism { get; init; } = Environment.ProcessorCount;
}

public interface IIngestProgress
{
    void Discovered(int totalFiles);
    void Filtered(int eligibleFiles, int skippedByCursor);
    void Started(string file);
    void Finished(string file, bool ingested, bool error);
}

public sealed class NullIngestProgress : IIngestProgress
{
    public static readonly NullIngestProgress Instance = new();
    public void Discovered(int totalFiles) { }
    public void Filtered(int eligibleFiles, int skippedByCursor) { }
    public void Started(string file) { }
    public void Finished(string file, bool ingested, bool error) { }
}
