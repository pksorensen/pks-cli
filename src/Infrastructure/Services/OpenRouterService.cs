using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

public interface IOpenRouterService
{
    Task<bool> IsAuthenticatedAsync();
    Task<OpenRouterStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(OpenRouterStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();

    /// <summary>Asks OpenRouter what the key is. Returns null when the key is rejected.</summary>
    Task<OpenRouterKeyInfo?> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>Same question, asked with the stored key. Null when nothing is stored or it no longer works.</summary>
    Task<OpenRouterKeyInfo?> GetStoredKeyInfoAsync(CancellationToken cancellationToken = default);
}

public sealed class OpenRouterService : IOpenRouterService
{
    public const string BaseUrl = "https://openrouter.ai/api/v1";
    private const string StorageKey = "openrouter.auth.credentials";
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configuration;
    private readonly ISecretResolver _secrets;

    public OpenRouterService(HttpClient httpClient, IConfigurationService configuration, ISecretResolver secrets)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _secrets = secrets;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials?.ApiKey.HasValue == true;
    }

    public async Task<OpenRouterStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<OpenRouterStoredCredentials>(json, SecretJson.Persistence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StoreCredentialsAsync(OpenRouterStoredCredentials credentials) =>
        _configuration.SetAsync(StorageKey, JsonSerializer.Serialize(credentials, SecretJson.Persistence), global: true);

    public Task ClearStoredCredentialsAsync() => _configuration.DeleteAsync(StorageKey);

    /// <summary>
    /// Validates against <c>/key</c>, not <c>/models</c>.
    ///
    /// This is the one place OpenRouter differs from every other provider in here, and the
    /// difference is silent: <c>GET https://openrouter.ai/api/v1/models</c> is a public catalogue
    /// that answers <c>200 OK</c> with no credentials at all (verified 2026-08-14). Copying the
    /// Moonshot shape — send the key to <c>/models</c>, treat 2xx as valid — would therefore accept
    /// a typo, an empty-ish string, or someone's Anthropic key, store it, and only surface the
    /// mistake later as a 401 from a completions call somewhere else entirely.
    ///
    /// <c>/key</c> is authenticated (401 unauthenticated) and describes the key that signed the
    /// request, which is both a real check and the material for the confirmation table.
    /// </summary>
    public async Task<OpenRouterKeyInfo?> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await SendKeyRequestAsync(request, cancellationToken);
    }

    public async Task<OpenRouterKeyInfo?> GetStoredKeyInfoAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials?.ApiKey.HasValue != true) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/key");
        SecretSink.SetBearerToken(request, credentials.ApiKey);
        return await SendKeyRequestAsync(request, cancellationToken);
    }

    private async Task<OpenRouterKeyInfo?> SendKeyRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<OpenRouterKeyResponse>(body)?.Data;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
