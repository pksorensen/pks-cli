using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// Deterministic streaming parser for a single Claude session JSONL file.
/// Ports the canonical filter logic from src/apps/www-site/src/lib/sync-parser.ts:62-213
/// to C#. No AI, no token spend. One pass per file; tool_use ↔ tool_result
/// matching is done in-memory per session.
public interface ISessionParser
{
    Task<ParsedSession> ParseAsync(string filePath, string projectSlug, CancellationToken ct = default);
}

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
