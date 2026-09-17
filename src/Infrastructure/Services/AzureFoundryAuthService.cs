using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for Azure AI Foundry OAuth2 authentication using authorization code + PKCE
/// </summary>
public interface IAzureFoundryAuthService
{
    Task<string?> DiscoverTenantAsync(string email, CancellationToken cancellationToken = default);
    Task<FoundryAuthResult> InitiateLoginAsync(string tenantId, string? loginHint = null, string? scopeOverride = null, CancellationToken cancellationToken = default);
    Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default);
    Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<List<CognitiveServicesAccount>> ListFoundryResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default);
    Task<List<AppInsightsComponent>> ListAppInsightsResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default);
    Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default);
    Task<List<FoundryDeployment>> ListDeploymentsAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync();
    Task<FoundryStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(FoundryStoredCredentials credentials);
    Task ClearCredentialsAsync();

    /// <summary>
    /// Writes a <c>~/.pks-cli/settings.json</c> carrying this machine's stored Foundry credential,
    /// for delivery to another machine over a pipe. Returns false when nothing is stored.
    ///
    /// It exists so a *command* can hand the credential to an ssh stdin without ever holding the
    /// plaintext — <c>SecretResolverGateTests</c> fails the build if anything under
    /// <c>src/Commands/</c> so much as names <c>Reveal(</c>, and that gate is the point, not an
    /// obstacle to route around. The receiving pks migrates the plaintext into its own AES-GCM
    /// store on first load and blanks the file, so what lands on disk there is short-lived.
    /// </summary>
    Task<bool> WriteRemoteSettingsAsync(TextWriter writer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure AI Foundry OAuth2 authentication using authorization code flow with PKCE.
/// Uses the Azure CLI well-known public client ID — no app registration needed.
/// The PKCE / token-endpoint mechanics live in <see cref="AzurePublicClientAuth"/>; this class
/// owns what is Foundry-specific: the stored credential and the ARM listings.
/// </summary>
public class AzureFoundryAuthService : IAzureFoundryAuthService
{
    private const string StorageKey = "foundry.auth.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<AzureFoundryAuthService> _logger;
    private readonly AzureFoundryAuthConfig _config;
    private readonly AzurePublicClientAuth _auth;

    public AzureFoundryAuthService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureFoundryAuthService> logger,
        ISecretResolver secrets,
        AzureFoundryAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureFoundryAuthConfig();
        _secrets = secrets;
        _auth = new AzurePublicClientAuth(
            httpClient, _config.ClientId, _config.AuthorizeUrl, _config.TokenUrl, _config.CallbackTimeoutSeconds, logger);
    }

    public Task<string?> DiscoverTenantAsync(string email, CancellationToken cancellationToken = default)
        => _auth.DiscoverTenantAsync(email, cancellationToken);

    public async Task<FoundryAuthResult> InitiateLoginAsync(string tenantId, string? loginHint = null, string? scopeOverride = null, CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrWhiteSpace(scopeOverride) ? _config.InitialScope : scopeOverride;
        var login = await _auth.LoginInteractiveAsync(tenantId, scope, loginHint, cancellationToken);

        return new FoundryAuthResult
        {
            AccessToken = login.AccessToken,
            RefreshToken = login.RefreshToken.Reveal(),
            ExpiresIn = login.ExpiresIn,
            TenantId = tenantId
        };
    }

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || !credentials.RefreshToken.HasValue)
        {
            _logger.LogWarning("Cannot refresh Foundry token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var refreshed = await _auth.RedeemRefreshTokenAsync(credentials.TenantId, credentials.RefreshToken, scope, cancellationToken);

            // Update stored refresh token if rotated
            if (refreshed.NewRefreshToken is { HasValue: true } rotated)
            {
                credentials.RefreshToken = rotated;
            }
            credentials.LastRefreshedAt = DateTime.UtcNow;

            // Persistence options, or the refresh token would be written to the store as "***" and
            // come back absent on the next load — a silent logout an hour later.
            var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
            await _configurationService.SetAsync(StorageKey, json, global: true);

            return refreshed.AccessToken;
        }
        catch (AzureRefreshTokenExpiredException)
        {
            _logger.LogError("Foundry refresh token has expired or been revoked. Re-auth with: pks foundry login");
            return null;
        }
        catch (AzureTokenEndpointException ex)
        {
            _logger.LogError("Foundry token refresh failed: {StatusCode} {Response}", ex.StatusCode, ex.ResponseBody);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry token refresh failed with exception");
            return null;
        }
    }

    public Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default)
        => AzureArmRequests.ListSubscriptionsAsync(_httpClient, accessToken, cancellationToken);

    public async Task<List<CognitiveServicesAccount>> ListFoundryResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
    {
        var allAccounts = await AzureArmRequests.ListCognitiveServicesAccountsAsync(_httpClient, accessToken, subscriptionId, cancellationToken);

        // Filter to AI Foundry resources: Kind contains "AIServices" or endpoint contains ".services.ai.azure.com"
        return allAccounts.Where(a =>
            a.Kind.Contains("AIServices", StringComparison.OrdinalIgnoreCase) ||
            a.Properties.Endpoint.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    public Task<List<AppInsightsComponent>> ListAppInsightsResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
        => AzureArmRequests.ListAppInsightsComponentsAsync(_httpClient, accessToken, subscriptionId, cancellationToken);

    public Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
        => AzureArmRequests.ListLogAnalyticsWorkspacesAsync(_httpClient, accessToken, subscriptionId, cancellationToken);

    public Task<List<FoundryDeployment>> ListDeploymentsAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
        => AzureArmRequests.ListDeploymentsAsync(_httpClient, accessToken, subscriptionId, resourceGroup, accountName, cancellationToken);

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials != null && credentials.RefreshToken.HasValue;
    }

    public async Task<FoundryStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var json = await _secrets.RevealAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            return JsonSerializer.Deserialize<FoundryStoredCredentials>(json, SecretJson.Persistence);
        }
        catch
        {
            return null;
        }
    }

    public async Task StoreCredentialsAsync(FoundryStoredCredentials credentials)
    {
        // The one place Foundry credentials are written unmasked, and it writes into the encrypted
        // store. Everywhere else a FoundryStoredCredentials serializes to "***" on purpose.
        var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
        await _configurationService.SetAsync(StorageKey, json, global: true);
    }

    public async Task<bool> WriteRemoteSettingsAsync(TextWriter writer, CancellationToken cancellationToken = default)
    {
        var json = await _secrets.RevealAsync(StorageKey);
        if (string.IsNullOrWhiteSpace(json)) return false;

        // The receiving side reads settings.json and migrates any key that names credential
        // material into its encrypted store, so the shape has to be exactly what it writes: the
        // whole serialized blob as a *string* value under the same key.
        var doc = new System.Text.Json.Nodes.JsonObject
        {
            [StorageKey] = json,
            // Otherwise the first thing pks does on that box is print a disclaimer and ask for a
            // y/n — under systemd stdin is /dev/null, so it takes the default and continues, but
            // the prompt lands in the journal of every unit and reads like a hang.
            ["cli.first-time-warning-acknowledged"] = "true",
        };

        // "\n", never WriteLine: TextWriter.NewLine follows the *sending* machine, and this is on
        // its way to a POSIX host. See SecretSink.
        await writer.WriteAsync(doc.ToJsonString() + "\n");
        await writer.FlushAsync(cancellationToken);
        return true;
    }

    public async Task ClearCredentialsAsync()
    {
        await _configurationService.DeleteAsync(StorageKey);
    }
}
