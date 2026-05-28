namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Provider-neutral streaming event. AgentLoop only sees these — each provider
/// adapter translates its native delta shape into this hierarchy.
/// </summary>
public abstract record ChatStreamEvent;

/// <summary>Assistant text delta.</summary>
public sealed record TextDeltaEvent(string Text) : ChatStreamEvent;

/// <summary>A tool-use block has started. Id+Name are known; arguments arrive via ToolUseDelta.</summary>
public sealed record ToolUseStartEvent(string Id, string Name) : ChatStreamEvent;

/// <summary>Partial JSON for an in-flight tool call. Concatenate per Id to assemble arguments.</summary>
public sealed record ToolUseDeltaEvent(string Id, string ArgumentsJsonDelta) : ChatStreamEvent;

/// <summary>The model emitted a reasoning/thinking delta (Anthropic extended thinking).</summary>
public sealed record ThinkingDeltaEvent(string Text) : ChatStreamEvent;

/// <summary>End of stream. Carries the finish reason so AgentLoop knows whether to loop or stop.</summary>
public sealed record MessageStopEvent(ChatFinishReason FinishReason, ChatUsage? Usage) : ChatStreamEvent;

public enum ChatFinishReason
{
    /// <summary>Model finished normally and is yielding to the user.</summary>
    Stop,
    /// <summary>Model wants tool calls executed before continuing.</summary>
    ToolCalls,
    /// <summary>Output truncated by max-tokens.</summary>
    MaxTokens,
    /// <summary>Output filtered by safety system.</summary>
    ContentFilter,
    /// <summary>Provider-level error mid-stream.</summary>
    Error,
}

public sealed record ChatUsage(int InputTokens, int OutputTokens);
