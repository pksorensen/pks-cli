using System.Net.Http;
using System.Text.Json;
using Azure;
using PKS.Infrastructure;
using PKS.Infrastructure.Services.Agent.Chat;

namespace PKS.Infrastructure.Services.Agent;

/// <summary>
/// Resolves a model id to a concrete <see cref="IChatProvider"/> and provider-native deployment name.
/// Configuration source order: explicit settings.json &gt; environment variables &gt; built-in defaults.
/// </summary>
public sealed class AgentChatProviderFactory
{
    private readonly IConfigurationService _config;
    private readonly HttpClient _httpClient;
    private readonly IAzureFoundryAuthService? _foundryAuth;

    public AgentChatProviderFactory(
        IConfigurationService config,
        HttpClient httpClient,
        IAzureFoundryAuthService? foundryAuth = null)
    {
        _config = config;
        _httpClient = httpClient;
        _foundryAuth = foundryAuth;
    }

    /// <summary>
    /// Resolve a model id to (provider, providerNativeDeploymentName).
    /// </summary>
    public async Task<(IChatProvider Provider, string DeploymentName)> ResolveAsync(string modelId, CancellationToken ct = default)
    {
        var entry = await GetModelEntryAsync(modelId, ct);
        switch (entry.Provider)
        {
            case "azure-openai":
                return (BuildAzureOpenAI(entry), entry.Deployment ?? modelId);
            case "anthropic":
                return (BuildAnthropic(entry), entry.Deployment ?? modelId);
            default:
                throw new InvalidOperationException($"Unknown provider '{entry.Provider}' for model '{modelId}'.");
        }
    }

    private IChatProvider BuildAzureOpenAI(ModelEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Endpoint))
            throw new InvalidOperationException("azure-openai model needs an endpoint.");
        var key = entry.ApiKey ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return new AzureOpenAIChatProvider(new Uri(entry.Endpoint), new AzureKeyCredential(key));
        }
        // No API key — prefer the Foundry refresh-token flow if we have stored Foundry credentials,
        // otherwise fall back to DefaultAzureCredential (works for users with az login / managed identity).
        if (_foundryAuth is not null)
        {
            return new AzureOpenAIChatProvider(new Uri(entry.Endpoint), new FoundryTokenCredential(_foundryAuth));
        }
        return new AzureOpenAIChatProvider(new Uri(entry.Endpoint), new Azure.Identity.DefaultAzureCredential());
    }

    private IChatProvider BuildAnthropic(ModelEntry entry)
    {
        var endpoint = new Uri(entry.Endpoint ?? "https://api.anthropic.com");
        var key = entry.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            return new AnthropicChatProvider(endpoint, key, _httpClient);
        }
        // No API key — if the endpoint looks like Microsoft Foundry's Claude route, fall back
        // to Entra ID auth via the stored Foundry refresh-token flow.
        if (_foundryAuth is not null && endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicChatProvider(endpoint, new FoundryTokenCredential(_foundryAuth), _httpClient);
        }
        throw new InvalidOperationException(
            "anthropic model needs an apiKey (in settings) or ANTHROPIC_API_KEY env var, "
            + "or a Foundry-served endpoint (host ending in services.ai.azure.com) with a logged-in Foundry session.");
    }

    private async Task<ModelEntry> GetModelEntryAsync(string modelId, CancellationToken ct)
    {
        // Flat key scheme over IConfigurationService:
        //   agent.models.<id>.provider   = "azure-openai" | "anthropic"
        //   agent.models.<id>.endpoint
        //   agent.models.<id>.deployment
        //   agent.models.<id>.apiKey
        var provider = await _config.GetAsync($"agent.models.{modelId}.provider");
        BuiltInDefaults.TryGetValue(modelId, out var defaults);
        if (string.IsNullOrWhiteSpace(provider) && defaults is null)
        {
            throw new InvalidOperationException($"Unknown model '{modelId}'. Configure agent.models.{modelId}.provider in ~/.pks-cli/settings.json.");
        }
        return new ModelEntry(
            Provider: provider ?? defaults!.Provider,
            Endpoint: (await _config.GetAsync($"agent.models.{modelId}.endpoint")) ?? defaults?.Endpoint,
            Deployment: (await _config.GetAsync($"agent.models.{modelId}.deployment")) ?? defaults?.Deployment,
            ApiKey: (await _config.GetAsync($"agent.models.{modelId}.apiKey")) ?? defaults?.ApiKey);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Built-in known models with sensible defaults.</summary>
    private static readonly Dictionary<string, ModelEntry> BuiltInDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5.5"] = new(
            Provider: "azure-openai",
            Endpoint: null, // user must supply (no shared default)
            Deployment: "gpt-5.5",
            ApiKey: null),
        ["claude-opus-4-7"] = new(
            Provider: "anthropic",
            Endpoint: "https://api.anthropic.com",
            Deployment: "claude-opus-4-7",
            ApiKey: null),
        ["claude-sonnet-4-6"] = new(
            Provider: "anthropic",
            Endpoint: "https://api.anthropic.com",
            Deployment: "claude-sonnet-4-6",
            ApiKey: null),
    };

    private sealed record ModelEntry(
        string Provider,
        string? Endpoint,
        string? Deployment,
        string? ApiKey);
}
