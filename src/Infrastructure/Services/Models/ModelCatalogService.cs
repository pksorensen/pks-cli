using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// The catalog across the providers registered with pks: Foundry when signed in with a resource
/// selected, Gemini when <c>GEMINI_API_KEY</c> is set, an OpenAI-compatible endpoint when
/// <c>OPENAI_BASE_URL</c> is set — the same availability test <c>pks exec</c> applies.
/// </summary>
public interface IModelCatalogService
{
    /// <summary>The provider kinds that are registered right now, in catalog order.</summary>
    Task<IReadOnlyList<string>> RegisteredKindsAsync();

    /// <summary>
    /// The catalog of one provider kind, or the union of every registered one when
    /// <paramref name="kind"/> is null. A provider that cannot be listed is reported in
    /// <see cref="CatalogResult.Failures"/> rather than failing the rest.
    /// </summary>
    Task<CatalogResult> BuildAsync(string? kind, CancellationToken ct);
}

public sealed record CatalogResult(ProviderCatalog.List Catalog, IReadOnlyList<(string Kind, string Error)> Failures);

public sealed class ModelCatalogService : IModelCatalogService
{
    private readonly HttpClient _http;
    private readonly IAzureFoundryAuthService _foundryAuth;
    private readonly AzureFoundryAuthConfig _foundryConfig;

    public ModelCatalogService(HttpClient http, IAzureFoundryAuthService foundryAuth, AzureFoundryAuthConfig foundryConfig)
    {
        _http = http;
        _foundryAuth = foundryAuth;
        _foundryConfig = foundryConfig;
    }

    public async Task<IReadOnlyList<string>> RegisteredKindsAsync()
    {
        var kinds = new List<string>();
        var creds = await _foundryAuth.GetStoredCredentialsAsync();
        if (creds != null && !string.IsNullOrEmpty(creds.SelectedResourceEndpoint)) kinds.Add(ProviderCatalog.Foundry);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))) kinds.Add(ProviderCatalog.Gemini);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_BASE_URL"))) kinds.Add(ProviderCatalog.OpenAiCompatible);
        return kinds;
    }

    public async Task<CatalogResult> BuildAsync(string? kind, CancellationToken ct)
    {
        var registered = await RegisteredKindsAsync();
        var wanted = kind is null ? registered : registered.Where(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kind is not null && wanted.Count == 0)
            throw new InvalidOperationException($"provider '{kind}' is not registered ({Hint(kind)})");

        var lists = new List<ProviderCatalog.List>();
        var failures = new List<(string, string)>();
        foreach (var k in wanted)
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
        return new CatalogResult(ProviderCatalog.Merge(lists), failures);
    }

    private async Task<ProviderCatalog.List> BuildOneAsync(string kind, CancellationToken ct)
    {
        switch (kind)
        {
            case ProviderCatalog.Foundry:
                var creds = await _foundryAuth.GetStoredCredentialsAsync()
                    ?? throw new InvalidOperationException("no Foundry credentials");
                var token = await _foundryAuth.GetAccessTokenAsync(_foundryConfig.CognitiveScope);
                if (string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("could not obtain a Foundry token; try pks foundry init --force");
                return await ProviderCatalog.BuildFoundryAsync(_http, creds.SelectedResourceEndpoint, SecretValue.From(token), creds, ct);
            case ProviderCatalog.Gemini:
                return await ProviderCatalog.BuildGeminiAsync(_http, SecretValue.From(Environment.GetEnvironmentVariable("GEMINI_API_KEY")), ct);
            case ProviderCatalog.OpenAiCompatible:
                return await ProviderCatalog.BuildOpenAiCompatibleAsync(_http,
                    Environment.GetEnvironmentVariable("OPENAI_BASE_URL") ?? "",
                    SecretValue.From(Environment.GetEnvironmentVariable("OPENAI_API_KEY")), ct);
            default:
                throw new InvalidOperationException($"unknown provider kind '{kind}'");
        }
    }

    private static string Hint(string kind) => kind.ToLowerInvariant() switch
    {
        ProviderCatalog.Foundry => "sign in with pks foundry init",
        ProviderCatalog.Gemini => "set GEMINI_API_KEY",
        ProviderCatalog.OpenAiCompatible => "set OPENAI_BASE_URL, and OPENAI_API_KEY where the endpoint wants one",
        _ => "known kinds: foundry, gemini, openai-compatible",
    };
}
