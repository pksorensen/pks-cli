using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Translates an inbound Anthropic Messages API request body (what Claude Code sends to
/// <c>POST /v1/messages</c>) into an OpenAI <b>Chat Completions</b> request body
/// (<c>POST {baseUrl}/chat/completions</c>) — the shape Scaleway's Generative APIs and any other
/// OpenAI-compatible serverless endpoint expect.
///
/// This is the Chat-Completions sibling of <see cref="AnthropicToResponsesTranslator"/>; its
/// streamed output is turned back into Anthropic SSE by <see cref="ChatCompletionsToAnthropicStreamConverter"/>.
///
/// Mapping summary:
///   anthropic.system            -> messages[{role:"system"}]
///   anthropic.messages[user]    -> messages[{role:"user", content:[text|image_url]}]
///   anthropic.assistant text    -> messages[{role:"assistant", content}]
///   anthropic.tool_use block    -> assistant message tool_calls[{id, function{name, arguments}}]
///   anthropic.tool_result block -> messages[{role:"tool", tool_call_id, content}]
///   anthropic.tools             -> tools[{type:"function", function{name, description, parameters}}]
///   anthropic.tool_choice       -> tool_choice
///   anthropic.max_tokens        -> max_tokens
/// Anthropic-only <c>cache_control</c> is stripped (it would be rejected upstream).
/// </summary>
public static class AnthropicToChatCompletionsTranslator
{
    public static JsonObject BuildChatRequest(JsonElement anthropic, string model, bool stream, int? maxOutputCap = null)
    {
        var messages = new JsonArray();

        var system = ExtractSystem(anthropic);
        if (!string.IsNullOrEmpty(system))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        }

        if (anthropic.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in msgs.EnumerateArray())
            {
                AppendMessage(messages, msg);
            }
        }

        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
        };

        if (anthropic.TryGetProperty("max_tokens", out var maxTokens) && maxTokens.ValueKind == JsonValueKind.Number)
        {
            var mt = maxTokens.GetInt32();
            // Scaleway rejects requests above each model's max_completion_tokens; Claude Code asks high.
            if (maxOutputCap is int cap && cap > 0 && mt > cap) mt = cap;
            request["max_tokens"] = mt;
        }

        // Reasoning models accept these; Claude Code rarely sets them, but pass through when present.
        if (anthropic.TryGetProperty("temperature", out var temp) && temp.ValueKind == JsonValueKind.Number)
        {
            request["temperature"] = temp.GetDouble();
        }
        if (anthropic.TryGetProperty("top_p", out var topP) && topP.ValueKind == JsonValueKind.Number)
        {
            request["top_p"] = topP.GetDouble();
        }

        var tools = BuildTools(anthropic);
        if (tools.Count > 0)
        {
            request["tools"] = tools;
        }

        var toolChoice = BuildToolChoice(anthropic);
        if (toolChoice is not null)
        {
            request["tool_choice"] = toolChoice;
        }

        if (stream)
        {
            // Scaleway returns usage on the final streamed chunk when asked.
            request["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return request;
    }

    /// <summary>system may be a plain string or an array of text blocks (Claude Code uses the array form).</summary>
    private static string ExtractSystem(JsonElement anthropic)
    {
        if (!anthropic.TryGetProperty("system", out var system)) return string.Empty;

        if (system.ValueKind == JsonValueKind.String) return system.GetString() ?? string.Empty;

        if (system.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in system.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static void AppendMessage(JsonArray messages, JsonElement msg)
    {
        var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
        if (!msg.TryGetProperty("content", out var content)) return;

        // Shorthand: content as a plain string.
        if (content.ValueKind == JsonValueKind.String)
        {
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content.GetString() ?? string.Empty });
            return;
        }

        if (content.ValueKind != JsonValueKind.Array) return;

        // tool_result blocks become standalone {role:"tool"} messages; emit any pending text/tool_use
        // assistant/user message first so ordering is preserved.
        var textParts = new JsonArray();  // user-side may include image_url parts
        var sbText = new StringBuilder();  // assistant-side: plain string content
        var toolCalls = new JsonArray();
        var hasImages = false;

        void FlushUserOrAssistant()
        {
            if (role == "assistant")
            {
                if (sbText.Length == 0 && toolCalls.Count == 0) return;
                var m = new JsonObject { ["role"] = "assistant" };
                m["content"] = sbText.Length > 0 ? sbText.ToString() : null;
                if (toolCalls.Count > 0) m["tool_calls"] = DetachArray(ref toolCalls);
                messages.Add(m);
                sbText.Clear();
            }
            else
            {
                if (textParts.Count == 0) return;
                // Plain text-only -> string content; mixed/image -> content parts array.
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = hasImages ? DetachArray(ref textParts) : JoinText(textParts),
                });
                textParts = new JsonArray();
                hasImages = false;
            }
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "text":
                {
                    var text = block.TryGetProperty("text", out var txt) ? txt.GetString() ?? string.Empty : string.Empty;
                    if (role == "assistant") sbText.Append(text);
                    else textParts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    break;
                }
                case "image":
                {
                    var dataUrl = ImageToDataUrl(block);
                    if (dataUrl is not null && role != "assistant")
                    {
                        hasImages = true;
                        textParts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl },
                        });
                    }
                    break;
                }
                case "tool_use":
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var args = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
                    });
                    break;
                }
                case "tool_result":
                {
                    // Close the in-progress assistant/user message, then emit the tool message.
                    FlushUserOrAssistant();
                    var callId = block.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? string.Empty : string.Empty;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = callId,
                        ["content"] = ToolResultText(block),
                    });
                    break;
                }
                // thinking / redacted_thinking: not replayed (no upstream equivalent).
            }
        }

        FlushUserOrAssistant();
    }

    private static JsonArray DetachArray(ref JsonArray array)
    {
        var detached = new JsonArray();
        while (array.Count > 0)
        {
            var node = array[0];
            array.RemoveAt(0);
            detached.Add(node);
        }
        return detached;
    }

    private static string JoinText(JsonArray parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is JsonObject o && o.TryGetPropertyValue("text", out var txt) && txt is not null)
            {
                sb.Append(txt.GetValue<string>());
            }
        }
        return sb.ToString();
    }

    private static string? ImageToDataUrl(JsonElement block)
    {
        if (!block.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) return null;

        var srcType = source.TryGetProperty("type", out var st) ? st.GetString() : null;
        if (srcType == "url" && source.TryGetProperty("url", out var url)) return url.GetString();

        if (srcType == "base64" &&
            source.TryGetProperty("media_type", out var mt) &&
            source.TryGetProperty("data", out var data))
        {
            return $"data:{mt.GetString()};base64,{data.GetString()}";
        }

        return null;
    }

    /// <summary>tool_result content may be a string or an array of blocks; flatten to text.</summary>
    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return string.Empty;

        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    item.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static JsonArray BuildTools(JsonElement anthropic)
    {
        var result = new JsonArray();
        if (!anthropic.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name)) continue;

            var fn = new JsonObject { ["name"] = name };
            if (tool.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            {
                fn["description"] = desc.GetString();
            }
            if (tool.TryGetProperty("input_schema", out var schema))
            {
                fn["parameters"] = StripCacheControl(JsonNode.Parse(schema.GetRawText()));
            }
            result.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
        }

        return result;
    }

    private static JsonNode? BuildToolChoice(JsonElement anthropic)
    {
        if (!anthropic.TryGetProperty("tool_choice", out var tc) || tc.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = tc.TryGetProperty("type", out var t) ? t.GetString() : null;
        return type switch
        {
            "auto" => JsonValue.Create("auto"),
            "any" => JsonValue.Create("required"),
            "none" => JsonValue.Create("none"),
            "tool" => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = tc.TryGetProperty("name", out var n) ? n.GetString() : null },
            },
            _ => null,
        };
    }

    /// <summary>Recursively removes Anthropic-only <c>cache_control</c> keys the upstream would reject.</summary>
    private static JsonNode? StripCacheControl(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("cache_control");
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    StripCacheControl(obj[key]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) StripCacheControl(item);
                break;
        }
        return node;
    }
}
