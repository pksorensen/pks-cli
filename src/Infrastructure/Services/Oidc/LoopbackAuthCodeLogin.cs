using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Oidc;

public sealed class LoopbackAuthCodeLogin(HttpClient http) : ILoopbackAuthCodeLogin
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// Long enough for a login that needs a password manager and an MFA prompt,
    /// short enough that a forgotten terminal frees the port the same morning.
    private static readonly TimeSpan WaitForCallback = TimeSpan.FromMinutes(5);

    public async Task<OidcLoginResult> LoginAsync(LoopbackLoginRequest request, CancellationToken ct = default)
    {
        var endpoints = request.Endpoints;
        if (string.IsNullOrEmpty(endpoints.AuthorizationEndpoint))
            return OidcLoginResult.Failed("This identity provider published no authorization endpoint.");

        using var listener = Listen(request, out var port);
        if (listener is null)
            return OidcLoginResult.Failed(
                $"None of the loopback ports {string.Join(", ", request.RedirectPorts)} could be bound.");

        // The redirect_uri has to be spelled exactly as the metadata document
        // spells it, so it is built without a trailing slash even though the
        // HttpListener prefix needs one.
        var redirect = $"http://127.0.0.1:{port}{request.RedirectPath}";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authorizeUrl = new StringBuilder(endpoints.AuthorizationEndpoint)
            .Append("?response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(request.ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirect))
            .Append("&scope=").Append(Uri.EscapeDataString(request.Scope))
            .Append("&state=").Append(state)
            .Append("&code_challenge=").Append(challenge)
            .Append("&code_challenge_method=S256");
        if (!string.IsNullOrEmpty(request.Resource))
            authorizeUrl.Append("&resource=").Append(Uri.EscapeDataString(request.Resource));

        var url = authorizeUrl.ToString();
        request.OnAuthorizeUrl(url);
        TryOpenBrowser(url);

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync().WaitAsync(WaitForCallback, ct);
        }
        catch (TimeoutException)
        {
            return OidcLoginResult.Failed("Timed out waiting for the browser to come back.");
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            return OidcLoginResult.Failed("The loopback listener stopped before the browser answered.");
        }

        var code = ctx.Request.QueryString["code"];
        var returnedState = ctx.Request.QueryString["state"];
        var error = ctx.Request.QueryString["error"];
        var ok = error is null && !string.IsNullOrEmpty(code) && returnedState == state;

        // Answer the browser before anything else can throw: a tab that hangs
        // on a dead socket is how a successful login looks like a failed one.
        await RespondAsync(ctx, ok, error);

        if (error is not null) return OidcLoginResult.Failed(error);
        if (string.IsNullOrEmpty(code)) return OidcLoginResult.Failed("The provider returned no authorization code.");

        // A mismatched state is the one case worth naming precisely: it means
        // the callback did not come from the request we started.
        if (returnedState != state) return OidcLoginResult.Failed("state mismatch — the callback was not ours.");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code!),
            new("redirect_uri", redirect),
            new("client_id", request.ClientId),
            new("code_verifier", verifier),
        };
        if (!string.IsNullOrEmpty(request.Resource)) fields.Add(new("resource", request.Resource));

        try
        {
            using var resp = await http.PostAsync(endpoints.TokenEndpoint, new FormUrlEncodedContent(fields), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return OidcLoginResult.Failed(ErrorOf(body));

            var token = JsonSerializer.Deserialize<TokenDto>(body, Json);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                return OidcLoginResult.Failed("The token endpoint answered without an access token.");

            return new OidcLoginResult(
                token.AccessToken,
                token.RefreshToken,
                token.IdToken,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + token.ExpiresIn,
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return OidcLoginResult.Failed(ex.Message);
        }
    }

    /// Binds the first port that is free. Returns null when none are — which is
    /// a real outcome on a machine already running another copy of this login.
    private static HttpListener? Listen(LoopbackLoginRequest request, out int bound)
    {
        foreach (var port in request.RedirectPorts)
        {
            var listener = new HttpListener();
            // The whole loopback root, not just the callback path: HttpListener
            // matches prefixes by path segment, so a prefix of `/callback/`
            // would not match a request for `/callback`.
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                bound = port;

                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        bound = 0;

        return null;
    }

    private static async Task RespondAsync(HttpListenerContext ctx, bool ok, string? error)
    {
        var message = ok
            ? "You're signed in. Close this tab and go back to the terminal."
            : $"Sign-in failed{(error is null ? "" : $": {WebUtility.HtmlEncode(error)}")}. Go back to the terminal.";
        var html =
            "<!doctype html><meta charset=utf-8><title>pks-cli</title>" +
            "<body style=\"font-family:system-ui;background:#0b0b0c;color:#ededef;text-align:center;padding-top:15vh\">" +
            $"<h2 style=\"color:#f87f2e\">pks-cli</h2><p>{message}</p>";
        var buf = Encoding.UTF8.GetBytes(html);

        try
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = buf.Length;
            await ctx.Response.OutputStream.WriteAsync(buf);
            ctx.Response.Close();
        }
        catch (HttpListenerException) { /* the browser gave up first; the code is still good */ }
    }

    private static string ErrorOf(string body)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ErrorDto>(body, Json);
            if (dto?.Error is { Length: > 0 } e)
                return dto.ErrorDescription is { Length: > 0 } d ? $"{e}: {d}" : e;
        }
        catch (JsonException) { /* not JSON — show what came back */ }

        return body.Length > 300 ? body[..300] : body;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            var browser = Environment.GetEnvironmentVariable("BROWSER");
            var (exe, args) = OperatingSystem.IsWindows() ? ("cmd", $"/c start \"\" \"{url}\"")
                : !string.IsNullOrEmpty(browser) ? (browser!, url)
                : OperatingSystem.IsMacOS() ? ("open", url)
                : ("xdg-open", url);

            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch { /* headless — the printed URL is the fallback */ }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenDto
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class ErrorDto
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
