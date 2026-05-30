using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Services.Agent.Anthropic;

/// <summary>
/// Covers the Anthropic Messages <-> Azure OpenAI Responses translation that backs
/// <c>pks claude codex</c> — the request shaping and the streamed-event reconstruction.
/// </summary>
[Trait(TestTraits.Category, TestCategories.Unit)]
[Trait(TestTraits.Speed, TestSpeed.Fast)]
public class AnthropicCodexTranslationTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---------- request translation ----------

    [Fact]
    public void Request_maps_system_messages_and_max_tokens()
    {
        var anthropic = Parse("""
        {
          "model": "claude-opus-4",
          "system": "You are helpful.",
          "max_tokens": 1024,
          "messages": [ { "role": "user", "content": "hi" } ]
        }
        """);

        var req = AnthropicToResponsesTranslator.BuildResponsesRequest(anthropic, "gpt-5.1-codex", "medium", stream: true);

        req["model"]!.GetValue<string>().Should().Be("gpt-5.1-codex");
        req["instructions"]!.GetValue<string>().Should().Be("You are helpful.");
        req["max_output_tokens"]!.GetValue<int>().Should().Be(1024);
        req["stream"]!.GetValue<bool>().Should().BeTrue();
        req["store"]!.GetValue<bool>().Should().BeFalse();
        req["reasoning"]!["effort"]!.GetValue<string>().Should().Be("medium");

        var input = req["input"]!.AsArray();
        input.Should().HaveCount(1);
        input[0]!["type"]!.GetValue<string>().Should().Be("message");
        input[0]!["role"]!.GetValue<string>().Should().Be("user");
        input[0]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("input_text");
        input[0]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void Request_concatenates_array_system_blocks_into_instructions()
    {
        var anthropic = Parse("""
        {
          "system": [ {"type":"text","text":"A"}, {"type":"text","text":"B"} ],
          "messages": []
        }
        """);

        var req = AnthropicToResponsesTranslator.BuildResponsesRequest(anthropic, "m", "low", stream: false);
        req["instructions"]!.GetValue<string>().Should().Be("A\nB");
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
            "input_schema": { "type":"object", "properties": { "city": {"type":"string"} }, "required": ["city"] }
          } ]
        }
        """);

        var req = AnthropicToResponsesTranslator.BuildResponsesRequest(anthropic, "m", "high", stream: true);
        var tool = req["tools"]!.AsArray()[0]!;
        tool["type"]!.GetValue<string>().Should().Be("function");
        tool["name"]!.GetValue<string>().Should().Be("get_weather");
        tool["description"]!.GetValue<string>().Should().Be("Get weather");
        tool["parameters"]!["properties"]!["city"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Fact]
    public void Request_maps_assistant_tool_use_and_user_tool_result()
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

        var input = AnthropicToResponsesTranslator
            .BuildResponsesRequest(anthropic, "m", "medium", stream: true)["input"]!.AsArray();

        // user msg, assistant text msg, function_call, function_call_output
        var fnCall = input.First(n => n!["type"]!.GetValue<string>() == "function_call")!;
        fnCall["call_id"]!.GetValue<string>().Should().Be("toolu_1");
        fnCall["name"]!.GetValue<string>().Should().Be("get_weather");
        fnCall["arguments"]!.GetValue<string>().Should().Contain("Oslo");

        var fnOut = input.First(n => n!["type"]!.GetValue<string>() == "function_call_output")!;
        fnOut["call_id"]!.GetValue<string>().Should().Be("toolu_1");
        fnOut["output"]!.GetValue<string>().Should().Be("12C");
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("any", "required")]
    [InlineData("none", "none")]
    public void Request_maps_tool_choice(string anthropicType, string expected)
    {
        var anthropic = Parse($$"""{ "messages": [], "tool_choice": { "type": "{{anthropicType}}" } }""");
        var req = AnthropicToResponsesTranslator.BuildResponsesRequest(anthropic, "m", "medium", stream: true);
        req["tool_choice"]!.GetValue<string>().Should().Be(expected);
    }

    [Fact]
    public void Request_drops_thinking_blocks_on_replay()
    {
        var anthropic = Parse("""
        {
          "messages": [
            { "role": "assistant", "content": [
              { "type": "thinking", "thinking": "secret", "signature": "x" },
              { "type": "text", "text": "answer" }
            ] }
          ]
        }
        """);

        var input = AnthropicToResponsesTranslator
            .BuildResponsesRequest(anthropic, "m", "medium", stream: true)["input"]!.AsArray();
        input.Should().HaveCount(1);
        input[0]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("output_text");
        input[0]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("answer");
    }

    // ---------- streaming reconstruction ----------

    private static string AllFrames(ResponsesToAnthropicStreamConverter c, params string[] events)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in events)
        {
            foreach (var f in c.Handle(Parse(e)))
            {
                sb.Append(f);
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Stream_text_response_produces_well_formed_anthropic_sequence()
    {
        var c = new ResponsesToAnthropicStreamConverter("gpt-5.1-codex", inputTokensEstimate: 10);
        var output = AllFrames(c,
            """{ "type": "response.created", "response": { "id": "resp_abc" } }""",
            """{ "type": "response.output_item.added", "output_index": 0, "item": { "type": "message" } }""",
            """{ "type": "response.output_text.delta", "output_index": 0, "delta": "Hel" }""",
            """{ "type": "response.output_text.delta", "output_index": 0, "delta": "lo" }""",
            """{ "type": "response.output_item.done", "output_index": 0 }""",
            """{ "type": "response.completed", "response": { "usage": { "input_tokens": 11, "output_tokens": 3 } } }""");

        output.Should().Contain("event: message_start");
        output.Should().Contain("\"id\":\"resp_abc\"");
        output.Should().Contain("event: content_block_start");
        output.Should().Contain("event: content_block_delta");
        output.Should().Contain("\"text\":\"Hel\"");
        output.Should().Contain("event: content_block_stop");
        output.Should().Contain("event: message_delta");
        output.Should().Contain("\"stop_reason\":\"end_turn\"");
        output.Should().Contain("\"output_tokens\":3");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Stream_tool_call_sets_tool_use_stop_reason_and_streams_json()
    {
        var c = new ResponsesToAnthropicStreamConverter("gpt-5.1-codex", 5);
        var output = AllFrames(c,
            """{ "type": "response.created", "response": { "id": "resp_1" } }""",
            """{ "type": "response.output_item.added", "output_index": 0, "item": { "type": "function_call", "call_id": "call_9", "name": "read_file" } }""",
            """{ "type": "response.function_call_arguments.delta", "output_index": 0, "delta": "{\"path\":" }""",
            """{ "type": "response.function_call_arguments.delta", "output_index": 0, "delta": "\"a.txt\"}" }""",
            """{ "type": "response.output_item.done", "output_index": 0 }""",
            """{ "type": "response.completed", "response": { "usage": { "output_tokens": 7 } } }""");

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
        var withThinking = new ResponsesToAnthropicStreamConverter("m", 1, emitThinking: true);
        var on = AllFrames(withThinking,
            """{ "type": "response.created", "response": { "id": "r" } }""",
            """{ "type": "response.reasoning_summary_text.delta", "output_index": 0, "delta": "pondering" }""",
            """{ "type": "response.completed", "response": {} }""");
        on.Should().Contain("\"type\":\"thinking_delta\"");
        on.Should().Contain("pondering");

        var without = new ResponsesToAnthropicStreamConverter("m", 1, emitThinking: false);
        var off = AllFrames(without,
            """{ "type": "response.created", "response": { "id": "r" } }""",
            """{ "type": "response.reasoning_summary_text.delta", "output_index": 0, "delta": "pondering" }""",
            """{ "type": "response.completed", "response": {} }""");
        off.Should().NotContain("thinking_delta");
    }

    [Fact]
    public void Stream_incomplete_maps_to_max_tokens()
    {
        var c = new ResponsesToAnthropicStreamConverter("m", 1);
        var output = AllFrames(c,
            """{ "type": "response.created", "response": { "id": "r" } }""",
            """{ "type": "response.incomplete", "response": { "usage": { "output_tokens": 99 } } }""");
        output.Should().Contain("\"stop_reason\":\"max_tokens\"");
    }

    [Fact]
    public void TokenEstimator_counts_system_messages_and_tools()
    {
        var anthropic = Parse("""
        { "system": "abcd", "messages": [ { "role": "user", "content": "efgh" } ], "tools": [] }
        """);
        TokenEstimator.EstimateInputTokens(anthropic).Should().BeGreaterThan(0);
    }
}
