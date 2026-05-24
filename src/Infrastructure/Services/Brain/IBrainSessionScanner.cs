using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.Infrastructure.Services.Brain;

/// <summary>
/// Implements FT-12 (Brain) scan command — Session→ToolCall→File edge discovery.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public interface IBrainSessionScanner
{
    Task<BrainScanResult> ScanAsync(BrainScanOptions options, CancellationToken ct = default);
}

public sealed class BrainScanOptions
{
    public required string TargetPath { get; init; }
    public required string ProjectsDir { get; init; }
    public bool IncludeBash { get; init; }
    public DateTime? SinceUtc { get; init; }
    public bool TargetIsDirectory { get; init; }
}

public sealed class BrainScanResult
{
    public int ScannedJsonls { get; init; }
    public int MatchedSessions { get; init; }
    public List<BrainScanEdge> Edges { get; init; } = new();
}

public sealed class BrainScanEdge
{
    public string SessionId { get; init; } = string.Empty;
    public string JsonlPath { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; }
    public string ToolUseId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string MatchKind { get; init; } = string.Empty;
}
