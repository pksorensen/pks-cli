using System.Net;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Providers;

/// <summary>
/// Host-wide API keys for the model vendors whose keys an agentic runner hands straight to a job:
/// an Anthropic Console key becomes the job's <c>ANTHROPIC_API_KEY</c>, an OpenAI platform key its
/// <c>OPENAI_API_KEY</c> (codex). Stored once per host by `pks providers init`, used by every project
/// the host runs — which is the point on a self-hosted box.
///
/// Callers get a <see cref="SecretValue"/>, never a string, so the command layer can forward a key
/// through <see cref="SecretSink"/> without reading it.
/// </summary>
public interface IProviderApiKeyService
{
    Task<bool> HasKeyAsync(ApiKeyProvider provider);

    /// <summary>The stored key, or <see cref="SecretValue.None"/>.</summary>
    Task<SecretValue> GetKeyAsync(ApiKeyProvider provider);

    /// <summary>Asks the vendor whether the key works, by listing models with it.</summary>
    Task<ApiKeyCheck> ValidateAsync(ApiKeyProvider provider, string apiKey, CancellationToken cancellationToken = default);

    Task StoreAsync(ApiKeyProvider provider, string apiKey);

    /// <summary>True when <paramref name="apiKey"/> is exactly the stored key — what makes re-running
    /// `pks providers init --from-env` a no-op instead of a re-validation and a rewrite.</summary>
    Task<bool> IsStoredKeyAsync(ApiKeyProvider provider, string apiKey);
}

public enum ApiKeyProvider
{
    Anthropic,
    OpenAI,
}

public enum ApiKeyVerdict
{
    Valid,
    /// <summary>The vendor said no (401/403). Never stored.</summary>
    Rejected,
    /// <summary>Could not tell — network, 5xx, rate limit. Stored with a warning: a box being set up
    /// behind a flaky link should not lose the key the operator just typed.</summary>
    Inconclusive,
}

public sealed record ApiKeyCheck(ApiKeyVerdict Verdict, int? StatusCode, string? Detail)
{
    public bool IsValid => Verdict == ApiKeyVerdict.Valid;
}

public sealed class ProviderApiKeyService : IProviderApiKeyService
{
    // Both end in api_key, so SecretKeys classifies them as credentials and `pks secrets` masks them.
    public const string AnthropicKey = "providers.anthropic.api_key";
    public const string OpenAIKey = "providers.openai.api_key";

    private readonly HttpClient _http;
    private readonly ISecretStore _store;
    private readonly ISecretResolver _resolver;
    private readonly ILogger<ProviderApiKeyService> _logger;

    public ProviderApiKeyService(HttpClient http, ISecretStore store, ISecretResolver resolver, ILogger<ProviderApiKeyService> logger)
    {
        _http = http;
        _store = store;
        _resolver = resolver;
        _logger = logger;
    }

    public static string StorageKey(ApiKeyProvider provider) => provider switch
    {
        ApiKeyProvider.Anthropic => AnthropicKey,
        ApiKeyProvider.OpenAI => OpenAIKey,
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public Task<bool> HasKeyAsync(ApiKeyProvider provider) => _store.HasAsync(StorageKey(provider));

    public async Task<SecretValue> GetKeyAsync(ApiKeyProvider provider) =>
        SecretValue.From(await _resolver.RevealAsync(StorageKey(provider)));

    public async Task<bool> IsStoredKeyAsync(ApiKeyProvider provider, string apiKey)
    {
        var stored = await _resolver.RevealAsync(StorageKey(provider));
        return stored is not null && string.Equals(stored, apiKey.Trim(), StringComparison.Ordinal);
    }

    public Task StoreAsync(ApiKeyProvider provider, string apiKey) =>
        _store.SetAsync(StorageKey(provider), apiKey.Trim());

    public async Task<ApiKeyCheck> ValidateAsync(ApiKeyProvider provider, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = provider switch
        {
            ApiKeyProvider.Anthropic => AnthropicModels(apiKey.Trim()),
            ApiKeyProvider.OpenAI => OpenAIModels(apiKey.Trim()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.SendAsync(request, cts.Token);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return new ApiKeyCheck(ApiKeyVerdict.Valid, status, null);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new ApiKeyCheck(ApiKeyVerdict.Rejected, status, "the key was refused");
            return new ApiKeyCheck(ApiKeyVerdict.Inconclusive, status, $"HTTP {status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("Validating the {Provider} key failed: {Message}", provider, ex.Message);
            return new ApiKeyCheck(ApiKeyVerdict.Inconclusive, null, ex.Message);
        }
    }

    private static HttpRequestMessage AnthropicModels(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models?limit=1");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    private static HttpRequestMessage OpenAIModels(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }
}
