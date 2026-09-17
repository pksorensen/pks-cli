using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// The mechanics of signing in to Entra ID as a public client — authorization code + PKCE through a
/// loopback listener, code-for-token exchange, refresh-token redemption and tenant discovery — with
/// no opinion about what gets stored where. <c>AzureFoundryAuthService</c> and
/// <c>AzureFileShareProvider</c> both carried a private copy of this; the tenant credential store is
/// the third caller, and three copies of a token endpoint client is two too many.
///
/// Refresh tokens cross this boundary as <see cref="SecretValue"/>. Inside, the plaintext goes only
/// into the form body of a request to the token endpoint.
/// </summary>
public sealed class AzurePublicClientAuth
{
    /// <summary>Azure CLI's well-known public client id — no app registration needed.</summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
    public const string DefaultAuthorizeUrlTemplate = "https://login.microsoftonline.com/{0}/oauth2/v2.0/authorize";
    public const string DefaultTokenUrlTemplate = "https://login.microsoftonline.com/{0}/oauth2/v2.0/token";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _authorizeUrlTemplate;
    private readonly string _tokenUrlTemplate;
    private readonly TimeSpan _callbackTimeout;
    private readonly ILogger? _logger;

    public AzurePublicClientAuth(
        HttpClient http,
        string? clientId = null,
        string? authorizeUrlTemplate = null,
        string? tokenUrlTemplate = null,
        int callbackTimeoutSeconds = 120,
        ILogger? logger = null)
    {
        _http = http;
        _clientId = string.IsNullOrWhiteSpace(clientId) ? AzureCliClientId : clientId;
        _authorizeUrlTemplate = string.IsNullOrWhiteSpace(authorizeUrlTemplate) ? DefaultAuthorizeUrlTemplate : authorizeUrlTemplate;
        _tokenUrlTemplate = string.IsNullOrWhiteSpace(tokenUrlTemplate) ? DefaultTokenUrlTemplate : tokenUrlTemplate;
        _callbackTimeout = TimeSpan.FromSeconds(callbackTimeoutSeconds);
        _logger = logger;
    }

    public string GetAuthorizeUrl(string tenantId) => string.Format(_authorizeUrlTemplate, tenantId);
    public string GetTokenUrl(string tenantId) => string.Format(_tokenUrlTemplate, tenantId);

    /// <summary>What an interactive sign-in yields.</summary>
    public sealed record LoginResult(string AccessToken, SecretValue RefreshToken, int ExpiresIn, string? Scope);

    /// <summary>What redeeming a refresh token yields. <see cref="NewRefreshToken"/> is set only when
    /// the STS rotated the token, so callers persist exactly when there is something new to persist.</summary>
    public sealed record RefreshResult(string AccessToken, DateTimeOffset ExpiresOn, SecretValue? NewRefreshToken);

    private sealed class TokenEndpointResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    /// <summary>
    /// Full interactive flow: builds the authorize URL with PKCE, starts the loopback listener
    /// before opening the browser (so the redirect cannot race the listener), prints the URL for
    /// terminals where the browser cannot be opened, waits for the callback, and exchanges the code.
    /// </summary>
    public async Task<LoginResult> LoginInteractiveAsync(string tenantId, string scope, string? loginHint, CancellationToken ct)
    {
        var (codeVerifier, codeChallenge) = GeneratePkce();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}";

        var authorizeUrl = $"{GetAuthorizeUrl(tenantId)}" +
            $"?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
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
        TryOpenBrowser(authorizeUrl);

