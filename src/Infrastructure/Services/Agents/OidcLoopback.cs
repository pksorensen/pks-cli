using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Agents;

/// <summary>Result of an interactive login: the tokens plus the resolved identity.</summary>
public sealed record OidcTokens(string AccessToken, string RefreshToken, string Sub, string Email, string Name);

/// <summary>
/// Desktop/CLI OIDC client: Authorization Code + PKCE against a public client with
/// a loopback redirect (RFC 8252). Prints the auth URL (and tries to open it),
/// captures the code on a local 127.0.0.1 listener, exchanges it for tokens, and
/// can refresh. No client secret. In a devcontainer the loopback port is reached
/// via the editor's automatic port forwarding (same as other CLI logins).
/// </summary>
public sealed class OidcLoopback
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private async Task<(string auth, string token)> DiscoverAsync(string issuer, CancellationToken ct)
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>(
            $"{issuer.TrimEnd('/')}/.well-known/openid-configuration", ct);
        return (doc.GetProperty("authorization_endpoint").GetString()!,
                doc.GetProperty("token_endpoint").GetString()!);
    }

    /// <summary>Run the interactive loopback login. <paramref name="print"/> emits the URL to the user.</summary>
    public async Task<OidcTokens> LoginAsync(string issuer, string clientId, string scopes,
        Action<string> print, CancellationToken ct = default)
    {
        var (authEndpoint, tokenEndpoint) = await DiscoverAsync(issuer, ct);
        var (verifier, challenge) = CreatePkce();
        var state = RandomUrlSafe(24);

        var listener = new HttpListener();
        int port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback/";
        listener.Prefixes.Add(redirect);
        listener.Start();

        var authUrl =
            $"{authEndpoint}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirect)}" +
            $"&scope={Uri.EscapeDataString(scopes)}" +
            $"&state={state}" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        print(authUrl);
        TryOpen(authUrl);

        var ctx = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), ct);
        var code = ctx.Request.QueryString["code"];
        var gotState = ctx.Request.QueryString["state"];

        var html = "<html><body style='font-family:system-ui;background:#0a0a0a;color:#ededef;text-align:center;padding-top:80px'>" +
                   "<h2>Agent Share</h2><p>Logget ind. Du kan lukke denne fane og vende tilbage til terminalen.</p></body></html>";
        var buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code) || gotState != state)
            throw new InvalidOperationException("OIDC sign-in failed (no code / state mismatch).");

        var tok = await ExchangeAsync(tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }, ct);
        return tok;
    }

    /// <summary>Refresh and return new tokens (rotated refresh token if the IdP rotates).</summary>
    public async Task<OidcTokens> RefreshAsync(string issuer, string clientId, string refreshToken, CancellationToken ct = default)
    {
        var (_, tokenEndpoint) = await DiscoverAsync(issuer, ct);
        return await ExchangeAsync(tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }, ct);
    }

    private async Task<OidcTokens> ExchangeAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"token endpoint {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : form.GetValueOrDefault("refresh_token", "");
        var (sub, email, name) = ReadClaims(access);
        return new OidcTokens(access, refresh, sub, email, name);
    }

    /// <summary>Decode the JWT payload (no verification — the server verifies) for display/identity.</summary>
    private static (string sub, string email, string name) ReadClaims(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return ("", "", "");
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var r = doc.RootElement;
            string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var name = Get("name");
            if (name == "") name = Get("preferred_username");
            return (Get("sub"), Get("email"), name);
        }
        catch { return ("", "", ""); }
    }

    private static void TryOpen(string url)
    {
        try
        {
            var browser = Environment.GetEnvironmentVariable("BROWSER");
            var (exe, args) = OperatingSystem.IsWindows() ? ("cmd", $"/c start \"\" \"{url}\"")
                : !string.IsNullOrEmpty(browser) ? (browser!, url)
                : OperatingSystem.IsMacOS() ? ("open", url)
                : ("xdg-open", url);
            Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        }
        catch { /* headless — the printed URL is the fallback */ }
    }

    private static (string verifier, string challenge) CreatePkce()
    {
        var verifier = RandomUrlSafe(64);
        using var sha = SHA256.Create();
        var challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
