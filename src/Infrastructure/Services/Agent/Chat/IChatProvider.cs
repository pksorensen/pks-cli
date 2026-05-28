namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Provider-neutral chat completion stream.
/// AgentLoop holds an IChatProvider; the concrete provider knows how to talk
/// to its backend (Azure OpenAI ChatClient, Anthropic Messages API, etc.).
/// </summary>
public interface IChatProvider
{
    /// <summary>Stable provider id, e.g. "azure-openai", "anthropic".</summary>
    string ProviderId { get; }

    /// <summary>
    /// Stream a chat completion. `modelId` is the provider-native deployment / model name
    /// (e.g. "gpt-5.5" for Azure OpenAI, "claude-opus-4-7" for Anthropic).
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        string modelId,
        CancellationToken cancellationToken);
}
