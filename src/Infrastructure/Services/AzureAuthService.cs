using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Result from an Azure auth flow (PKCE)
/// </summary>
public class AzureAuthResult
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Internal token response shape
/// </summary>
internal class AzureTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

/// <summary>
/// Interface for generic Azure OAuth2 authentication using authorization code + PKCE
/// </summary>
public interface IAzureAuthService
{
    Task<string?> DiscoverTenantAsync(string email, CancellationToken cancellationToken = default);
    Task<AzureAuthResult> InitiateLoginAsync(string tenantId, string? loginHint = null, CancellationToken cancellationToken = default);
    Task<string?> GetAccessTokenAsync(string scope, CancellationToken cancellationToken = default);
    Task<List<AzureSubscription>> ListSubscriptionsAsync(string accessToken, CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync();
    Task<AzureStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(AzureStoredCredentials credentials);
    Task ClearCredentialsAsync();
}

/// <summary>
/// Generic Azure OAuth2 authentication using authorization code flow with PKCE.
/// Uses the Azure CLI well-known public client ID — no app registration needed.
/// </summary>
public class AzureAuthService : IAzureAuthService
{
    private const string StorageKey = "azure.auth.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AzureAuthService> _logger;
    private readonly AzureAuthConfig _config;

    private record PkceChallenge(string CodeVerifier, string CodeChallenge);

    public AzureAuthService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureAuthService> logger,
        AzureAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureAuthConfig();
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

            var domain = root.TryGetProperty("DomainName", out var domainProp) ? domainProp.GetString() : null;
            if (string.IsNullOrEmpty(domain))
                return null;

            var openIdUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(domain)}/.well-known/openid-configuration";
            var openIdResponse = await _httpClient.GetAsync(openIdUrl, cancellationToken);
            var openIdContent = await openIdResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!openIdResponse.IsSuccessStatusCode)
                return domain;

            using var openIdDoc = JsonDocument.Parse(openIdContent);
            var issuer = openIdDoc.RootElement.TryGetProperty("issuer", out var issuerProp) ? issuerProp.GetString() : null;

            if (!string.IsNullOrEmpty(issuer))
            {
                var parts = issuer.TrimEnd('/').Split('/');
                var tenantId = parts[^1];
                if (tenantId == "v2.0" && parts.Length >= 2)
                    tenantId = parts[^2];
                if (!string.IsNullOrEmpty(tenantId))
                    return tenantId;
            }

            return domain;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tenant discovery failed for email: {Email}", email);
            return null;
        }
    }

    public async Task<AzureAuthResult> InitiateLoginAsync(string tenantId, string? loginHint = null, CancellationToken cancellationToken = default)
    {
        var scope = _config.ManagementScope;
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

        if (!string.IsNullOrEmpty(loginHint))
            authorizeUrl += $"&login_hint={Uri.EscapeDataString(loginHint)}";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Console.WriteLine(authorizeUrl);
        TryOpenBrowser(authorizeUrl);

        var code = await WaitForCallbackAsync(listener, state, cancellationToken);
        var tokenResponse = await ExchangeCodeForTokensAsync(code, redirectUri, pkce.CodeVerifier, tenantId, scope, cancellationToken);

        return new AzureAuthResult
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
            _logger.LogWarning("Cannot refresh Azure token: no stored credentials or refresh token");
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
                _logger.LogError("Azure token refresh failed: {StatusCode} {Response}", response.StatusCode, content);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<AzureTokenResponse>(content);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("Azure token refresh returned no access token");
                return null;
            }

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
            _logger.LogError(ex, "Azure token refresh failed with exception");
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

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials != null && !string.IsNullOrEmpty(credentials.RefreshToken);
    }

    public async Task<AzureStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var json = await _configurationService.GetAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            return JsonSerializer.Deserialize<AzureStoredCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task StoreCredentialsAsync(AzureStoredCredentials credentials)
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
        return new PkceChallenge(codeVerifier, codeChallenge);
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

    private async Task<AzureTokenResponse> ExchangeCodeForTokensAsync(
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

        var tokenResponse = JsonSerializer.Deserialize<AzureTokenResponse>(content);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token exchange returned no access token");

        return tokenResponse;
    }
}
