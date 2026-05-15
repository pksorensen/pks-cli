namespace PKS.Infrastructure.Services.Brain.Models;

/// One row per real user prompt across every session.
/// Written append-only to ~/.pks-cli/brain/prompts.jsonl.
/// "Real" = matches the filter from sync-parser.ts:123-154
/// (text-array content, not isMeta, not [Request interrupted, not tool_result-only).
public sealed class PromptRow
{
    public required string SessionId { get; set; }
    public required string ProjectSlug { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string? PromptId { get; set; }
    public required string Uuid { get; set; }
    public required string Text { get; set; }
    public required string TextHash { get; set; }
    public string? Cwd { get; set; }
    public string? GitBranch { get; set; }
    public int Length { get; set; }
    public bool IsSlash { get; set; }
    public string? SlashCommand { get; set; }
    public string? SlashArgs { get; set; }
}

/// One row per assistant tool_use across every session.
/// Written append-only to ~/.pks-cli/brain/tools.jsonl.
/// Phase 1 deterministic — durationMs is filled in when the matching tool_result
/// is seen; isError comes from that tool_result.
public sealed class ToolCallRow
{
    public required string SessionId { get; set; }
    public required string ProjectSlug { get; set; }
    public DateTime TimestampUtc { get; set; }
    public required string ToolName { get; set; }
    public required string ToolUseId { get; set; }
    public string? InputDigest { get; set; }
    public string? InputPreview { get; set; }
    public string? ParentAssistantUuid { get; set; }
    public long? DurationMs { get; set; }
    public bool IsError { get; set; }
    public long? ResultSize { get; set; }
    public bool IsMcp { get; set; }
    public bool IsSubagent { get; set; }
    public string? SubagentType { get; set; }
}

/// Derived from Read / Edit / Write / MultiEdit / NotebookEdit tool calls.
/// Written append-only to ~/.pks-cli/brain/files.jsonl.
public sealed class FileOpRow
{
    public required string SessionId { get; set; }
    public required string ProjectSlug { get; set; }
    public DateTime TimestampUtc { get; set; }
    public required string Op { get; set; } // read | write | edit | multi-edit | notebook-edit
    public required string FilePath { get; set; }
    public bool Success { get; set; }
}

/// One row per tool_result with is_error=true, plus retry-loop and p95-bottleneck
/// markers. Written append-only to ~/.pks-cli/brain/errors.jsonl.
public sealed class ErrorRow
{
    public required string SessionId { get; set; }
    public required string ProjectSlug { get; set; }
    public DateTime TimestampUtc { get; set; }
    /// "error" | "retry-loop" | "bottleneck-p95" | "interruption"
    public required string Kind { get; set; }
    public string? ToolName { get; set; }
    public string? ToolUseId { get; set; }
    public string? Snippet { get; set; }
    public string? InputDigest { get; set; }
    public long? DurationMs { get; set; }
}
