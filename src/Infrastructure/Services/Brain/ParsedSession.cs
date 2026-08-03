using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// One session reduced to the rows the local brain index is built from.
///
/// Produced by <see cref="AsfSessionProjector"/> from an ASF event stream, so the
/// filter logic that used to live in a Claude-only parser (ported originally from
/// src/apps/www-site/src/lib/sync-parser.ts) now runs once for all three tools.
public sealed class ParsedSession
{
    public required SessionMetadata Metadata { get; init; }
    public List<PromptRow> Prompts { get; init; } = new();
    public List<ToolCallRow> ToolCalls { get; init; } = new();
    public List<FileOpRow> FileOps { get; init; } = new();
    public List<ErrorRow> Errors { get; init; } = new();
    public List<PlanEvent> PlanEvents { get; init; } = new();
}

public sealed class PlanEvent
{
    public required string SessionId { get; init; }
    public required string ProjectSlug { get; init; }
    public required string ToolUseId { get; init; }
    public required string PlanBody { get; init; }
    public required string PlanHash { get; init; }
    public DateTime TimestampUtc { get; init; }
}
