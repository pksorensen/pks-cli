using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

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
    Task<List<FoundryDeployment>> ListDeploymentsAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync();
    Task<FoundryStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(FoundryStoredCredentials credentials);
    Task ClearCredentialsAsync();
}

/// <summary>
/// Azure AI Foundry OAuth2 authentication using authorization code flow with PKCE.
/// Uses the Azure CLI well-known public client ID — no app registration needed.
/// </summary>
public class AzureFoundryAuthService : IAzureFoundryAuthService
{
    private const string StorageKey = "foundry.auth.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AzureFoundryAuthService> _logger;
    private readonly AzureFoundryAuthConfig _config;

    public AzureFoundryAuthService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureFoundryAuthService> logger,
        AzureFoundryAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureFoundryAuthConfig();
    }

    public async Task<string?> DiscoverTenantAsync(string email, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://login.microsoftonline.com/common/userrealm/{Uri.EscapeDataString(email)}?api-version=1.0";
            var response = await _httpClient.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tenant discovery failed: {StatusCode}", response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // The userrealm endpoint returns different fields depending on account type:
            // Managed (cloud): "NameSpaceType": "Managed", with tenant info
            // Federated: "NameSpaceType": "Federated", with federation metadata
            // Both return a "DomainName" field we can use to get the tenant

            // Try to extract tenant from the domain via OpenID discovery
            var domain = root.TryGetProperty("DomainName", out var domainProp) ? domainProp.GetString() : null;
            if (string.IsNullOrEmpty(domain))
                return null;

            // Use OpenID configuration to get the tenant ID from the issuer
            var openIdUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(domain)}/.well-known/openid-configuration";
            var openIdResponse = await _httpClient.GetAsync(openIdUrl, cancellationToken);
            var openIdContent = await openIdResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!openIdResponse.IsSuccessStatusCode)
                return domain; // Fall back to using domain as tenant identifier

            using var openIdDoc = JsonDocument.Parse(openIdContent);
            var issuer = openIdDoc.RootElement.TryGetProperty("issuer", out var issuerProp) ? issuerProp.GetString() : null;

            // Issuer format: https://sts.windows.net/{tenant-id}/ or https://login.microsoftonline.com/{tenant-id}/v2.0
            if (!string.IsNullOrEmpty(issuer))
            {
                var parts = issuer.TrimEnd('/').Split('/');
                var tenantId = parts[^1];
                // If it ends with "v2.0", go one more level up
                if (tenantId == "v2.0" && parts.Length >= 2)
                    tenantId = parts[^2];
                if (!string.IsNullOrEmpty(tenantId))
                    return tenantId;
            }

            return domain; // Fall back to domain as tenant
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant discovery failed for email: {Email}", email);
            return null;
        }
    }

    public async Task<FoundryAuthResult> InitiateLoginAsync(string tenantId, string? loginHint = null, string? scopeOverride = null, CancellationToken cancellationToken = default)
    {
        var scope = string.IsNullOrWhiteSpace(scopeOverride) ? _config.InitialScope : scopeOverride;
        var pkce = GeneratePkce();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}";

        var authorizeUrl = $"{_config.GetAuthorizeUrl(tenantId)}" +
            $"?client_id={Uri.EscapeDataString(_config.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(pkce.CodeChallenge)}" +
            $"&code_challenge_method=S256" +
            $"&prompt=select_account";

        // Pre-fill the email in the account picker if provided
        if (!string.IsNullOrEmpty(loginHint))
            authorizeUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

        // Start listener BEFORE opening browser to avoid race condition
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        // Print URL so it's clickable in terminals
        Console.WriteLine(authorizeUrl);

        // Try to open the browser
        TryOpenBrowser(authorizeUrl);

        // Wait for the callback
        var code = await WaitForCallbackAsync(listener, state, cancellationToken);

        // Exchange code for tokens
        var tokenResponse = await ExchangeCodeForTokensAsync(code, redirectUri, pkce.CodeVerifier, tenantId, scope, cancellationToken);

        return new FoundryAuthResult
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresIn = tokenResponse.ExpiresIn,
            TenantId = tenantId
        };
    }

    public async Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh Foundry token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken,
                ["scope"] = scope
            });

            var tokenUrl = _config.GetTokenUrl(credentials.TenantId);
            var response = await _httpClient.PostAsync(tokenUrl, requestBody, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Foundry token refresh failed: {StatusCode} {Response}", response.StatusCode, content);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<FoundryTokenResponse>(content);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Foundry token refresh returned no access token");
                return null;
            }

            // Update stored refresh token if rotated
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken) &&
                tokenResponse.RefreshToken != credentials.RefreshToken)
            {
                credentials.RefreshToken = tokenResponse.RefreshToken;
            }
            credentials.LastRefreshedAt = DateTime.UtcNow;

            var json = JsonSerializer.Serialize(credentials);
            await _configurationService.SetAsync(StorageKey, json, global: true);

            return tokenResponse.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry token refresh failed with exception");
            return null;
        }
    }

    public async Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://management.azure.com/subscriptions?api-version=2022-12-01");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var subscriptionsResponse = JsonSerializer.Deserialize<AzureSubscriptionListResponse>(content);
        return subscriptionsResponse?.Value ?? new List<AzureSubscription>();
    }

    public async Task<List<CognitiveServicesAccount>> ListFoundryResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CognitiveServices/accounts?api-version=2023-05-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var accountsResponse = JsonSerializer.Deserialize<CognitiveServicesAccountListResponse>(content);
        var allAccounts = accountsResponse?.Value ?? new List<CognitiveServicesAccount>();

        // Filter to AI Foundry resources: Kind contains "AIServices" or endpoint contains ".services.ai.azure.com"
        return allAccounts.Where(a =>
            a.Kind.Contains("AIServices", StringComparison.OrdinalIgnoreCase) ||
            a.Properties.Endpoint.Contains(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
        ).ToList();
    }

    public async Task<List<AppInsightsComponent>> ListAppInsightsResourcesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Insights/components?api-version=2020-02-02";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<AppInsightsComponentListResponse>(content);
        return result?.Value ?? new List<AppInsightsComponent>();
    }

    public async Task<List<FoundryDeployment>> ListDeploymentsAsync(string accessToken, string subscriptionId, string resourceGroup, string accountName, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.CognitiveServices/accounts/{accountName}/deployments?api-version=2023-05-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var deploymentsResponse = JsonSerializer.Deserialize<FoundryDeploymentListResponse>(content);
        return deploymentsResponse?.Value ?? new List<FoundryDeployment>();
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials != null && !string.IsNullOrEmpty(credentials.RefreshToken);
    }

    public async Task<FoundryStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var json = await _configurationService.GetAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            return JsonSerializer.Deserialize<FoundryStoredCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task StoreCredentialsAsync(FoundryStoredCredentials credentials)
    {
        var json = JsonSerializer.Serialize(credentials);
        await _configurationService.SetAsync(StorageKey, json, global: true);
    }

    public async Task ClearCredentialsAsync()
    {
        await _configurationService.DeleteAsync(StorageKey);
    }

    private static PkceChallenge GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        return new PkceChallenge
        {
            CodeVerifier = codeVerifier,
            CodeChallenge = codeChallenge
        };
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryOpenBrowser(string url)
    {
        // Try $BROWSER first — VS Code devcontainers set this to a helper that opens on the host
        var browserEnv = Environment.GetEnvironmentVariable("BROWSER");
        if (!string.IsNullOrEmpty(browserEnv))
        {
            try
            {
                Process.Start(new ProcessStartInfo(browserEnv, url)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                return;
            }
            catch { }
        }

        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", url)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.CallbackTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var contextTask = listener.GetContextAsync();
            var completedTask = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, linkedCts.Token));

            if (completedTask != contextTask)
                throw new OperationCanceledException("Authentication callback timed out");

            var context = await contextTask;
            var query = context.Request.QueryString;
            var code = query["code"];
            var returnedState = query["state"];
            var error = query["error"];

            // Send response to browser
            var responseHtml = "<html><body><h2>Authentication complete. You can close this tab.</h2></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            context.Response.ContentType = "text/html";
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, linkedCts.Token);
            context.Response.Close();

            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException($"Authentication error: {error}");

            if (returnedState != expectedState)
                throw new InvalidOperationException("State mismatch — possible CSRF attack");

            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("No authorization code received");

            return code;
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task<FoundryTokenResponse> ExchangeCodeForTokensAsync(
        string code, string redirectUri, string codeVerifier, string tenantId, string scope, CancellationToken cancellationToken)
    {
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = scope
        });

        var tokenUrl = _config.GetTokenUrl(tenantId);
        var response = await _httpClient.PostAsync(tokenUrl, requestBody, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenResponse = JsonSerializer.Deserialize<FoundryTokenResponse>(content);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token exchange returned no access token");

        return tokenResponse;
    }
}
