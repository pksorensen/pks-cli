using System.Text.Json;
using Azure;
using FluentAssertions;
using OpenAI.Chat;
using PKS.Infrastructure.Services.Agent.Chat;
using Xunit;
using NeutralChat = PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.CLI.Tests.Infrastructure.Services.Agent.Chat;

public class AzureOpenAIChatProviderTests
{
    private static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ConvertMessages_SystemPromptOnly_PrependsSystemMessage()
    {
        var result = AzureOpenAIChatProvider.ConvertMessages(
            Array.Empty<NeutralChat.ChatMessage>(),
            "you are helpful");

        result.Should().HaveCount(1);
        result[0].Should().BeOfType<SystemChatMessage>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ConvertMessages_UserText_BecomesUserChatMessage()
    {
        var messages = new NeutralChat.ChatMessage[]
        {
            new(ChatRole.User, new ChatContentBlock[] { new TextBlock("hello") }),
        };

        var result = AzureOpenAIChatProvider.ConvertMessages(messages, string.Empty);

        result.Should().HaveCount(1);
        var user = result[0].Should().BeOfType<UserChatMessage>().Subject;
        user.Content.Should().ContainSingle();
        user.Content[0].Text.Should().Be("hello");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ConvertMessages_AssistantWithToolUse_EmitsAssistantWithToolCall()
    {
        var args = ParseJson("""{"path":"foo.txt"}""");
        var messages = new NeutralChat.ChatMessage[]
        {
            new(ChatRole.Assistant, new ChatContentBlock[]
            {
                new TextBlock("ok"),
                new ToolUseBlock("call_1", "read", args),
            }),
        };

        var result = AzureOpenAIChatProvider.ConvertMessages(messages, string.Empty);

        result.Should().HaveCount(1);
        var asst = result[0].Should().BeOfType<AssistantChatMessage>().Subject;
        asst.ToolCalls.Should().HaveCount(1);
        asst.ToolCalls[0].FunctionName.Should().Be("read");
        asst.ToolCalls[0].FunctionArguments.ToString().Should().Contain("\"path\":\"foo.txt\"");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ConvertMessages_ToolResults_EmitOneToolChatMessagePerResult()
    {
        var messages = new NeutralChat.ChatMessage[]
        {
            new(ChatRole.Tool, new ChatContentBlock[]
            {
                new ToolResultBlock("call_1", "contents", false),
                new ToolResultBlock("call_2", "more", false),
            }),
        };

        var result = AzureOpenAIChatProvider.ConvertMessages(messages, string.Empty);

        result.Should().HaveCount(2);
        result[0].Should().BeOfType<ToolChatMessage>();
        result[1].Should().BeOfType<ToolChatMessage>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void ConvertTools_PassesSchemaThrough()
    {
        var schemaJson = """{"type":"object","properties":{"path":{"type":"string"}}}""";
        var schema = ParseJson(schemaJson);
        var tools = new[]
        {
            new ChatToolDefinition("read", "Read a file", schema),
        };

        var result = AzureOpenAIChatProvider.ConvertTools(tools);

        result.Should().HaveCount(1);
        result[0].FunctionName.Should().Be("read");
        result[0].FunctionParameters.ToString().Should().Be(schema.GetRawText());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Constructor_StoresProviderId()
    {
        var provider = new AzureOpenAIChatProvider(new Uri("https://x.invalid"), new AzureKeyCredential("k"));
        provider.ProviderId.Should().Be("azure-openai");
    }
}
