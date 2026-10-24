using PKS.Infrastructure.Services.Providers;
using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// The catalog across the providers registered with pks, and the routing table behind
/// <see cref="ProviderCatalog.ChatPath"/>. A provider is registered when pks holds a credential for
/// it: Foundry when signed in with a resource selected; Anthropic, OpenAI, Google and TypeSafe when
/// <c>pks providers init</c> / <c>pks google init</c> / <c>pks typesafe init</c> stored a key (or the
/// vendor's usual environment variable is set); Moonshot when <c>pks moonshot init</c> ran; an
/// OpenAI-compatible endpoint when <c>OPENAI_BASE_URL</c> is set — the same availability test
/// <c>pks exec</c> applies when it picks a provider.
/// </summary>
public interface IModelCatalogService
{
    /// <summary>The provider kinds that are registered right now, in catalog order.</summary>
    Task<IReadOnlyList<string>> RegisteredKindsAsync();

    /// <summary>
    /// The catalog of one provider kind, or the union of every registered one when
    /// <paramref name="kind"/> is null. A provider that cannot be listed is reported in
    /// <see cref="CatalogResult.Failures"/> rather than failing the rest. The union is cached for a
    /// couple of minutes: a tool cleaning every utterance must not fan out a listing per provider.
    /// </summary>
    Task<CatalogResult> BuildAsync(string? kind, CancellationToken ct);

    /// <summary>
    /// Where a chat-completions request for <paramref name="model"/> goes: the provider's OpenAI
    /// chat endpoint, the credential for it, and the model name that provider expects. The model is a
    /// bare catalog id — the first provider in catalog order that lists it wins — or
    /// <c>provider/id</c>, which pins the provider (the prefix is only stripped when it names a
    /// registered kind, since OpenRouter-style ids carry slashes of their own).
    /// </summary>
    Task<ChatRouteResult> RouteChatAsync(string model, CancellationToken ct);

    /// <summary>The credential pks holds for a provider kind, or <see cref="SecretValue.None"/>.</summary>
    Task<SecretValue> CredentialAsync(string kind);
}

public sealed record CatalogResult(ProviderCatalog.List Catalog, IReadOnlyList<(string Kind, string Error)> Failures);

/// <summary>One resolved chat route. The credential is a bearer token at every provider's OpenAI-compatible endpoint.</summary>
public sealed record ChatRoute(string Kind, string Model, string Url, SecretValue Credential);

/// <summary>Either a route, or the reason there is none (an OpenAI-shaped 404 at the proxy).</summary>
public sealed record ChatRouteResult(ChatRoute? Route, string? Error)
{
    public static ChatRouteResult Found(ChatRoute route) => new(route, null);
    public static ChatRouteResult NotFound(string error) => new(null, error);
}

