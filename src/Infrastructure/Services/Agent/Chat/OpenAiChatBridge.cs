using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Bridges chat-llm:v1's OpenAI-compatible chat-completions wire contract
/// (external/alp-spec/2026-03-30-draft/spec/13-chat.md, Kind B) to pks-cli's provider-neutral
/// ChatRequest/ChatStreamEvent abstraction (IChatProvider.cs) — the same abstraction
/// AgentChatProviderFactory/CodingAgentService already use for `pks agent`. This lets a chat-llm:v1
/// Job be served by any backend AgentChatProviderFactory already knows how to reach (a Foundry
/// session from `pks foundry init`, a stored Anthropic/Azure OpenAI key) instead of requiring a
/// manually-configured --chat-llm-backend-url pointed at a literal OpenAI-compatible HTTP endpoint.
/// </summary>
public static class OpenAiChatBridge
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Parses an inbound OpenAI chat-completions request body into a provider-neutral ChatRequest.</summary>
    public static ChatRequest ParseRequest(JsonElement body)
    {
        var systemParts = new List<string>();
        var messages = new List<ChatMessage>();

        if (body.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in msgs.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                if (role == "system")
                {
                    var text = ExtractText(msg);
                    if (!string.IsNullOrEmpty(text)) systemParts.Add(text);
                    continue;
                }
                messages.Add(ConvertMessage(role, msg));
            }
        }

        var tools = new List<ChatToolDefinition>();
        if (body.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (!tool.TryGetProperty("function", out var fn)) continue;
                var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;
                var desc = fn.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var schema = fn.TryGetProperty("parameters", out var p) ? p.Clone() : EmptyObject;
                tools.Add(new ChatToolDefinition(name, desc, schema));
            }
        }

        var maxTokens = 4096;
        if (body.TryGetProperty("max_completion_tokens", out var mct) && mct.ValueKind == JsonValueKind.Number && mct.TryGetInt32(out var v1))
            maxTokens = v1;
        else if (body.TryGetProperty("max_tokens", out var mt) && mt.ValueKind == JsonValueKind.Number && mt.TryGetInt32(out var v2))
            maxTokens = v2;

        return new ChatRequest(messages, string.Join("\n", systemParts), tools, maxTokens);
    }

    private static ChatMessage ConvertMessage(string role, JsonElement msg)
    {
        if (role == "assistant")
        {
            var blocks = new List<ChatContentBlock>();
            var text = ExtractText(msg);
            if (!string.IsNullOrEmpty(text)) blocks.Add(new TextBlock(text));

            if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    if (!tc.TryGetProperty("function", out var fn)) continue;
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = fn.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    var argsJson = fn.TryGetProperty("arguments", out var a) ? a.GetString() : null;
                    JsonElement args;
                    try
                    {
                        args = string.IsNullOrWhiteSpace(argsJson)
                            ? EmptyObject
                            : JsonDocument.Parse(argsJson).RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        args = EmptyObject;
                    }
                    blocks.Add(new ToolUseBlock(id, name, args));
                }
            }

            return new ChatMessage(ChatRole.Assistant, blocks);
        }

        if (role == "tool")
        {
            var toolCallId = msg.TryGetProperty("tool_call_id", out var idEl) ? idEl.GetString() ?? "" : "";
            var content = ExtractText(msg);
            return new ChatMessage(ChatRole.Tool, new ChatContentBlock[] { new ToolResultBlock(toolCallId, content, IsError: false) });
        }

        // "user" and anything else not otherwise recognized.
        return new ChatMessage(ChatRole.User, new ChatContentBlock[] { new TextBlock(ExtractText(msg)) });
    }

    /// <summary>content may be a plain string, or an array of {type:"text", text} parts (image parts
    /// are intentionally not carried into ChatRequest — out of scope for the first working version).</summary>
    private static string ExtractText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    part.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static string MapFinishReason(ChatFinishReason reason) => reason switch
    {
        ChatFinishReason.Stop => "stop",
        ChatFinishReason.ToolCalls => "tool_calls",
        ChatFinishReason.MaxTokens => "length",
        ChatFinishReason.ContentFilter => "content_filter",
        _ => "stop",
    };

    /// <summary>
    /// Builds one OpenAI-compatible `chat.completion.chunk` object per ChatStreamEvent for a single
    /// turn — stateful only in the per-turn sense (tracks tool-call id -&gt; stream index, and whether
    /// the {role:"assistant"} delta has already been sent), mirroring how a real OpenAI-compatible
    /// backend streams tool_calls deltas (each needs a stable numeric `index`).
    /// </summary>
    public sealed class ChunkBuilder
    {
        private readonly string _id;
        private readonly string _model;
        private readonly Dictionary<string, int> _toolCallIndexById = new();
        private int _nextToolCallIndex;
        private bool _roleSent;

        public ChunkBuilder(string id, string model)
        {
            _id = id;
            _model = model;
        }

        /// <summary>Returns null for events with no OpenAI chat-completions wire equivalent (e.g. thinking deltas) — caller should skip sending a frame for those.</summary>
        public JsonObject? Build(ChatStreamEvent evt)
        {
            var delta = new JsonObject();
            string? finishReason = null;

            switch (evt)
            {
                case TextDeltaEvent t:
                    if (!_roleSent) { delta["role"] = "assistant"; _roleSent = true; }
                    delta["content"] = t.Text;
                    break;

                case ToolUseStartEvent s:
                {
                    if (!_roleSent) { delta["role"] = "assistant"; _roleSent = true; }
                    var idx = _nextToolCallIndex++;
                    _toolCallIndexById[s.Id] = idx;
                    delta["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = idx,
                            ["id"] = s.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = s.Name, ["arguments"] = "" },
                        },
                    };
                    break;
                }

                case ToolUseDeltaEvent d:
                {
                    if (!_toolCallIndexById.TryGetValue(d.Id, out var idx)) return null;
                    delta["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = idx,
                            ["function"] = new JsonObject { ["arguments"] = d.ArgumentsJsonDelta },
                        },
                    };
                    break;
                }

                case ThinkingDeltaEvent:
                    return null;

                case MessageStopEvent stop:
                    finishReason = MapFinishReason(stop.FinishReason);
                    break;

                default:
                    return null;
            }

            var choice = new JsonObject
            {
                ["index"] = 0,
                ["delta"] = delta,
                ["finish_reason"] = finishReason,
            };

            return new JsonObject
            {
                ["id"] = _id,
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = _model,
                ["choices"] = new JsonArray { choice },
            };
        }
    }
}
