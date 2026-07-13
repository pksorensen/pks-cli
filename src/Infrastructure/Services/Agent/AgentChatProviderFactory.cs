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

    /// <summary>
    /// Enumerate the model ids this Runner can actually serve right now — the union of built-in
    /// defaults, custom <c>agent.models.&lt;id&gt;</c> settings entries, and Foundry-enabled models,
    /// filtered to those whose credentials/endpoint are currently satisfiable (see
    /// <see cref="CanResolveAsync"/>). Used to populate a web UI /model picker. Order is stable
    /// (first-seen across the three sources) and ids are deduped case-insensitively.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct = default)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                candidates.Add(id);
        }

        // 1. Built-in known models.
        foreach (var id in BuiltInDefaults.Keys)
            Add(id);

        // 2. Custom model ids configured directly in settings (agent.models.<id>.provider).
        const string prefix = "agent.models.";
        const string suffix = ".provider";
        var all = await _config.GetAllAsync();
        foreach (var key in all.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && key.EndsWith(suffix, StringComparison.Ordinal)
                && key.Length > prefix.Length + suffix.Length)
            {
                Add(key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length));
            }
        }

        // 3. Models the user enabled via `pks foundry init` / `pks foundry select`.
        if (_foundryAuth is not null)
        {
            var creds = await _foundryAuth.GetStoredCredentialsAsync();
            if (creds is not null && creds.EnabledModels.Count > 0)
            {
                foreach (var id in creds.EnabledModels)
                    Add(id);
            }
        }

        // 4. Keep only ids that would resolve to a usable provider right now.
        var available = new List<string>();
        foreach (var id in candidates)
        {
            if (await CanResolveAsync(id, ct))
                available.Add(id);
        }

        return available;
    }

    /// <summary>
    /// True if <paramref name="modelId"/> would resolve to a usable provider right now, mirroring the
    /// preconditions <see cref="BuildAzureOpenAI"/>/<see cref="BuildAnthropic"/> enforce — without
    /// constructing a provider or making a network call. An azure-openai model needs an endpoint; an
    /// anthropic model needs an apiKey, the <c>ANTHROPIC_API_KEY</c> env var, or a Foundry-served
    /// Claude endpoint.
    /// </summary>
    private async Task<bool> CanResolveAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var entry = await GetModelEntryAsync(modelId, ct);
            return entry.Provider switch
            {
                "azure-openai" => !string.IsNullOrWhiteSpace(entry.Endpoint),
                "anthropic" => !string.IsNullOrWhiteSpace(entry.ApiKey)
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
                    || (_foundryAuth is not null && entry.Endpoint is not null
                        && new Uri(entry.Endpoint).Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }
        catch
        {
            return false;
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
        // Endpoint precedence: explicit settings entry → ANTHROPIC_BASE_URL env
        // (lets the runner point chat-llm at the pks-agent-gateway LLM sim the
        // same way the spawned claude is pointed at it) → the real API.
        var endpoint = new Uri(entry.Endpoint
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
            ?? "https://api.anthropic.com");
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

        var resolvedProvider = provider ?? defaults!.Provider;
        var endpoint = (await _config.GetAsync($"agent.models.{modelId}.endpoint")) ?? defaults?.Endpoint;
        var deployment = (await _config.GetAsync($"agent.models.{modelId}.deployment")) ?? defaults?.Deployment;
        var apiKey = (await _config.GetAsync($"agent.models.{modelId}.apiKey")) ?? defaults?.ApiKey;

        // Foundry fallback: an azure-openai model with no explicitly configured
        // endpoint resolves to the Foundry-selected resource from `pks foundry init`
        // (the same credentials the codex proxy and image generator use). This lets
        // `pks agent --model gpt-5.5` work out of the box for any logged-in Foundry
        // user — no manual agent.models.* endpoint wiring required. An explicit
        // settings.json endpoint always wins.
        if (string.Equals(resolvedProvider, "azure-openai", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(endpoint)
            && _foundryAuth is not null)
        {
            var creds = await _foundryAuth.GetStoredCredentialsAsync();
            if (creds is not null && !string.IsNullOrWhiteSpace(creds.SelectedResourceEndpoint))
            {
                endpoint = creds.SelectedResourceEndpoint;
                // Prefer a stored Foundry API key; otherwise BuildAzureOpenAI falls
                // back to the FoundryTokenCredential bearer flow.
                apiKey ??= creds.ApiKey;
            }
        }

        return new ModelEntry(
            Provider: resolvedProvider,
            Endpoint: endpoint,
            Deployment: deployment,
            ApiKey: apiKey);
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