        var code = await WaitForCallbackAsync(listener, state, ct);
        var token = await ExchangeCodeForTokensAsync(code, redirectUri, codeVerifier, tenantId, scope, ct);
        return new LoginResult(token.AccessToken, SecretValue.From(token.RefreshToken), token.ExpiresIn, token.Scope);
    }

    /// <summary>
    /// Redeems a refresh token for an access token in <paramref name="scope"/>. Throws
    /// <see cref="AzureRefreshTokenExpiredException"/> when the STS says the refresh token itself is
    /// dead (the user has to sign in again) and <see cref="AzureTokenEndpointException"/> for any
    /// other non-success response, so callers can log the two differently.
    /// </summary>
    public async Task<RefreshResult> RedeemRefreshTokenAsync(string tenantId, SecretValue refreshToken, string scope, CancellationToken ct)
    {
        if (!refreshToken.HasValue)
            throw new AzureRefreshTokenExpiredException(tenantId, HttpStatusCode.Unauthorized, "no refresh token stored");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["scope"] = scope
        };
        SecretSink.SetFormField(form, "refresh_token", refreshToken);

        var response = await _http.PostAsync(GetTokenUrl(tenantId), new FormUrlEncodedContent(form), ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Differentiate "your refresh token has aged out — re-auth" from generic transient AAD
            // errors so the user can take the single right action without digging through raw AAD
            // JSON. AADSTS50196 ("client request loop") + invalid_grant is what we see when the
            // refresh token itself has expired.
            if (IsRefreshTokenDead(content))
                throw new AzureRefreshTokenExpiredException(tenantId, response.StatusCode, content);
            throw new AzureTokenEndpointException(tenantId, response.StatusCode, content);
        }

        var token = JsonSerializer.Deserialize<TokenEndpointResponse>(content);
        if (token == null || string.IsNullOrEmpty(token.AccessToken))
            throw new AzureTokenEndpointException(tenantId, response.StatusCode, "token refresh returned no access token");

        var rotated = SecretValue.From(token.RefreshToken);
        var newRefreshToken = rotated.HasValue && rotated != refreshToken ? rotated : (SecretValue?)null;

        // Trust expires_in when the STS sends it; an hour is AAD's default for this grant.
        var lifetime = token.ExpiresIn > 0 ? TimeSpan.FromSeconds(token.ExpiresIn) : TimeSpan.FromHours(1);
        return new RefreshResult(token.AccessToken, DateTimeOffset.UtcNow + lifetime, newRefreshToken);
    }

    /// <summary>The AADSTS signatures of a refresh token that is expired or revoked rather than merely refused right now.</summary>
    public static bool IsRefreshTokenDead(string? tokenEndpointBody)
        => tokenEndpointBody is not null && (
            tokenEndpointBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            || tokenEndpointBody.Contains("AADSTS50196", StringComparison.Ordinal)
            || tokenEndpointBody.Contains("AADSTS70008", StringComparison.Ordinal)
            || tokenEndpointBody.Contains("AADSTS700082", StringComparison.Ordinal));

    /// <summary>
    /// Resolves the tenant id for an email address through the userrealm and OpenID discovery
    /// endpoints. Returns the tenant id, the bare domain when discovery only got that far, or null.
    /// </summary>
    public async Task<string?> DiscoverTenantAsync(string email, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://login.microsoftonline.com/common/userrealm/{Uri.EscapeDataString(email)}?api-version=1.0";
            var response = await _http.GetAsync(url, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Tenant discovery failed: {StatusCode}", response.StatusCode);
                return null;
            }

            // The userrealm endpoint returns different fields depending on account type (Managed vs
            // Federated); both carry "DomainName", which OpenID discovery turns into a tenant id.
            using var doc = JsonDocument.Parse(content);
            var domain = doc.RootElement.TryGetProperty("DomainName", out var domainProp) ? domainProp.GetString() : null;
            if (string.IsNullOrEmpty(domain))
                return null;

            var openIdUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(domain)}/.well-known/openid-configuration";
            var openIdResponse = await _http.GetAsync(openIdUrl, ct);
            var openIdContent = await openIdResponse.Content.ReadAsStringAsync(ct);

            if (!openIdResponse.IsSuccessStatusCode)
                return domain; // Fall back to using domain as tenant identifier

            using var openIdDoc = JsonDocument.Parse(openIdContent);
            var issuer = openIdDoc.RootElement.TryGetProperty("issuer", out var issuerProp) ? issuerProp.GetString() : null;

            // Issuer format: https://sts.windows.net/{tenant-id}/ or https://login.microsoftonline.com/{tenant-id}/v2.0
            if (!string.IsNullOrEmpty(issuer))
            {
                var parts = issuer.TrimEnd('/').Split('/');
                var tenantId = parts[^1];
                if (tenantId == "v2.0" && parts.Length >= 2)
                    tenantId = parts[^2];
                if (!string.IsNullOrEmpty(tenantId))
                    return tenantId;
            }

            return domain; // Fall back to domain as tenant
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Tenant discovery failed for email: {Email}", email);
            return null;
        }
    }

    private async Task<TokenEndpointResponse> ExchangeCodeForTokensAsync(
        string code, string redirectUri, string codeVerifier, string tenantId, string scope, CancellationToken ct)
    {
        var requestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = scope
        });

        var response = await _http.PostAsync(GetTokenUrl(tenantId), requestBody, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        var tokenResponse = JsonSerializer.Deserialize<TokenEndpointResponse>(content);
        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token exchange returned no access token");

        return tokenResponse;
    }

    private async Task<string> WaitForCallbackAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(_callbackTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

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

    private static (string CodeVerifier, string CodeChallenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return (codeVerifier, Base64UrlEncode(challengeBytes));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
}

/// <summary>A non-success response from the token endpoint. Carries the status and body so the
/// caller can log what AAD said; the body never contains the credential that was sent.</summary>
public class AzureTokenEndpointException : Exception
{
    public string TenantId { get; }
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public AzureTokenEndpointException(string tenantId, HttpStatusCode statusCode, string responseBody)
        : base($"Token request for tenant '{tenantId}' failed: {(int)statusCode} {statusCode}")
    {
        TenantId = tenantId;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}

/// <summary>The refresh token is expired or revoked: only a new interactive sign-in helps.</summary>
public sealed class AzureRefreshTokenExpiredException : AzureTokenEndpointException
{
    public AzureRefreshTokenExpiredException(string tenantId, HttpStatusCode statusCode, string responseBody)
        : base(tenantId, statusCode, responseBody)
    {
    }
}
