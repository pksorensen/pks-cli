using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.Foundry;

/// <summary>
/// In-process MSI (Managed Identity) token endpoint bound to loopback. Mirrors the contract Claude Code's
/// Foundry mode expects (IMDS-style): a GET with the <c>X-IDENTITY-HEADER</c> secret and a <c>resource</c>
/// query param, answered with an Azure AD access token acquired via the stored Foundry OAuth credentials.
/// This is the inline counterpart to the remote python MSI server used for devcontainers.
/// Shared by `pks claude --inline` and `pks brain extract --agent claude --foundry`.
/// </summary>
public sealed class LocalMsiTokenServer : IAsyncDisposable
{
    private const string AllowedResource = "https://cognitiveservices.azure.com";

    private readonly HttpListener? _listener;
    private readonly IAzureFoundryAuthService? _auth;
    private readonly IAnsiConsole? _console;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Endpoint { get; }
    public string Secret { get; }

    /// <summary>A disposable that does nothing — returned when Foundry isn't used.</summary>
    public static LocalMsiTokenServer None { get; } = new();

    private LocalMsiTokenServer()
    {
        Endpoint = "";
        Secret = "";
    }

    private LocalMsiTokenServer(HttpListener listener, int port, string secret,
        IAzureFoundryAuthService auth, IAnsiConsole console)
    {
        _listener = listener;
        _auth = auth;
        _console = console;
        Secret = secret;
        Endpoint = $"http://127.0.0.1:{port}";
    }

    public static Task<LocalMsiTokenServer> StartAsync(IAzureFoundryAuthService auth, IAnsiConsole console)
    {
        var port = GetFreeLoopbackPort();
        var secret = Guid.NewGuid().ToString("N");
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var server = new LocalMsiTokenServer(listener, port, secret, auth, console);
        server._loop = Task.Run(() => server.ServeAsync(server._cts.Token));
        console.MarkupLine($"[dim]Local Foundry token server listening on {server.Endpoint}.[/]");
        return Task.FromResult(server);
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener!.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped

            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.Headers["X-IDENTITY-HEADER"] != Secret)
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                return;
            }

            var resource = ctx.Request.QueryString["resource"];
            if (resource != AllowedResource)
            {
                await WriteJsonAsync(ctx, 403, "{\"error\":\"resource_not_allowed\"}");
                return;
            }

            var token = await _auth!.GetAccessTokenAsync($"{AllowedResource}/.default");
            if (string.IsNullOrEmpty(token))
            {
                await WriteJsonAsync(ctx, 500, "{\"error\":\"token_acquisition_failed\"}");
                return;
            }

            var expiresOn = DateTimeOffset.UtcNow.AddMinutes(50).ToUnixTimeSeconds();
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["access_token"] = token,
                ["expires_in"] = "3000",
                ["expires_on"] = expiresOn.ToString(),
                ["resource"] = AllowedResource,
                ["token_type"] = "Bearer",
            });
            await WriteJsonAsync(ctx, 200, body);
        }
        catch (Exception ex)
        {
            _console?.MarkupLine($"[dim]Foundry token server error: {Markup.Escape(ex.Message)}[/]");
            try { await WriteJsonAsync(ctx, 500, "{\"error\":\"internal\"}"); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static int GetFreeLoopbackPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener == null) return; // None
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        if (_loop != null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }
}
