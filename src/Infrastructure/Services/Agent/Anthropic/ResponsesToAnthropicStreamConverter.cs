using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Stateful converter that turns an Azure OpenAI <b>Responses API</b> streamed event
/// (one parsed <c>data:</c> JSON object) into zero or more Anthropic Messages API SSE
/// frames, so Claude Code — which only speaks the Anthropic streaming protocol — can drive
/// a Codex / GPT-5.x model unchanged.
///
/// Each Responses output item (reasoning, message, function_call) maps to one Anthropic
/// content block, keyed by the Responses <c>output_index</c>. The converter also accumulates
/// the full response so the non-streaming path can synthesise a single Anthropic message via
/// <see cref="BuildFinalMessage"/>.
///
/// Anthropic event sequence produced:
///   message_start -> (content_block_start, content_block_delta*, content_block_stop)* -> message_delta -> message_stop
/// </summary>
public sealed class ResponsesToAnthropicStreamConverter
{
    private readonly string _model;
    private readonly int _inputTokensEstimate;
    private readonly bool _emitThinking;

    private string _messageId = "msg_" + Guid.NewGuid().ToString("N");
    private bool _messageStarted;
    private bool _messageStopped;
    private int _nextIndex;
    private int _outputTokens;
    private int _inputTokens;
    private bool _sawToolCall;
    private string _stopReason = "end_turn";

    // output_index -> open block
    private readonly Dictionary<int, Block> _blocks = new();

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

    public ResponsesToAnthropicStreamConverter(string model, int inputTokensEstimate, bool emitThinking = true)
    {
        _model = model;
        _inputTokensEstimate = inputTokensEstimate;
        _emitThinking = emitThinking;
    }

    /// <summary>Process one parsed Responses event; returns Anthropic SSE frames to write (may be empty).</summary>
    public IEnumerable<string> Handle(JsonElement evt)
    {
        var type = evt.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is null) yield break;