public sealed class ModelCatalogService : IModelCatalogService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IAzureFoundryAuthService _foundryAuth;
    private readonly AzureFoundryAuthConfig _foundryConfig;
    private readonly IGoogleAiService? _google;
    private readonly IProviderApiKeyService? _keys;
    private readonly IMoonshotService? _moonshot;
    private readonly ITypeSafeCredentialService? _typeSafe;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private CatalogResult? _cached;
    private DateTimeOffset _cachedAt;
    private SecretValue _foundryToken;
    private DateTimeOffset _foundryTokenExpires;

    public ModelCatalogService(
        HttpClient http,
        IAzureFoundryAuthService foundryAuth,
        AzureFoundryAuthConfig foundryConfig,
        IGoogleAiService? google = null,
        IProviderApiKeyService? keys = null,
        IMoonshotService? moonshot = null,
        ITypeSafeCredentialService? typeSafe = null)
    {
        _http = http;
        _foundryAuth = foundryAuth;
        _foundryConfig = foundryConfig;
        _google = google;
        _keys = keys;
        _moonshot = moonshot;
        _typeSafe = typeSafe;
    }

    public async Task<IReadOnlyList<string>> RegisteredKindsAsync()
    {
        var kinds = new List<string>();
        foreach (var kind in ProviderCatalog.Kinds)
        {
            if (await IsRegisteredAsync(kind)) kinds.Add(kind);
        }
        return kinds;
    }

    private async Task<bool> IsRegisteredAsync(string kind) => kind switch
    {
        ProviderCatalog.Foundry => !string.IsNullOrEmpty((await _foundryAuth.GetStoredCredentialsAsync())?.SelectedResourceEndpoint),
        // OPENAI_API_KEY alone means api.openai.com; with OPENAI_BASE_URL it belongs to the compatible endpoint.
        ProviderCatalog.OpenAi => (await StoredKeyAsync(ApiKeyProvider.OpenAI)).HasValue
                                  || (!string.IsNullOrEmpty(Env("OPENAI_API_KEY")) && string.IsNullOrEmpty(Env("OPENAI_BASE_URL"))),
        ProviderCatalog.OpenAiCompatible => !string.IsNullOrEmpty(Env("OPENAI_BASE_URL")),
        _ => (await CredentialAsync(kind)).HasValue,
    };

    public async Task<SecretValue> CredentialAsync(string kind)
    {
        switch (kind.ToLowerInvariant())
        {
            case ProviderCatalog.Foundry:
                return await FoundryTokenAsync();
            case ProviderCatalog.Gemini:
                var google = _google is null ? null : await _google.GetApiKeyAsync();
                return SecretValue.From(string.IsNullOrEmpty(google) ? Env("GEMINI_API_KEY") : google);
            case ProviderCatalog.Anthropic:
                var anthropic = await StoredKeyAsync(ApiKeyProvider.Anthropic);
                return anthropic.HasValue ? anthropic : SecretValue.From(Env("ANTHROPIC_API_KEY"));
            case ProviderCatalog.OpenAi:
                var openai = await StoredKeyAsync(ApiKeyProvider.OpenAI);
                if (openai.HasValue) return openai;
                return string.IsNullOrEmpty(Env("OPENAI_BASE_URL")) ? SecretValue.From(Env("OPENAI_API_KEY")) : SecretValue.None;
            case ProviderCatalog.Moonshot:
                var moonshot = _moonshot is null ? null : await _moonshot.GetStoredCredentialsAsync();
                return moonshot?.ApiKey ?? SecretValue.None;
            case ProviderCatalog.TypeSafe:
                var typesafe = _typeSafe is null ? null : await _typeSafe.RevealApiKeyAsync();
                return SecretValue.From(string.IsNullOrEmpty(typesafe) ? Env("TYPESAFE_API_KEY") : typesafe);
            case ProviderCatalog.OpenAiCompatible:
                return SecretValue.From(Env("OPENAI_API_KEY"));
            default:
                return SecretValue.None;
        }
    }

    private async Task<SecretValue> StoredKeyAsync(ApiKeyProvider provider)
        => _keys is null ? SecretValue.None : await _keys.GetKeyAsync(provider);

    /// <summary>
    /// A Foundry token held for a few minutes. Redeeming the refresh token rewrites the credential
    /// store on every call, so a catalog and a chat route must not each mint their own.
    /// </summary>
    private async Task<SecretValue> FoundryTokenAsync()
    {
        if (_foundryToken.HasValue && DateTimeOffset.UtcNow < _foundryTokenExpires) return _foundryToken;
        var token = SecretValue.From(await _foundryAuth.GetAccessTokenAsync(_foundryConfig.CognitiveScope));
        if (token.HasValue)
        {
            _foundryToken = token;
            _foundryTokenExpires = DateTimeOffset.UtcNow + TokenLifetime;
        }
        return token;
    }

    public async Task<CatalogResult> BuildAsync(string? kind, CancellationToken ct)
    {
        var registered = await RegisteredKindsAsync();
        if (kind is not null && !registered.Contains(kind, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"provider '{kind}' is not registered ({Hint(kind)})");

        var union = await UnionAsync(registered, ct);
        if (kind is null) return union;

        return new CatalogResult(
            new ProviderCatalog.List { Data = union.Catalog.Data.Where(e => string.Equals(e.Pks.Provider, kind, StringComparison.OrdinalIgnoreCase)).ToList() },
            union.Failures.Where(f => string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private async Task<CatalogResult> UnionAsync(IReadOnlyList<string> registered, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime) return _cached;

            var lists = new List<ProviderCatalog.List>();
            var failures = new List<(string, string)>();
            foreach (var k in registered)
            {
                try
                {
                    lists.Add(await BuildOneAsync(k, ct));
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    failures.Add((k, ex.Message));
                }
            }
            var result = new CatalogResult(ProviderCatalog.Merge(lists), failures);
            // A listing that failed everywhere is not worth remembering: the next call retries.
            if (result.Catalog.Data.Count > 0)
            {
                _cached = result;
                _cachedAt = DateTimeOffset.UtcNow;
            }
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ProviderCatalog.List> BuildOneAsync(string kind, CancellationToken ct)
    {
        var credential = await CredentialAsync(kind);
        switch (kind)
        {
            case ProviderCatalog.Foundry:
                var creds = await _foundryAuth.GetStoredCredentialsAsync()
                    ?? throw new InvalidOperationException("no Foundry credentials");
                if (!credential.HasValue)
                    throw new InvalidOperationException("could not obtain a Foundry token; try pks foundry init --force");
                return await ProviderCatalog.BuildFoundryAsync(_http, creds.SelectedResourceEndpoint, credential, creds, ct);
            case ProviderCatalog.Gemini:
                return await ProviderCatalog.BuildGeminiAsync(_http, credential, ct);
            case ProviderCatalog.Anthropic:
                return await ProviderCatalog.BuildAnthropicAsync(_http, credential, ct);
            case ProviderCatalog.OpenAi:
                return await ProviderCatalog.BuildOpenAiCompatibleAsync(_http, ProviderCatalog.OpenAiBaseUrl, credential, ct, ProviderCatalog.OpenAi);
            case ProviderCatalog.Moonshot:
                return await ProviderCatalog.BuildOpenAiCompatibleAsync(_http, MoonshotService.BaseUrl, credential, ct, ProviderCatalog.Moonshot);
            case ProviderCatalog.TypeSafe:
                return await ProviderCatalog.BuildTypeSafeAsync(_http, TypeSafeCredentialService.BaseUrl, credential, ct);
            case ProviderCatalog.OpenAiCompatible:
                return await ProviderCatalog.BuildOpenAiCompatibleAsync(_http, Env("OPENAI_BASE_URL") ?? "", credential, ct);
            default:
                throw new InvalidOperationException($"unknown provider kind '{kind}'");
        }
    }

    public async Task<ChatRouteResult> RouteChatAsync(string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            return ChatRouteResult.NotFound("the request names no model");

        var registered = await RegisteredKindsAsync();
        string? pinned = null;
        var id = model.Trim();
        var slash = id.IndexOf('/');
        if (slash > 0)
        {
            var prefix = id[..slash];
            var match = registered.FirstOrDefault(k => string.Equals(k, prefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                pinned = match;
                id = id[(slash + 1)..];
            }
            else if (ProviderCatalog.Kinds.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                return ChatRouteResult.NotFound($"provider '{prefix}' is not registered with pks ({Hint(prefix)})");
            }
        }

        var union = await UnionAsync(registered, ct);
        var entry = union.Catalog.Data.FirstOrDefault(e =>
            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)
            && (pinned is null || string.Equals(e.Pks.Provider, pinned, StringComparison.OrdinalIgnoreCase)));
        if (entry is null)
        {
            var where = pinned is null ? "the pks catalog" : $"the pks catalog for '{pinned}'";
            var unlisted = union.Failures.Count == 0 ? "" : $" (not listed: {string.Join(", ", union.Failures.Select(f => f.Kind))})";
            return ChatRouteResult.NotFound($"model '{model}' is not in {where}{unlisted}");
        }

        var kind = entry.Pks.Provider;
        var url = ProviderCatalog.ChatCompletionsUrl(kind, await BaseUrlAsync(kind));
        if (url is null)
            return ChatRouteResult.NotFound($"model '{model}' is a {entry.Architecture.Modality} model at {kind}; it judges through /v1/systemone and has no chat route");

        var credential = await CredentialAsync(kind);
        if (!credential.HasValue)
            return ChatRouteResult.NotFound($"no credential for provider '{kind}' ({Hint(kind)})");
        return ChatRouteResult.Found(new ChatRoute(kind, entry.Id, url, credential));
    }

    private async Task<string?> BaseUrlAsync(string kind) => kind switch
    {
        ProviderCatalog.Foundry => (await _foundryAuth.GetStoredCredentialsAsync())?.SelectedResourceEndpoint,
        ProviderCatalog.Moonshot => MoonshotService.BaseUrl,
        ProviderCatalog.OpenAiCompatible => Env("OPENAI_BASE_URL"),
        _ => null,
    };

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static string Hint(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderCatalog.Foundry => "sign in with pks foundry init",
        ProviderCatalog.Gemini => "pks google init, or set GEMINI_API_KEY",
        ProviderCatalog.Anthropic => "pks providers init --only anthropic, or set ANTHROPIC_API_KEY",
        ProviderCatalog.OpenAi => "pks providers init --only openai, or set OPENAI_API_KEY",
        ProviderCatalog.Moonshot => "pks moonshot init",
        ProviderCatalog.TypeSafe => "pks typesafe init",
        ProviderCatalog.OpenAiCompatible => "set OPENAI_BASE_URL, and OPENAI_API_KEY where the endpoint wants one",
        _ => "known kinds: " + string.Join(", ", ProviderCatalog.Kinds),
    };
}
