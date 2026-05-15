using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// Builds the compact JSON context the brain-extract skill consumes for a
/// single session. Streams the firehose files once per session so we don't
/// load 84,000 tool rows into memory.
public interface IBrainExtractContextBuilder
{
    Task<ExtractContext?> BuildAsync(string sessionId, string projectSlug, CancellationToken ct = default);
}

public sealed class ExtractContext
{
    public required SessionMetadata Meta { get; init; }
    public List<PromptRow> Prompts { get; init; } = new();
    public List<TopName> TopTools { get; init; } = new();
    public List<TopName> TopFiles { get; init; } = new();
    public List<ErrorRow> Errors { get; init; } = new();
    public List<PlanBody> Plans { get; init; } = new();
    public List<string> Subagents { get; init; } = new();
    public List<string> Skills { get; init; } = new();
}

public sealed class PlanBody
{
    public required string ToolUseId { get; init; }
    public required string Body { get; init; }
}