        switch (type)
        {
            case "response.created":
            case "response.in_progress":
                foreach (var f in EnsureStarted(evt)) yield return f;
                break;

            case "response.output_item.added":
                foreach (var f in OnItemAdded(evt)) yield return f;
                break;

            case "response.output_text.delta":
                foreach (var f in OnDelta(evt, "text", "text_delta")) yield return f;
                break;

            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                if (_emitThinking)
                    foreach (var f in OnDelta(evt, "thinking", "thinking_delta")) yield return f;
                break;

            case "response.function_call_arguments.delta":
                foreach (var f in OnDelta(evt, "tool_use", "input_json_delta")) yield return f;
                break;

            case "response.output_item.done":
                foreach (var f in OnItemDone(evt)) yield return f;
                break;

            case "response.completed":
                _stopReason = _sawToolCall ? "tool_use" : "end_turn";
                ReadUsage(evt);
                foreach (var f in Finish()) yield return f;
                break;

            case "response.incomplete":
                _stopReason = "max_tokens";
                ReadUsage(evt);
                foreach (var f in Finish()) yield return f;
                break;

            case "response.failed":
            case "error":
                foreach (var f in OnError(evt)) yield return f;
                break;
        }
    }

    private IEnumerable<string> EnsureStarted(JsonElement evt)
    {
        if (_messageStarted) yield break;
        _messageStarted = true;

        if (evt.TryGetProperty("response", out var resp) &&
            resp.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var rid = id.GetString();
            if (!string.IsNullOrEmpty(rid)) _messageId = rid!;
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

    private IEnumerable<string> OnItemAdded(JsonElement evt)
    {
        foreach (var f in EnsureStarted(evt)) yield return f;

        if (!evt.TryGetProperty("item", out var item)) yield break;
        var oi = OutputIndex(evt);
        var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

        switch (itemType)
        {
            case "function_call":
            {
                var callId = item.TryGetProperty("call_id", out var c) ? c.GetString()
                    : item.TryGetProperty("id", out var i) ? i.GetString() : null;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var block = OpenBlock(oi, "tool_use");
                block.ToolId = callId ?? "call_" + oi;
                block.ToolName = name ?? string.Empty;
                _sawToolCall = true;
                yield return Frame("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = block.Index,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.ToolId,
                        ["name"] = block.ToolName,
                        ["input"] = new JsonObject(),
                    },
                });
                break;
            }
            // reasoning / message blocks are opened lazily on their first delta.
        }
    }

    /// <param name="blockType">text | thinking | tool_use</param>
    /// <param name="deltaType">text_delta | thinking_delta | input_json_delta</param>
    private IEnumerable<string> OnDelta(JsonElement evt, string blockType, string deltaType)
    {
        foreach (var f in EnsureStarted(evt)) yield return f;

        var delta = evt.TryGetProperty("delta", out var d) ? d.GetString() ?? string.Empty : string.Empty;
        if (delta.Length == 0) yield break;

        var oi = OutputIndex(evt);
        var (block, startFrames) = EnsureBlock(oi, blockType);
        foreach (var f in startFrames) yield return f;

        if (blockType == "tool_use") block.Args.Append(delta);
        else block.Text.Append(delta);

        var deltaNode = blockType == "tool_use"
            ? new JsonObject { ["type"] = deltaType, ["partial_json"] = delta }
            : new JsonObject { ["type"] = deltaType, [blockType] = delta };

        yield return Frame("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta",
            ["index"] = block.Index,
            ["delta"] = deltaNode,
        });
    }

    private IEnumerable<string> OnItemDone(JsonElement evt)
    {
        var oi = OutputIndex(evt);
        if (_blocks.TryGetValue(oi, out var block) && !block.Closed)
        {
            block.Closed = true;
            yield return Frame("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = block.Index,
            });
        }
    }

    private IEnumerable<string> Finish()
    {
        if (_messageStopped) yield break;
        foreach (var f in EnsureStarted(default)) yield return f;

        // Close any blocks still open (out-of-order / missing item.done events).
        foreach (var block in _blocks.Values.Where(b => !b.Closed).OrderBy(b => b.Index))
        {
            block.Closed = true;
            yield return Frame("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = block.Index,
            });
        }

        _messageStopped = true;
        yield return Frame("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = _stopReason, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = _outputTokens },
        });
        yield return Frame("message_stop", new JsonObject { ["type"] = "message_stop" });
    }

    private IEnumerable<string> OnError(JsonElement evt)
    {
        string message = "upstream error";
        if (evt.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String) message = err.GetString() ?? message;
            else if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m))
                message = m.GetString() ?? message;
        }
        else if (evt.TryGetProperty("message", out var m2))
        {
            message = m2.GetString() ?? message;
        }

        yield return Frame("error", new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject { ["type"] = "api_error", ["message"] = message },
        });
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

    private (Block block, IEnumerable<string> frames) EnsureBlock(int oi, string type)
    {
        if (_blocks.TryGetValue(oi, out var existing))
        {
            return (existing, Array.Empty<string>());
        }

        var block = OpenBlock(oi, type);
        var contentBlock = type == "thinking"
            ? new JsonObject { ["type"] = "thinking", ["thinking"] = "" }
            : new JsonObject { ["type"] = "text", ["text"] = "" };

        var frame = Frame("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start",
            ["index"] = block.Index,
            ["content_block"] = contentBlock,
        });
        return (block, new[] { frame });
    }

    private Block OpenBlock(int oi, string type)
    {
        if (_blocks.TryGetValue(oi, out var existing)) return existing;
        var block = new Block { Index = _nextIndex++, Type = type };
        _blocks[oi] = block;
        return block;
    }

    private void ReadUsage(JsonElement evt)
    {
        if (!evt.TryGetProperty("response", out var resp) ||
            !resp.TryGetProperty("usage", out var usage)) return;

        if (usage.TryGetProperty("input_tokens", out var inp) && inp.ValueKind == JsonValueKind.Number)
            _inputTokens = inp.GetInt32();
        if (usage.TryGetProperty("output_tokens", out var outp) && outp.ValueKind == JsonValueKind.Number)
            _outputTokens = outp.GetInt32();
    }

    private static int OutputIndex(JsonElement evt)
    {
        if (evt.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number)
            return oi.GetInt32();
        // Fall back to a stable hash of item_id when output_index is absent.
        if (evt.TryGetProperty("item_id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString()!.GetHashCode() & 0x7fffffff;
        return 0;
    }

    private static string Frame(string eventName, JsonObject data)
    {
        var json = data.ToJsonString();
        return $"event: {eventName}\ndata: {json}\n\n";
    }
}
