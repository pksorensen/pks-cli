namespace PKS.Infrastructure.Services.Brain.Models;

/// Per-session deterministic extract written to
/// ~/.pks-cli/brain/projects/&lt;slug&gt;/sessions/&lt;sessionId&gt;.json.
/// One row per Claude session JSONL file. No AI involved — all fields derived
/// by scanning the JSONL once.
public sealed class SessionMetadata
{
    public required string SessionId { get; set; }
    public string? ClaudeSessionId { get; set; }
    public required string ProjectSlug { get; set; }

    public required string SourcePath { get; set; }
    public DateTime SourceMtimeUtc { get; set; }
    public long SourceBytes { get; set; }
    public long LineCount { get; set; }

    public DateTime? FirstTimestampUtc { get; set; }
    public DateTime? LastTimestampUtc { get; set; }

    public string? Cwd { get; set; }
    public string? RealCwd { get; set; }
    public List<string> GitBranches { get; set; } = new();
    public List<string> Models { get; set; } = new();

    public int PromptCount { get; set; }
    public int AssistantTurnCount { get; set; }
    public int ToolCallCount { get; set; }
    public int ToolErrorCount { get; set; }
    public int ThinkingBlockCount { get; set; }
    public int FileOpCount { get; set; }
    public int SubagentInvocationCount { get; set; }
    public int PlanEventCount { get; set; }
    public int InterruptionCount { get; set; }

    public List<ModelTokenTotals> TokensByModel { get; set; } = new();
    public double EstimatedCostUsd { get; set; }

    /// Per-session top-N rollups. These are the source of truth for the
    /// project-level rollup; aggregating these from disk lets reruns always
    /// produce correct project.json output, even if only a subset of sessions
    /// was re-parsed in this run.
    public List<TopName> TopTools { get; set; } = new();
    public List<TopName> TopFiles { get; set; } = new();
    public List<TopName> TopErrors { get; set; } = new();
    public List<string> Skills { get; set; } = new();
    public List<string> Subagents { get; set; } = new();
}

public sealed class ModelTokenTotals
{
    public required string Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadInputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public double EstimatedCostUsd { get; set; }
}
