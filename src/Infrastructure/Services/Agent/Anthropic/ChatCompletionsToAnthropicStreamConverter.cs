using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Stateful converter that turns an OpenAI <b>Chat Completions</b> streamed chunk (one parsed
/// <c>data:</c> JSON object) into zero or more Anthropic Messages API SSE frames, so Claude Code —
/// which only speaks the Anthropic streaming protocol — can drive a Scaleway serverless model
/// unchanged. The Chat-Completions sibling of <see cref="ResponsesToAnthropicStreamConverter"/>.
///
/// Chat Completions streams a single <c>choices[0].delta</c> per chunk; we map:
///   delta.reasoning_content -> Anthropic thinking block
///   delta.content           -> Anthropic text block
///   delta.tool_calls[i]     -> Anthropic tool_use block (keyed by the OpenAI tool index)
/// Blocks are opened lazily on first delta and closed when the active block changes. Usage often
/// arrives on a trailing chunk after <c>finish_reason</c>, so the caller must invoke <see cref="Flush"/>
/// once the upstream stream ends to emit the final <c>message_delta</c>/<c>message_stop</c>.
///
/// Anthropic event sequence produced:
///   message_start -> (content_block_start, content_block_delta*, content_block_stop)* -> message_delta -> message_stop
/// </summary>
public sealed class ChatCompletionsToAnthropicStreamConverter
{
    private readonly string _model;
    private readonly int _inputTokensEstimate;
    private readonly bool _emitThinking;

    private string _messageId = "msg_" + Guid.NewGuid().ToString("N");
    private bool _messageStarted;
    private bool _messageStopped;
    private bool _finishSeen;
    private int _nextIndex;
    private int _inputTokens;
    private int _outputTokens;
    private bool _sawToolCall;
    private string _stopReason = "end_turn";

    private string? _openKey;                                  // key of the currently open (started, not stopped) block
    private readonly Dictionary<string, Block> _blocks = new();  // logical key -> block

    private sealed class Block
    {
        public int Index;
        public string Type = "text"; // text | tool_use | thinking
        public string? ToolId;
        public string? ToolName;
        public readonly StringBuilder Text = new();   // text or thinking content
        public readonly StringBuilder Args = new();    // tool_use arguments json
        public bool Closed;
    }

    public ChatCompletionsToAnthropicStreamConverter(string model, int inputTokensEstimate, bool emitThinking = true)
    {
        _model = model;
        _inputTokensEstimate = inputTokensEstimate;
        _emitThinking = emitThinking;
    }

    /// <summary>Process one parsed Chat Completions chunk; returns Anthropic SSE frames to write (may be empty).</summary>
    public IEnumerable<string> Handle(JsonElement chunk)
    {
        foreach (var f in EnsureStarted(chunk)) yield return f;

        ReadUsage(chunk);

        if (chunk.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                // Reasoning (thinking) — some serverless models stream chain-of-thought here.
                if (_emitThinking &&
                    delta.TryGetProperty("reasoning_content", out var rc) &&
                    rc.ValueKind == JsonValueKind.String && rc.GetString() is { Length: > 0 } reasoning)
                {
                    foreach (var f in OnTextLike("thinking", "thinking", "thinking_delta", reasoning)) yield return f;
                }

                // Assistant text.
                if (delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    foreach (var f in OnTextLike("text", "text", "text_delta", text)) yield return f;
                }

                // Tool calls.
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        foreach (var f in OnToolCall(tc)) yield return f;
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                _finishSeen = true;
                _stopReason = MapFinishReason(fr.GetString());
            }
        }
    }

    /// <summary>Emit the terminating frames once the upstream stream has ended.</summary>
    public IEnumerable<string> Flush() => Finish();

    private IEnumerable<string> EnsureStarted(JsonElement chunk)
    {
        if (_messageStarted) yield break;
        _messageStarted = true;

        if (chunk.ValueKind == JsonValueKind.Object &&
            chunk.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
            id.GetString() is { Length: > 0 } cid)
        {
            _messageId = cid;
        }

        _inputTokens = _inputTokensEstimate;

        var message = new JsonObject
        {
            ["id"] = _messageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = _model,
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = _inputTokens, ["output_tokens"] = 0 },
        };
        yield return Frame("message_start", new JsonObject { ["type"] = "message_start", ["message"] = message });
    }

    /// <param name="key">logical block key</param>
    /// <param name="blockType">text | thinking</param>
    /// <param name="deltaType">text_delta | thinking_delta</param>
    private IEnumerable<string> OnTextLike(string key, string blockType, string deltaType, string delta)
    {
        var (block, frames) = EnsureOpen(key, blockType);
        foreach (var f in frames) yield return f;

        block.Text.Append(delta);
        yield return Frame("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = block.Index,
            ["delta"] = new JsonObject { ["type"] = deltaType, [blockType] = delta },
        });
    }

    private IEnumerable<string> OnToolCall(JsonElement tc)
    {
        if (tc.ValueKind != JsonValueKind.Object) yield break;

        var idx = tc.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0;
        var key = "tool:" + idx;

        // First sighting of this tool index carries id + name — capture before opening so the
        // content_block_start frame reports them.
        string? callId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } c
            ? c : null;
        var fn = tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object ? fnEl : default;
        string? name = fn.ValueKind == JsonValueKind.Object &&
            fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } nm
            ? nm : null;

        var (block, frames) = EnsureOpen(key, "tool_use", callId, name);
        block.ToolId ??= callId;
        block.ToolName ??= name;

        foreach (var f in frames) yield return f;

        // Argument fragments stream incrementally.
        if (fn.ValueKind == JsonValueKind.Object &&
            fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String &&
            args.GetString() is { Length: > 0 } argChunk)
        {
            block.Args.Append(argChunk);
            yield return Frame("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta",
                ["index"] = block.Index,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = argChunk },
            });
        }
    }

    /// <summary>Open <paramref name="key"/> (closing whatever block is currently open). Returns start frames if newly opened.</summary>
    private (Block block, IEnumerable<string> frames) EnsureOpen(string key, string type, string? toolId = null, string? toolName = null)
    {
        var frames = new List<string>();

        if (_openKey == key && _blocks.TryGetValue(key, out var current))
        {
            return (current, frames);
        }

        // Close the previously-open block before starting/resuming another.
        if (_openKey is not null && _blocks.TryGetValue(_openKey, out var prev) && !prev.Closed)
        {
            prev.Closed = true;
            frames.Add(Frame("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = prev.Index }));
        }

        if (!_blocks.TryGetValue(key, out var block))
        {
            block = new Block { Index = _nextIndex++, Type = type, ToolId = toolId, ToolName = toolName };
            _blocks[key] = block;
            if (type == "tool_use") _sawToolCall = true;

            JsonObject contentBlock = type switch
            {
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
                "tool_use" => new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = block.ToolId ?? key,
                    ["name"] = block.ToolName ?? string.Empty,
                    ["input"] = new JsonObject(),
                },
                _ => new JsonObject { ["type"] = "text", ["text"] = "" },
            };
            frames.Add(Frame("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start",
                ["index"] = block.Index,
                ["content_block"] = contentBlock,
            }));
        }

        _openKey = key;
        return (block, frames);
    }

    private IEnumerable<string> Finish()
    {
        if (_messageStopped) yield break;
        foreach (var f in EnsureStarted(default)) yield return f;

        foreach (var block in _blocks.Values.Where(b => !b.Closed).OrderBy(b => b.Index))
        {
            block.Closed = true;
            yield return Frame("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = block.Index });
        }

        _messageStopped = true;
        yield return Frame("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = _sawToolCall ? "tool_use" : _stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = _outputTokens },
        });
        yield return Frame("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    /// <summary>Builds the single non-streaming Anthropic message body from accumulated state.</summary>
    public JsonObject BuildFinalMessage()
    {
        var content = new JsonArray();
        foreach (var block in _blocks.Values.OrderBy(b => b.Index))
        {
            switch (block.Type)
            {
                case "text":
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text.ToString() });
                    break;
                case "thinking":
                    if (_emitThinking)
                        content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = block.Text.ToString() });
                    break;
                case "tool_use":
                    var argsText = block.Args.Length > 0 ? block.Args.ToString() : "{}";
                    JsonNode input;
                    try { input = JsonNode.Parse(argsText) ?? new JsonObject(); }
                    catch { input = new JsonObject(); }
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.ToolId,
                        ["name"] = block.ToolName,
                        ["input"] = input,
                    });
                    break;
            }
        }

        return new JsonObject
        {
            ["id"] = _messageId,
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = _model,
            ["content"] = content,
            ["stop_reason"] = _sawToolCall ? "tool_use" : _stopReason,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = _inputTokens > 0 ? _inputTokens : _inputTokensEstimate,
                ["output_tokens"] = _outputTokens,
            },
        };
    }

    private void ReadUsage(JsonElement chunk)
    {
        if (chunk.ValueKind != JsonValueKind.Object ||
            !chunk.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("prompt_tokens", out var inp) && inp.ValueKind == JsonValueKind.Number)
            _inputTokens = inp.GetInt32();
        if (usage.TryGetProperty("completion_tokens", out var outp) && outp.ValueKind == JsonValueKind.Number)
            _outputTokens = outp.GetInt32();
    }

    private static string MapFinishReason(string? finishReason) => finishReason switch
    {
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        "function_call" => "tool_use",
        _ => "end_turn",
    };

    private static string Frame(string eventName, JsonObject data)
    {
        var json = data.ToJsonString();
        return $"event: {eventName}\ndata: {json}\n\n";
    }
}
