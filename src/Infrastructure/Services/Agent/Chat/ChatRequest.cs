namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Provider-neutral request envelope.
/// SystemPrompt is carried as a top-level field (not a message) because Anthropic
/// puts it at the top level and OpenAI accepts a `system` role message; the provider
/// adapter decides how to wire it.
/// </summary>
public sealed record ChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    string SystemPrompt,
    IReadOnlyList<ChatToolDefinition> Tools,
    int MaxOutputTokens);
