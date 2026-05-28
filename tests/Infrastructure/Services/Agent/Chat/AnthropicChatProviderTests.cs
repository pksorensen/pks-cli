using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using PKS.Infrastructure.Services.Agent.Chat;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Chat;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class AnthropicChatProviderTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatRequest MakeRequest(
        IEnumerable<ChatMessage>? messages = null,
        string systemPrompt = "",
        IEnumerable<ChatToolDefinition>? tools = null,
        int maxTokens = 1024) =>
        new(
            (messages ?? Array.Empty<ChatMessage>()).ToList(),
            systemPrompt,
            (tools ?? Array.Empty<ChatToolDefinition>()).ToList(),
            maxTokens);

    [Fact]
    public void Constructor_StoresProviderId()
    {
        var p = new AnthropicChatProvider(new Uri("https://x.invalid"), "k", new HttpClient());
        p.ProviderId.Should().Be("anthropic");
    }

    [Fact]
    public void BuildRequestBody_SystemPrompt_GoesToTopLevel()
    {
        var req = MakeRequest(
            messages: new[] { ChatMessage.User("hi") },
            systemPrompt: "you are nice");

        var json = AnthropicChatProvider.BuildRequestBody(req, "claude-opus-4-7");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("system").GetString().Should().Be("you are nice");
        var messages = root.GetProperty("messages");
        foreach (var m in messages.EnumerateArray())
        {
            m.GetProperty("role").GetString().Should().NotBe("system");
        }
    }

    [Fact]
    public void BuildRequestBody_EmptySystemPrompt_OmitsField()
    {
        var req = MakeRequest(messages: new[] { ChatMessage.User("hi") }, systemPrompt: "");
        var json = AnthropicChatProvider.BuildRequestBody(req, "claude-opus-4-7");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("system", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildRequestBody_AssistantWithToolUse_ProducesToolUseBlock()
    {
        var args = JsonDocument.Parse("{\"path\":\"foo\"}").RootElement.Clone();
        var assistant = new ChatMessage(ChatRole.Assistant, new ChatContentBlock[]
        {
            new TextBlock("ok"),
            new ToolUseBlock("toolu_1", "read", args),
        });

        var json = AnthropicChatProvider.BuildRequestBody(MakeRequest(messages: new[] { assistant }), "m");
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("messages")[0];
        msg.GetProperty("role").GetString().Should().Be("assistant");
        var content = msg.GetProperty("content");
        content.GetArrayLength().Should().Be(2);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("ok");
        var tu = content[1];
        tu.GetProperty("type").GetString().Should().Be("tool_use");
        tu.GetProperty("id").GetString().Should().Be("toolu_1");
        tu.GetProperty("name").GetString().Should().Be("read");
        tu.GetProperty("input").GetProperty("path").GetString().Should().Be("foo");
    }

    [Fact]
    public void BuildRequestBody_ToolMessage_BecomesUserToolResult()
    {
        var toolMsg = new ChatMessage(ChatRole.Tool, new ChatContentBlock[]
        {
            new ToolResultBlock("toolu_1", "contents", false),
        });

        var json = AnthropicChatProvider.BuildRequestBody(MakeRequest(messages: new[] { toolMsg }), "m");
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("messages")[0];
        msg.GetProperty("role").GetString().Should().Be("user");
        var tr = msg.GetProperty("content")[0];
        tr.GetProperty("type").GetString().Should().Be("tool_result");
        tr.GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
        tr.GetProperty("content").GetString().Should().Be("contents");
        tr.GetProperty("is_error").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void BuildRequestBody_ToolsPassedThroughWithInputSchema()
    {
        var schema = Schema("{\"type\":\"object\"}");
        var tool = new ChatToolDefinition("read", "Read", schema);
        var json = AnthropicChatProvider.BuildRequestBody(MakeRequest(tools: new[] { tool }), "m");
        using var doc = JsonDocument.Parse(json);

        var tools = doc.RootElement.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        var t0 = tools[0];
        t0.GetProperty("name").GetString().Should().Be("read");
        t0.GetProperty("description").GetString().Should().Be("Read");
        t0.GetProperty("input_schema").GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public void BuildRequestBody_NoTools_OmitsToolsField()
    {
        var json = AnthropicChatProvider.BuildRequestBody(MakeRequest(), "m");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public void ParseSseEvent_ContentBlockStart_ToolUse_YieldsToolUseStart()
    {
        var blocks = new Dictionary<int, string>();
        string? stop = null; ChatUsage? usage = null;
        var data = "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_abc\",\"name\":\"read\",\"input\":{}}}";

        var evt = AnthropicChatProvider.ParseSseEvent("content_block_start", data, blocks, ref stop, ref usage);

        evt.Should().BeOfType<ToolUseStartEvent>();
        var tu = (ToolUseStartEvent)evt!;
        tu.Id.Should().Be("toolu_abc");
        tu.Name.Should().Be("read");
        blocks[1].Should().Be("toolu_abc");
    }

    [Fact]
    public void ParseSseEvent_TextDelta_YieldsTextDelta()
    {
        var blocks = new Dictionary<int, string>();
        string? stop = null; ChatUsage? usage = null;
        var data = "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hello\"}}";

        var evt = AnthropicChatProvider.ParseSseEvent("content_block_delta", data, blocks, ref stop, ref usage);

        evt.Should().BeOfType<TextDeltaEvent>();
        ((TextDeltaEvent)evt!).Text.Should().Be("hello");
    }

    [Fact]
    public void ParseSseEvent_InputJsonDelta_ResolvesIdByIndex()
    {
        var blocks = new Dictionary<int, string> { [2] = "toolu_xyz" };
        string? stop = null; ChatUsage? usage = null;
        var data = "{\"type\":\"content_block_delta\",\"index\":2,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"a\\\":1}\"}}";

        var evt = AnthropicChatProvider.ParseSseEvent("content_block_delta", data, blocks, ref stop, ref usage);

        evt.Should().BeOfType<ToolUseDeltaEvent>();
        var d = (ToolUseDeltaEvent)evt!;
        d.Id.Should().Be("toolu_xyz");
        d.ArgumentsJsonDelta.Should().Be("{\"a\":1}");
    }

    [Fact]
    public void ParseSseEvent_MessageDelta_RecordsStopReasonAndUsage_NoEventYielded()
    {
        var blocks = new Dictionary<int, string>();
        string? stop = null; ChatUsage? usage = null;
        var data = "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"input_tokens\":12,\"output_tokens\":34}}";

        var evt = AnthropicChatProvider.ParseSseEvent("message_delta", data, blocks, ref stop, ref usage);

        evt.Should().BeNull();
        stop.Should().Be("end_turn");
        usage.Should().NotBeNull();
        usage!.InputTokens.Should().Be(12);
        usage.OutputTokens.Should().Be(34);
    }

    [Fact]
    public void ParseSseEvent_MessageStop_YieldsMappedFinishReason()
    {
        var blocks = new Dictionary<int, string>();
        string? stop = "end_turn"; ChatUsage? usage = new ChatUsage(1, 2);
        var evt = AnthropicChatProvider.ParseSseEvent("message_stop", "{\"type\":\"message_stop\"}", blocks, ref stop, ref usage);

        evt.Should().BeOfType<MessageStopEvent>();
        var ms = (MessageStopEvent)evt!;
        ms.FinishReason.Should().Be(ChatFinishReason.Stop);
        ms.Usage.Should().Be(new ChatUsage(1, 2));
    }

    [Fact]
    public void MapStopReason_MapsKnownReasons()
    {
        AnthropicChatProvider.MapStopReason("end_turn").Should().Be(ChatFinishReason.Stop);
        AnthropicChatProvider.MapStopReason("tool_use").Should().Be(ChatFinishReason.ToolCalls);
        AnthropicChatProvider.MapStopReason("max_tokens").Should().Be(ChatFinishReason.MaxTokens);
        AnthropicChatProvider.MapStopReason("stop_sequence").Should().Be(ChatFinishReason.Stop);
        AnthropicChatProvider.MapStopReason(null).Should().Be(ChatFinishReason.Stop);
        AnthropicChatProvider.MapStopReason("unknown").Should().Be(ChatFinishReason.Stop);
    }
}
