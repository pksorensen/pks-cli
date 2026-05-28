using System.Text.Json;

namespace PKS.Infrastructure.Services.Agent.Chat;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

public abstract record ChatContentBlock;

public sealed record TextBlock(string Text) : ChatContentBlock;

public sealed record ImageBlock(string MimeType, byte[] Data) : ChatContentBlock;

public sealed record ToolUseBlock(string Id, string Name, JsonElement Arguments) : ChatContentBlock;

public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ChatContentBlock;

public sealed record ThinkingBlock(string Text) : ChatContentBlock;

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ChatContentBlock> Content)
{
    public static ChatMessage User(string text) =>
        new(ChatRole.User, new ChatContentBlock[] { new TextBlock(text) });

    public static ChatMessage Assistant(string text) =>
        new(ChatRole.Assistant, new ChatContentBlock[] { new TextBlock(text) });
}
