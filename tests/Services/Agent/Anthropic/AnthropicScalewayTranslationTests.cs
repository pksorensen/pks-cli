using System.Linq;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Services.Agent.Anthropic;

/// <summary>
/// Covers the Anthropic Messages &lt;-&gt; OpenAI Chat Completions translation that backs
/// <c>pks claude scaleway</c> (and the mistral/qwen aliases) — request shaping, streamed-chunk
/// reconstruction, and the model catalog that drives the pickers.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class AnthropicScalewayTranslationTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---------- request translation ----------

    [Fact]
    public void Request_maps_system_and_user_and_max_tokens_and_stream_options()
    {
        var anthropic = Parse("""
        {
          "system": "You are helpful.",
          "max_tokens": 1024,
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """);

        var req = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "devstral-2-123b-instruct-2512", stream: true);

        req["model"]!.GetValue<string>().Should().Be("devstral-2-123b-instruct-2512");
        req["stream"]!.GetValue<bool>().Should().BeTrue();
        req["max_tokens"]!.GetValue<int>().Should().Be(1024);
        req["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();

        var messages = req["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[0]!["content"]!.GetValue<string>().Should().Be("You are helpful.");
        messages[1]!["role"]!.GetValue<string>().Should().Be("user");
        messages[1]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void Request_clamps_max_tokens_to_model_cap()
    {
        var anthropic = Parse("""{ "messages": [], "max_tokens": 32000 }""");

        AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "devstral", stream: true, maxOutputCap: 16384)
            ["max_tokens"]!.GetValue<int>().Should().Be(16384);

        // Below the cap is left untouched; no cap leaves it as-is.
        AnthropicToChatCompletionsTranslator.BuildChatRequest(Parse("""{ "messages": [], "max_tokens": 1000 }"""), "m", stream: true, maxOutputCap: 16384)
            ["max_tokens"]!.GetValue<int>().Should().Be(1000);
        AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "m", stream: true)
            ["max_tokens"]!.GetValue<int>().Should().Be(32000);
    }

    [Fact]
    public void Request_concatenates_array_system_blocks()
    {
        var anthropic = Parse("""
        { "system": [ {"type":"text","text":"A"}, {"type":"text","text":"B"} ], "messages": [] }
        """);

        var req = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "m", stream: false);
        req["messages"]!.AsArray()[0]!["content"]!.GetValue<string>().Should().Be("A\nB");
        req.ContainsKey("stream_options").Should().BeFalse();
    }

    [Fact]
    public void Request_maps_assistant_tool_use_to_tool_calls_and_tool_result_to_tool_message()
    {
        var anthropic = Parse("""
        {
          "messages": [
            { "role": "user", "content": "weather?" },
            { "role": "assistant", "content": [
              { "type": "text", "text": "checking" },
              { "type": "tool_use", "id": "toolu_1", "name": "get_weather", "input": { "city": "Oslo" } }
            ] },
            { "role": "user", "content": [
              { "type": "tool_result", "tool_use_id": "toolu_1", "content": "12C" }
            ] }
          ]
        }
        """);

        var messages = AnthropicToChatCompletionsTranslator
            .BuildChatRequest(anthropic, "m", stream: true)["messages"]!.AsArray();

        var assistant = messages.First(m => m!["role"]!.GetValue<string>() == "assistant")!;
        assistant["content"]!.GetValue<string>().Should().Be("checking");
        var toolCall = assistant["tool_calls"]!.AsArray()[0]!;
        toolCall["id"]!.GetValue<string>().Should().Be("toolu_1");
        toolCall["type"]!.GetValue<string>().Should().Be("function");
        toolCall["function"]!["name"]!.GetValue<string>().Should().Be("get_weather");
        toolCall["function"]!["arguments"]!.GetValue<string>().Should().Contain("Oslo");

        var toolMsg = messages.First(m => m!["role"]!.GetValue<string>() == "tool")!;
        toolMsg["tool_call_id"]!.GetValue<string>().Should().Be("toolu_1");
        toolMsg["content"]!.GetValue<string>().Should().Be("12C");
    }

    [Fact]
    public void Request_maps_tools_to_function_definitions()
    {
        var anthropic = Parse("""
        {
          "messages": [],
          "tools": [ {
            "name": "get_weather",
            "description": "Get weather",
            "input_schema": { "type":"object", "properties": { "city": {"type":"string"} } }
          } ]
        }
        """);

        var tool = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "m", stream: true)["tools"]!.AsArray()[0]!;
        tool["type"]!.GetValue<string>().Should().Be("function");
        tool["function"]!["name"]!.GetValue<string>().Should().Be("get_weather");
        tool["function"]!["description"]!.GetValue<string>().Should().Be("Get weather");
        tool["function"]!["parameters"]!["properties"]!["city"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "required")]
    [InlineData("none", "none")]
    public void Request_maps_tool_choice(string anthropicType, string expected)
    {
        var anthropic = Parse($$"""{ "messages": [], "tool_choice": { "type": "{{anthropicType}}" } }""");
        var req = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "m", stream: true);
        req["tool_choice"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void Request_strips_cache_control_from_tool_schema()
    {
        var anthropic = Parse("""
        {
          "messages": [],
          "tools": [ {
            "name": "t",
            "input_schema": { "type":"object", "cache_control": {"type":"ephemeral"}, "properties": {} }
          } ]
        }
        """);

        var req = AnthropicToChatCompletionsTranslator.BuildChatRequest(anthropic, "m", stream: true);
        req.ToJsonString().Should().NotContain("cache_control");
    }

    // ---------- streaming reconstruction ----------

    private static string AllFrames(ChatCompletionsToAnthropicStreamConverter c, params string[] chunks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in chunks)
        {
            foreach (var f in c.Handle(Parse(e))) sb.Append(f);
        }
        foreach (var f in c.Flush()) sb.Append(f);
        return sb.ToString();
    }

    [Fact]
    public void Stream_text_response_produces_well_formed_anthropic_sequence()
    {
        var c = new ChatCompletionsToAnthropicStreamConverter("devstral", inputTokensEstimate: 10);
        var output = AllFrames(c,
            """{ "id": "chatcmpl-1", "choices": [ { "index": 0, "delta": { "role": "assistant", "content": "Hel" } } ] }""",
            """{ "id": "chatcmpl-1", "choices": [ { "index": 0, "delta": { "content": "lo" } } ] }""",
            """{ "id": "chatcmpl-1", "choices": [ { "index": 0, "delta": {}, "finish_reason": "stop" } ] }""",
            """{ "id": "chatcmpl-1", "choices": [], "usage": { "prompt_tokens": 11, "completion_tokens": 3 } }""");

        output.Should().Contain("event: message_start");
        output.Should().Contain("\"id\":\"chatcmpl-1\"");
        output.Should().Contain("event: content_block_start");
        output.Should().Contain("\"text\":\"Hel\"");
        output.Should().Contain("event: content_block_stop");
        output.Should().Contain("\"stop_reason\":\"end_turn\"");
        output.Should().Contain("\"output_tokens\":3");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Stream_tool_call_sets_tool_use_stop_reason_and_streams_json()
    {
        var c = new ChatCompletionsToAnthropicStreamConverter("devstral", 5);
        var output = AllFrames(c,
            """{ "id": "c1", "choices": [ { "index": 0, "delta": { "tool_calls": [ { "index": 0, "id": "call_9", "type": "function", "function": { "name": "read_file", "arguments": "" } } ] } } ] }""",
            """{ "id": "c1", "choices": [ { "index": 0, "delta": { "tool_calls": [ { "index": 0, "function": { "arguments": "{\"path\":" } } ] } } ] }""",
            """{ "id": "c1", "choices": [ { "index": 0, "delta": { "tool_calls": [ { "index": 0, "function": { "arguments": "\"a.txt\"}" } } ] } } ] }""",
            """{ "id": "c1", "choices": [ { "index": 0, "delta": {}, "finish_reason": "tool_calls" } ] }""");

        output.Should().Contain("\"type\":\"tool_use\"");
        output.Should().Contain("\"id\":\"call_9\"");
        output.Should().Contain("\"name\":\"read_file\"");
        output.Should().Contain("\"type\":\"input_json_delta\"");
        output.Should().Contain("\"stop_reason\":\"tool_use\"");

        var final = c.BuildFinalMessage();
        var toolBlock = final["content"]!.AsArray()[0]!;
        toolBlock["type"]!.GetValue<string>().Should().Be("tool_use");
        toolBlock["input"]!["path"]!.GetValue<string>().Should().Be("a.txt");
    }

    [Fact]
    public void Stream_reasoning_becomes_thinking_when_enabled_and_dropped_when_not()
    {
        var on = AllFrames(new ChatCompletionsToAnthropicStreamConverter("m", 1, emitThinking: true),
            """{ "id": "r", "choices": [ { "index": 0, "delta": { "reasoning_content": "pondering" } } ] }""",
            """{ "id": "r", "choices": [ { "index": 0, "delta": { "content": "done" }, "finish_reason": "stop" } ] }""");
        on.Should().Contain("\"type\":\"thinking_delta\"");
        on.Should().Contain("pondering");

        var off = AllFrames(new ChatCompletionsToAnthropicStreamConverter("m", 1, emitThinking: false),
            """{ "id": "r", "choices": [ { "index": 0, "delta": { "reasoning_content": "pondering" } } ] }""",
            """{ "id": "r", "choices": [ { "index": 0, "delta": { "content": "done" }, "finish_reason": "stop" } ] }""");
        off.Should().NotContain("thinking_delta");
    }

    [Fact]
    public void Stream_length_finish_maps_to_max_tokens()
    {
        var output = AllFrames(new ChatCompletionsToAnthropicStreamConverter("m", 1),
            """{ "id": "r", "choices": [ { "index": 0, "delta": { "content": "x" } } ] }""",
            """{ "id": "r", "choices": [ { "index": 0, "delta": {}, "finish_reason": "length" } ] }""");
        output.Should().Contain("\"stop_reason\":\"max_tokens\"");
    }

    // ---------- catalog ----------

    [Fact]
    public void Catalog_byFamily_returns_only_that_family()
    {
        GenerativeModelCatalog.ByFamily("qwen").Should().NotBeEmpty();
        GenerativeModelCatalog.ByFamily("qwen").Should().OnlyContain(m => m.Family == "qwen");
        GenerativeModelCatalog.ByFamily("mistral").Should().OnlyContain(m => m.Family == "mistral");
    }

    [Fact]
    public void Catalog_scaleway_has_exactly_one_default_and_all_scaleway_provider()
    {
        var all = GenerativeModelCatalog.ByProvider(GenerativeModelCatalog.ScalewayProvider);
        all.Should().OnlyContain(m => m.Provider == GenerativeModelCatalog.ScalewayProvider);
        all.Count(m => m.IsDefault).Should().Be(1);
        GenerativeModelCatalog.DefaultIn(all)!.Id.Should().Be("devstral-2-123b-instruct-2512");
    }
}
