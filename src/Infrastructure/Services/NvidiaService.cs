using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

public interface INvidiaService
{
    Task<bool> IsAuthenticatedAsync();
    Task<NvidiaStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(NvidiaStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();
    Task<NvidiaValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
    Task<NvidiaValidationResult> ValidateStoredKeyAsync(CancellationToken cancellationToken = default);
}

public sealed class NvidiaService : INvidiaService
{
    public const string BaseUrl = "https://integrate.api.nvidia.com/v1";
    private const string StorageKey = "nvidia.auth.credentials";

    /// <summary>
    /// The model the credential probe runs one token against.
    ///
    /// It has to be a real, currently-served model: NVIDIA routes by model first, so a made-up name
    /// answers 404 with or without a valid key — the "probe with a nonsense model so nothing is
    /// billed" trick does not work here (verified 2026-08-15). Llama 3.1 8B is the smallest thing on
    /// the catalogue that has been there throughout, and one token of it is the cheapest honest
    /// question we can ask.
    /// </summary>
    private const string ProbeModel = "meta/llama-3.1-8b-instruct";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configuration;
    private readonly ISecretResolver _secrets;

    public NvidiaService(HttpClient httpClient, IConfigurationService configuration, ISecretResolver secrets)
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

    public async Task<NvidiaStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<NvidiaStoredCredentials>(json, SecretJson.Persistence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StoreCredentialsAsync(NvidiaStoredCredentials credentials) =>
        _configuration.SetAsync(StorageKey, JsonSerializer.Serialize(credentials, SecretJson.Persistence), global: true);

    public Task ClearStoredCredentialsAsync() => _configuration.DeleteAsync(StorageKey);

    /// <summary>
    /// Validates by asking for one token of completion, because NVIDIA offers nothing cheaper that
    /// is actually authenticated. <c>GET /v1/models</c> is a public catalogue — it answers 200 with
    /// no credentials at all, and 200 again for a garbage bearer (verified 2026-08-15) — so the
    /// usual "call /models and trust 2xx" check would accept any string. There is no OpenRouter-style
    /// <c>/key</c> endpoint to fall back on.
    /// </summary>
    public Task<NvidiaValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default) =>
        ProbeAsync(request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey), cancellationToken);

    public async Task<NvidiaValidationResult> ValidateStoredKeyAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials?.ApiKey.HasValue != true)
            return new NvidiaValidationResult(NvidiaKeyVerdict.Rejected, null, "No API key stored");

        return await ProbeAsync(request => SecretSink.SetBearerToken(request, credentials.ApiKey), cancellationToken);
    }

    private async Task<NvidiaValidationResult> ProbeAsync(
        Action<HttpRequestMessage> authenticate,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = ProbeModel,
            max_tokens = 1,
            temperature = 0.0,
            messages = new[] { new { role = "user", content = "hi" } },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        authenticate(request);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return new NvidiaValidationResult(NvidiaKeyVerdict.Valid, (int)response.StatusCode, null);

            var verdict = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? NvidiaKeyVerdict.Rejected
                : NvidiaKeyVerdict.Inconclusive;

            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            return new NvidiaValidationResult(verdict, (int)response.StatusCode, Truncate(detail));
        }
        catch (HttpRequestException exception)
        {
            return new NvidiaValidationResult(NvidiaKeyVerdict.Inconclusive, null, exception.Message);
        }
        catch (TaskCanceledException exception)
        {
            return new NvidiaValidationResult(NvidiaKeyVerdict.Inconclusive, null, exception.Message);
        }
    }

    private static string Truncate(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" :
        value.Length <= 200 ? value.Trim() : value[..200].Trim() + "…";
}
