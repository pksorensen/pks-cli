using System.Net.Http.Headers;
using System.Text.Json;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

public interface IMoonshotService
{
    Task<bool> IsAuthenticatedAsync();
    Task<MoonshotStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(MoonshotStoredCredentials credentials);
    Task ClearStoredCredentialsAsync();
    Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
}

public sealed class MoonshotService : IMoonshotService
{
    public const string BaseUrl = "https://api.moonshot.ai/v1";
    private const string StorageKey = "moonshot.auth.credentials";
    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configuration;

    private readonly ISecretResolver _secrets;

    public MoonshotService(HttpClient httpClient, IConfigurationService configuration, ISecretResolver secrets)
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

    public async Task<MoonshotStoredCredentials?> GetStoredCredentialsAsync()
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<MoonshotStoredCredentials>(json, SecretJson.Persistence);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task StoreCredentialsAsync(MoonshotStoredCredentials credentials) =>
        _configuration.SetAsync(StorageKey, JsonSerializer.Serialize(credentials, SecretJson.Persistence), global: true);

    public Task ClearStoredCredentialsAsync() => _configuration.DeleteAsync(StorageKey);

    public async Task<bool> ValidateApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
