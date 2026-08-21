using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
/// </summary>
public class AzureFoundryAuthService : IAzureFoundryAuthService
{
    private const string StorageKey = "foundry.auth.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ISecretResolver _secrets;
    private readonly ILogger<AzureFoundryAuthService> _logger;
    private readonly AzureFoundryAuthConfig _config;

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
        if (credentials == null || !credentials.RefreshToken.HasValue)
        {
            _logger.LogWarning("Cannot refresh Foundry token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["grant_type"] = "refresh_token",
                ["scope"] = scope
            };
            SecretSink.SetFormField(form, "refresh_token", credentials.RefreshToken);
            var requestBody = new FormUrlEncodedContent(form);

            var tokenUrl = _config.GetTokenUrl(credentials.TenantId);
            var response = await _httpClient.PostAsync(tokenUrl, requestBody, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Differentiate "your refresh token has aged out — re-auth"
                // from generic transient AAD errors so the user can take the
                // single right action without digging through raw AAD JSON.
                // AADSTS50196 ("client request loop") + invalid_grant is what
                // we see when the refresh token itself has expired.
                var aadExpired = content.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                                 || content.Contains("AADSTS50196", StringComparison.Ordinal)
                                 || content.Contains("AADSTS70008", StringComparison.Ordinal)
                                 || content.Contains("AADSTS700082", StringComparison.Ordinal);
                if (aadExpired)
                {
                    _logger.LogError("Foundry refresh token has expired or been revoked. Re-auth with: pks foundry login");
                }
                else
                {
                    _logger.LogError("Foundry token refresh failed: {StatusCode} {Response}", response.StatusCode, content);
                }
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<FoundryTokenResponse>(content);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Foundry token refresh returned no access token");
                return null;
            }

            // Update stored refresh token if rotated
            var rotated = SecretValue.From(tokenResponse.RefreshToken);
            if (rotated.HasValue && rotated != credentials.RefreshToken)
            {
                credentials.RefreshToken = rotated;
            }
            credentials.LastRefreshedAt = DateTime.UtcNow;

            // Persistence options, or the refresh token would be written to the store as "***" and
            // come back absent on the next load — a silent logout an hour later.
            var json = JsonSerializer.Serialize(credentials, SecretJson.Persistence);
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

    public async Task<List<LogAnalyticsWorkspace>> ListLogAnalyticsWorkspacesAsync(string accessToken, string subscriptionId, CancellationToken cancellationToken = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.OperationalInsights/workspaces?api-version=2022-10-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = JsonSerializer.Deserialize<LogAnalyticsWorkspaceListResponse>(content);
        return result?.Value ?? new List<LogAnalyticsWorkspace>();
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
