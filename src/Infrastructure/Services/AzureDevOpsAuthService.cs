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
/// Interface for Azure DevOps OAuth2 authentication using authorization code + PKCE
/// </summary>
public interface IAzureDevOpsAuthService
{
    Task<AdoAuthResult> InitiateAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(AdoAuthResult result, AdoAccount selectedOrg);
    Task<string?> RefreshAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync();
    Task<AdoStoredCredentials?> GetStoredCredentialsAsync();
    Task ClearStoredCredentialsAsync();
}

/// <summary>
/// Azure DevOps OAuth2 authentication using authorization code flow with PKCE.
/// Uses the Visual Studio well-known public client ID — no app registration needed.
/// </summary>
public class AzureDevOpsAuthService : IAzureDevOpsAuthService
{
    private const string StorageKey = "ado.auth.credentials";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<AzureDevOpsAuthService> _logger;
    private readonly AzureDevOpsAuthConfig _config;

    public AzureDevOpsAuthService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<AzureDevOpsAuthService> logger,
        AzureDevOpsAuthConfig? config = null)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
        _config = config ?? new AzureDevOpsAuthConfig();
    }

    public async Task<AdoAuthResult> InitiateAsync(CancellationToken cancellationToken = default)
    {
        var pkce = GeneratePkce();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}";

        var authorizeUrl = $"{_config.AuthorizeUrl}" +
            $"?client_id={Uri.EscapeDataString(_config.ClientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(_config.Scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(pkce.CodeChallenge)}" +
            $"&code_challenge_method=S256";

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
        var tokenResponse = await ExchangeCodeForTokensAsync(code, redirectUri, pkce.CodeVerifier, cancellationToken);

        // Fetch profile and accounts
        var profile = await FetchProfileAsync(tokenResponse.AccessToken, cancellationToken);
        var accounts = await FetchAccountsAsync(tokenResponse.AccessToken, profile.Id, cancellationToken);

        return new AdoAuthResult
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresIn = tokenResponse.ExpiresIn,
            Profile = profile,
            Accounts = accounts
        };
    }

    public async Task CompleteAsync(AdoAuthResult result, AdoAccount selectedOrg)
    {
        var credentials = new AdoStoredCredentials
        {
            RefreshToken = result.RefreshToken ?? string.Empty,
            SelectedOrg = selectedOrg.AccountName,
            Profile = result.Profile,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(credentials);
        await _configurationService.SetAsync(StorageKey, json, global: true);
    }

    public async Task<string?> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh ADO token: no stored credentials or refresh token");
            return null;
        }

        try
        {
            var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _config.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credentials.RefreshToken
            });

            var response = await _httpClient.PostAsync(_config.TokenUrl, requestBody, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("ADO token refresh failed: {StatusCode} {Response}", response.StatusCode, content);
                return null;
            }

            var tokenResponse = JsonSerializer.Deserialize<AdoTokenResponse>(content);
            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                _logger.LogError("ADO token refresh returned no access token");
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
            _logger.LogError(ex, "ADO token refresh failed with exception");
            return null;
        }
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        return credentials != null && !string.IsNullOrEmpty(credentials.RefreshToken);
    }

    public async Task<AdoStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var json = await _configurationService.GetAsync(StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            return JsonSerializer.Deserialize<AdoStoredCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task ClearStoredCredentialsAsync()
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

    private async Task<AdoTokenResponse> ExchangeCodeForTokensAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken)
    {
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _config.ClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });

        var response = await _httpClient.PostAsync(_config.TokenUrl, requestBody, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var tokenResponse = JsonSerializer.Deserialize<AdoTokenResponse>(content);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token exchange returned no access token");

        return tokenResponse;
    }

    private async Task<AdoUserProfile> FetchProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _config.ProfileUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var profile = JsonSerializer.Deserialize<AdoUserProfile>(content);
        return profile ?? throw new InvalidOperationException("Failed to fetch user profile");
    }

    private async Task<List<AdoAccount>> FetchAccountsAsync(string accessToken, string memberId, CancellationToken cancellationToken)
    {
        var url = $"{_config.AccountsUrl}&memberId={memberId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        var accountsResponse = JsonSerializer.Deserialize<AdoAccountsResponse>(content);
        return accountsResponse?.Value ?? new List<AdoAccount>();
    }
}
