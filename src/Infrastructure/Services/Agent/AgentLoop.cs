using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Agent.Chat;
using PKS.Infrastructure.Services.Agent.Tools;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Provider-neutral multi-turn agent loop. Holds messages, asks the model, executes tool
/// calls, appends results, and loops until the model stops or the max-turns cap is hit.
///
/// Lifetime: one instance per <c>pks agent</c> invocation. Not thread-safe.
/// </summary>
public sealed class AgentLoop
{
    private readonly IChatProvider _provider;
    private readonly string _modelId;
    private readonly AgentToolRegistry _tools;
    private readonly IAgentLoopRenderer _renderer;
    private readonly ILogger<AgentLoop> _logger;
    private readonly int _maxTurns;
    private readonly int _maxOutputTokens;

    public AgentLoop(
        IChatProvider provider,
        string modelId,
        AgentToolRegistry tools,
        IAgentLoopRenderer renderer,
        ILogger<AgentLoop> logger,
        int maxTurns = 50,
        int maxOutputTokens = 32_000)
    {
        _provider = provider;
        _modelId = modelId;
        _tools = tools;
        _renderer = renderer;
        _logger = logger;
        _maxTurns = maxTurns;
        _maxOutputTokens = maxOutputTokens;
    }

    /// <summary>
    /// Run the loop. Returns 0 on a clean stop, 2 on max-turns exceeded,
    /// 3 on max-tokens / content-filter / error finish.
    /// </summary>
    public async Task<int> RunAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User(userPrompt),
        };

        for (int turn = 0; turn < _maxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new ChatRequest(
                Messages: messages.ToList(),
                SystemPrompt: systemPrompt,
                Tools: _tools.All.Select(t => t.Definition).ToList(),
                MaxOutputTokens: _maxOutputTokens);

            _logger.LogDebug("Turn {Turn}: sending {MessageCount} messages, {ToolCount} tools",
                turn + 1, messages.Count, request.Tools.Count);

            var (assistantMessage, finish) = await StreamAndAggregateAsync(request, cancellationToken);
            messages.Add(assistantMessage);

            if (finish == ChatFinishReason.Stop)
            {
                _logger.LogDebug("Model stopped after {Turn} turn(s)", turn + 1);
                return 0;
            }
            if (finish == ChatFinishReason.MaxTokens
                || finish == ChatFinishReason.ContentFilter
                || finish == ChatFinishReason.Error)
            {
                _renderer.RenderError($"Stream ended unexpectedly: {finish}");
                return 3;
            }

            // Tool calls — execute each, append results.
            var toolUses = assistantMessage.Content.OfType<ToolUseBlock>().ToList();
            if (toolUses.Count == 0)
            {
                // Provider said ToolCalls but emitted none — defensive: treat as stop.
                _logger.LogWarning("FinishReason was ToolCalls but no ToolUseBlocks emitted; stopping");
                return 0;
            }

            var resultBlocks = new List<ChatContentBlock>();
            foreach (var call in toolUses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteToolAsync(call, cancellationToken);
                resultBlocks.Add(new ToolResultBlock(call.Id, result.Output, result.IsError));
            }
            messages.Add(new ChatMessage(ChatRole.Tool, resultBlocks));
        }

        _renderer.RenderError($"Max turns ({_maxTurns}) exceeded — stopping.");
        return 2;
    }

    private async Task<ToolResult> ExecuteToolAsync(ToolUseBlock call, CancellationToken ct)
    {
        IAgentTool tool;
        try
        {
            tool = _tools.GetByName(call.Name);
        }
        catch (KeyNotFoundException)
        {
            _renderer.RenderToolCall(call.Name, call.Arguments, isError: true);
            return ToolResult.Error($"unknown tool: {call.Name}");
        }

        _renderer.RenderToolCall(call.Name, call.Arguments, isError: false);
        try
        {
            var result = await tool.ExecuteAsync(call.Arguments, ct);
            _renderer.RenderToolResult(call.Name, result.Output, result.IsError);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Name} threw", call.Name);
            var msg = $"tool {call.Name} threw: {ex.GetType().Name}: {ex.Message}";
            _renderer.RenderToolResult(call.Name, msg, isError: true);
            return ToolResult.Error(msg);
        }
    }

    /// <summary>
    /// Consume the provider's streaming events, render text/tool-call deltas to the user,
    /// and assemble the final assistant message + finish reason.
    /// </summary>
    internal async Task<(ChatMessage assistant, ChatFinishReason finish)> StreamAndAggregateAsync(
        ChatRequest request,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        // tool-call accumulators keyed by tool-use id
        var toolCallOrder = new List<string>();
        var toolCallName = new Dictionary<string, string>();
        var toolCallArgs = new Dictionary<string, StringBuilder>();

        ChatFinishReason finish = ChatFinishReason.Stop;

        await foreach (var ev in _provider.StreamAsync(request, _modelId, cancellationToken))
        {
            switch (ev)
            {
                case TextDeltaEvent t:
                    text.Append(t.Text);
                    _renderer.RenderTextDelta(t.Text);
                    break;
                case ToolUseStartEvent s:
                    toolCallOrder.Add(s.Id);
                    toolCallName[s.Id] = s.Name;
                    toolCallArgs[s.Id] = new StringBuilder();
                    break;
                case ToolUseDeltaEvent d:
                    if (toolCallArgs.TryGetValue(d.Id, out var sb))
                    {
                        sb.Append(d.ArgumentsJsonDelta);
                    }
                    break;
                case ThinkingDeltaEvent:
                    // ignored for v1 (we don't render thinking text)
                    break;
                case MessageStopEvent stop:
                    finish = stop.FinishReason;
                    break;
            }
        }

        // Build the assembled assistant message.
        var blocks = new List<ChatContentBlock>();
        if (text.Length > 0)
        {
            blocks.Add(new TextBlock(text.ToString()));
        }
        foreach (var id in toolCallOrder)
        {
            var name = toolCallName[id];
            var argsJson = toolCallArgs[id].ToString();
            JsonElement args;
            if (string.IsNullOrWhiteSpace(argsJson))
            {
                args = JsonDocument.Parse("{}").RootElement;
            }
            else
            {
                try
                {
                    args = JsonDocument.Parse(argsJson).RootElement;
                }
                catch (JsonException)
                {
                    // Pass through as a string-keyed error; the tool will reject it.
                    args = JsonDocument.Parse("{}").RootElement;
                }
            }
            blocks.Add(new ToolUseBlock(id, name, args));
        }

        return (new ChatMessage(ChatRole.Assistant, blocks), finish);
    }
}

/// <summary>
/// Sink for user-facing output. Stripped to bare hooks so tests can use a no-op
/// renderer and the production renderer can talk to Spectre.Console.
/// </summary>
public interface IAgentLoopRenderer
{
    void RenderTextDelta(string text);
    void RenderToolCall(string toolName, JsonElement arguments, bool isError);
    void RenderToolResult(string toolName, string output, bool isError);
    void RenderError(string message);
}

/// <summary>No-op renderer for tests.</summary>
public sealed class NullAgentLoopRenderer : IAgentLoopRenderer
{
    public void RenderTextDelta(string text) { }
    public void RenderToolCall(string toolName, JsonElement arguments, bool isError) { }
    public void RenderToolResult(string toolName, string output, bool isError) { }
    public void RenderError(string message) { }
}
