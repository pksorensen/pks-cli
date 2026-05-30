using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Translates an inbound Anthropic Messages API request body (what Claude Code sends to
/// <c>POST /v1/messages</c>) into an Azure OpenAI <b>Responses API</b> request body
/// (<c>POST {endpoint}/openai/v1/responses</c>) suitable for Codex / GPT-5.x reasoning models.
///
/// This is the inverse-direction companion to <see cref="ResponsesToAnthropicStreamConverter"/>,
/// which turns the streamed Responses output back into Anthropic SSE events.
///
/// Mapping summary:
///   anthropic.system            -> responses.instructions
///   anthropic.messages[user]    -> input message (input_text / input_image parts)
///   anthropic.tool_result block -> input function_call_output item
///   anthropic.messages[asst]    -> input message (output_text) + function_call items
///   anthropic.tools             -> responses.tools (type=function)
///   anthropic.tool_choice       -> responses.tool_choice
///   anthropic.max_tokens        -> responses.max_output_tokens
///   (reasoning effort)          -> responses.reasoning { effort, summary:auto }
/// Sampling params (temperature/top_p) are intentionally dropped: reasoning models reject them.
/// </summary>
public static class AnthropicToResponsesTranslator
{
    public static JsonObject BuildResponsesRequest(
        JsonElement anthropic,
        string modelDeployment,
        string reasoningEffort,
        bool stream)
    {
        var input = new JsonArray();

        if (anthropic.TryGetProperty("messages", out var messages) &&
            messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in messages.EnumerateArray())
            {
                AppendMessage(input, msg);
            }
        }

        var request = new JsonObject
        {
            ["model"] = modelDeployment,
            ["input"] = input,
            ["stream"] = stream,
            ["store"] = false,
        };

        var instructions = ExtractSystem(anthropic);
        if (!string.IsNullOrEmpty(instructions))
        {
            request["instructions"] = instructions;
        }

        if (anthropic.TryGetProperty("max_tokens", out var maxTokens) &&
            maxTokens.ValueKind == JsonValueKind.Number)
        {
            request["max_output_tokens"] = maxTokens.GetInt32();
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

        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            request["reasoning"] = new JsonObject
            {
                ["effort"] = reasoningEffort,
                ["summary"] = "auto",
            };
        }

        return request;
    }

    /// <summary>system may be a plain string or an array of text blocks (Claude Code uses the array form).</summary>
    private static string ExtractSystem(JsonElement anthropic)
    {
        if (!anthropic.TryGetProperty("system", out var system))
        {
            return string.Empty;
        }

        if (system.ValueKind == JsonValueKind.String)
        {
            return system.GetString() ?? string.Empty;
        }

        if (system.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var block in system.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object &&
                    block.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text.GetString());
                }
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static void AppendMessage(JsonArray input, JsonElement msg)
    {
        var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";

        if (!msg.TryGetProperty("content", out var content))
        {
            return;
        }

        // Shorthand: content as a plain string.
        if (content.ValueKind == JsonValueKind.String)
        {
            input.Add(MessageItem(role, role == "assistant" ? "output_text" : "input_text", content.GetString() ?? string.Empty));
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // Collect ordinary content into a single message item; tool_use / tool_result
        // become their own top-level items (Responses requires them outside messages).
        var parts = new JsonArray();
        var textPartType = role == "assistant" ? "output_text" : "input_text";

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "text":
                {
                    var text = block.TryGetProperty("text", out var txt) ? txt.GetString() ?? string.Empty : string.Empty;
                    parts.Add(new JsonObject { ["type"] = textPartType, ["text"] = text });
                    break;
                }
                case "image":
                {
                    // Only user images are supported as input_image.
                    var dataUrl = ImageToDataUrl(block);
                    if (dataUrl is not null)
                    {
                        parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = dataUrl });
                    }
                    break;
                }
                case "tool_use":
                {
                    // Flush any buffered message parts first to preserve ordering.
                    FlushParts(input, role, parts);
                    parts = new JsonArray();

                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var args = block.TryGetProperty("input", out var inp)
                        ? inp.GetRawText()
                        : "{}";
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = id,
                        ["name"] = name,
                        ["arguments"] = args,
                    });
                    break;
                }
                case "tool_result":
                {
                    FlushParts(input, role, parts);
                    parts = new JsonArray();

                    var callId = block.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? string.Empty : string.Empty;
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = ToolResultText(block),
                    });
                    break;
                }
                case "thinking":
                case "redacted_thinking":
                    // Reasoning is not replayed: Responses reconstructs its own reasoning state,
                    // and Anthropic thinking signatures are meaningless to the upstream.
                    break;
            }
        }

        FlushParts(input, role, parts);
    }

    private static void FlushParts(JsonArray input, string role, JsonArray parts)
    {
        if (parts.Count == 0) return;
        input.Add(new JsonObject
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = parts,
        });
    }

    private static JsonObject MessageItem(string role, string partType, string text) => new()
    {
        ["type"] = "message",
        ["role"] = role,
        ["content"] = new JsonArray { new JsonObject { ["type"] = partType, ["text"] = text } },
    };

    private static string? ImageToDataUrl(JsonElement block)
    {
        if (!block.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var srcType = source.TryGetProperty("type", out var st) ? st.GetString() : null;
        if (srcType == "url" && source.TryGetProperty("url", out var url))
        {
            return url.GetString();
        }

        if (srcType == "base64" &&
            source.TryGetProperty("media_type", out var mt) &&
            source.TryGetProperty("data", out var data))
        {
            return $"data:{mt.GetString()};base64,{data.GetString()}";
        }

        return null;
    }

    /// <summary>tool_result content may be a string or an array of blocks; flatten to text for function_call_output.</summary>
    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

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

            var fn = new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
            };
            if (tool.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            {
                fn["description"] = desc.GetString();
            }
            // Anthropic input_schema -> Responses parameters (both JSON Schema).
            if (tool.TryGetProperty("input_schema", out var schema))
            {
                fn["parameters"] = JsonNode.Parse(schema.GetRawText());
            }
            result.Add(fn);
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
                ["name"] = tc.TryGetProperty("name", out var n) ? n.GetString() : null,
            },
            _ => null,
        };
    }
}
