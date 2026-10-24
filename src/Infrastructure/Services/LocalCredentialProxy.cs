using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services;

/// <summary>
/// A loopback reverse proxy that swaps a throwaway token for a stored credential.
///
/// This is what the credential quarantine offers instead of an export command: a local script that
/// needs an upstream API gets a URL and a per-process token, never the key. The credential is
/// fetched per request through the supplied delegate and attached with <see cref="SecretSink"/>, so
/// it exists only for the duration of one outbound call — and a rotation through
/// <c>… init --force</c> takes effect without restarting the proxy.
///
/// WebSockets are proxied too: the upgrade is checked against the same token, the upstream socket is
/// opened with the credential, and frames are pumped both ways untouched. The credential signs the
/// handshake only, so a session outlives the token that opened it.
///
/// The server lives here rather than in a command so no command needs to hold a credential at all,
/// and so the third provider that wants this does not become the third copy of it.
/// </summary>
public static class LocalCredentialProxy
{
    // Hop-by-hop headers, plus Authorization which we replace ourselves.
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Transfer-Encoding",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Upgrade",
    };

    private static readonly JsonSerializerOptions ErrorJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>How long a graceful close may take to come back the other way before both sockets are
    /// dropped. A peer that never answers a close frame must not hold the proxy open.</summary>
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(5);

    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <param name="credential">
    /// Fetches the credential to sign the next upstream request with. Called per request; returning
    /// an absent <see cref="SecretValue"/> makes the proxy answer 502 rather than send an
    /// unauthenticated request that would come back as a confusing upstream 401.
    /// </param>
    public static async Task RunAsync(
        int port,
        string proxyToken,
        string upstreamBase,
        Func<Task<SecretValue>> credential,
        string missingCredentialMessage,
        CancellationToken cancellationToken = default)
    {
        var app = Build($"http://localhost:{port}", proxyToken, upstreamBase, credential, missingCredentialMessage);

        // Not app.RunAsync(token) — that overload's single argument is a URL, not a CancellationToken,
        // and WaitForShutdownAsync is an IWebHost extension that WebApplication does not satisfy.
        await app.StartAsync(cancellationToken);
        await ((IHost)app).WaitForShutdownAsync(cancellationToken);
    }

    /// <summary>
    /// Starts the proxy in the background and returns once it is listening — the shape a caller needs
    /// when the proxy serves a child process rather than being the command itself.
    ///
    /// It binds <c>127.0.0.1</c> rather than <c>localhost</c>: with no port given the OS picks one at
    /// bind time, so there is no window for another process to take it, and a client never has to
    /// guess whether <c>localhost</c> means IPv4 or IPv6 (Go on Windows may try <c>::1</c> first).
    /// </summary>
    /// <param name="catalog">
    /// Serves <c>GET /pks/v1/models</c> (<see cref="Models.ProviderCatalog.Path"/>) to the child: the
    /// model catalog as pks knows it, behind the same proxy token. Null leaves the path to the
    /// upstream like any other.
    /// </param>
    /// <param name="chat">
    /// Routes <c>POST /pks/v1/chat/completions</c> (<see cref="Models.ProviderCatalog.ChatPath"/>):
    /// given the request's <c>model</c>, says which provider endpoint and credential the request goes
    /// to — so a child bound to one provider can still use a model from another, through pks. Null
    /// leaves the path to the upstream.
    /// </param>
    public static async Task<RunningCredentialProxy> StartAsync(
        string proxyToken,
        string upstreamBase,
        Func<Task<SecretValue>> credential,
        string missingCredentialMessage,
        int? port = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<string>>? catalog = null,
        Func<string, CancellationToken, Task<Models.ChatRouteResult>>? chat = null)
    {
        var app = Build($"http://127.0.0.1:{port ?? 0}", proxyToken, upstreamBase, credential, missingCredentialMessage, catalog, chat);
        await app.StartAsync(cancellationToken);
        var bound = new Uri(app.Urls.Single()).Port;
        return new RunningCredentialProxy(app, bound);
    }

    private static WebApplication Build(
        string url,
        string proxyToken,
        string upstreamBase,
        Func<Task<SecretValue>> credential,
        string missingCredentialMessage,
        Func<CancellationToken, Task<string>>? catalog = null,
        Func<string, CancellationToken, Task<Models.ChatRouteResult>>? chat = null)
    {
        upstreamBase = upstreamBase.TrimEnd('/');

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls(url);
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("credential-proxy")
            // Streaming completions hold a connection open far past the 100s default.
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        var app = builder.Build();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                await next(ctx);
                return;
            }

            if (!IsAuthorized(ctx, proxyToken))
            {
                await RejectAsync(ctx);
                return;
            }

            await ProxyWebSocketAsync(ctx, upstreamBase, credential, missingCredentialMessage);
        });

        if (catalog != null)
        {
            app.MapGet(Models.ProviderCatalog.Path, async (HttpContext ctx) =>
            {
                if (!IsAuthorized(ctx, proxyToken))
                {
                    await RejectAsync(ctx);
                    return;
                }
                string json;
                try
                {
                    json = await catalog(ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    ctx.Response.StatusCode = 502;
                    await ctx.Response.WriteAsync($"catalog unavailable: {ex.Message}");
                    return;
                }
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(json, ctx.RequestAborted);
            });
        }

        if (chat != null)
        {
            app.MapPost(Models.ProviderCatalog.ChatPath, async (HttpContext ctx) =>
            {
                if (!IsAuthorized(ctx, proxyToken))
                {
                    await RejectAsync(ctx);
                    return;
                }

                JsonObject? body;
                try
                {
                    body = (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted)) as JsonObject;
                }
                catch (JsonException)
                {
                    body = null;
                }
                if (body is null)
                {
                    await OpenAiErrorAsync(ctx, 400, "the request body is not a JSON object", "invalid_request_error", null);
                    return;
                }

                var model = body["model"]?.GetValue<string>() ?? "";
                var route = await chat(model, ctx.RequestAborted);
                if (route.Route is null)
                {
                    await OpenAiErrorAsync(ctx, 404, route.Error ?? "no route", "invalid_request_error", "model_not_found");
                    return;
                }

                // The provider gets its own name for the model; a `provider/` prefix was pks's.
                body["model"] = route.Route.Model;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
                using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, route.Route.Url)
                {
                    // ByteArrayContent carries a Content-Length, which some providers insist on.
                    Content = new ByteArrayContent(bytes),
                };
                upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                await ForwardAsync(ctx, httpClientFactory, upstreamRequest, route.Route.Credential,
                    $"no credential for provider '{route.Route.Kind}'");
            });
        }

        app.MapMethods("{**path}", AllHttpMethods, async (HttpContext ctx) =>
        {
            if (!IsAuthorized(ctx, proxyToken))
            {
                await RejectAsync(ctx);
                return;
            }

            var upstreamUrl = $"{upstreamBase}{ctx.Request.Path}{ctx.Request.QueryString}";
            using var upstreamRequest = new HttpRequestMessage(
                new HttpMethod(ctx.Request.Method), upstreamUrl);

            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamRequest.Content = new StreamContent(ctx.Request.Body);
                if (ctx.Request.ContentType != null)
                    upstreamRequest.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
                // A known length goes upstream as one; StreamContent would otherwise send chunked,
                // and Foundry's Decision-1 route refuses a body without Content-Length (HTTP 400).
                if (ctx.Request.ContentLength is { } length)
                    upstreamRequest.Content.Headers.ContentLength = length;
            }

            await ForwardAsync(ctx, httpClientFactory, upstreamRequest, await credential(), missingCredentialMessage);
        });

        return app;
    }

    /// <summary>
    /// Sends a prepared upstream request with the child's remaining headers and the given credential,
    /// and streams the answer back — status, headers and body, SSE included. Shared by the catch-all
    /// (same path at the bound provider) and the routed chat route (another provider entirely).
    /// </summary>
    private static async Task ForwardAsync(
        HttpContext ctx,
        IHttpClientFactory httpClientFactory,
        HttpRequestMessage upstreamRequest,
        SecretValue credential,
        string missingCredentialMessage)
    {
        foreach (var header in ctx.Request.Headers)
        {
            if (SkipHeaders.Contains(header.Key)) continue;
            upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        if (!SecretSink.SetBearerToken(upstreamRequest, credential))
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync(missingCredentialMessage);
            return;
        }

        var client = httpClientFactory.CreateClient("credential-proxy");
        using var upstreamResponse = await client.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ctx.RequestAborted);

        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        foreach (var header in upstreamResponse.Headers)
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
        foreach (var header in upstreamResponse.Content.Headers)
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());

        // Kestrel owns the framing; forwarding the upstream values breaks SSE pass-through.
        ctx.Response.Headers.Remove("Transfer-Encoding");
        ctx.Response.Headers.Remove("Content-Length");

        await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>An error in the shape OpenAI clients already parse, so a tool sees one error format on this route.</summary>
    private static async Task OpenAiErrorAsync(HttpContext ctx, int status, string message, string type, string? code)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var error = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = message, ["type"] = type, ["param"] = "model", ["code"] = code },
        };
        // Relaxed escaping keeps quotes in the message readable; the body is JSON a tool prints, not HTML.
        await ctx.Response.WriteAsync(error.ToJsonString(ErrorJson), ctx.RequestAborted);
    }

    private static bool IsAuthorized(HttpContext ctx, string proxyToken)
        => (ctx.Request.Headers.Authorization.FirstOrDefault() ?? "") == $"Bearer {proxyToken}";

    private static async Task RejectAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsync("Unauthorized: invalid proxy token");
    }

    // ---------- WebSocket ----------

    /// <summary>
    /// Connects upstream first and accepts the client only once that worked, so a refused or failed
    /// upstream handshake reaches the client as an HTTP status on its upgrade request rather than as a
    /// socket that opens and immediately dies.
    /// </summary>
    private static async Task ProxyWebSocketAsync(
        HttpContext ctx,
        string upstreamBase,
        Func<Task<SecretValue>> credential,
        string missingCredentialMessage)
    {
        var upstreamUri = ToWebSocketUri($"{upstreamBase}{ctx.Request.Path}{ctx.Request.QueryString}");

        using var upstream = new ClientWebSocket();
        upstream.Options.CollectHttpResponseDetails = true;
        foreach (var subProtocol in ctx.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(subProtocol);
        }
        foreach (var header in ctx.Request.Headers)
        {
            if (SkipHeaders.Contains(header.Key)
                || header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                upstream.Options.SetRequestHeader(header.Key, header.Value.ToString());
            }
            catch (ArgumentException)
            {
                // A header ClientWebSocket reserves for itself; it sets its own.
            }
        }

        if (!SecretSink.SetBearerToken(upstream.Options, await credential()))
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync(missingCredentialMessage);
            return;
        }

        try
        {
            await upstream.ConnectAsync(upstreamUri, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            ctx.Response.StatusCode = 502;
            var status = upstream.HttpStatusCode == 0 ? "no response" : $"HTTP {(int)upstream.HttpStatusCode}";
            await ctx.Response.WriteAsync($"Upstream WebSocket handshake failed ({status})");
            return;
        }

        using var client = await ctx.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        await PumpAsync(client, upstream, ctx.RequestAborted);
    }

    private static Uri ToWebSocketUri(string url)
    {
        var builder = new UriBuilder(url);
        builder.Scheme = builder.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            var other => other,
        };
        // UriBuilder keeps an explicit default port across a scheme change; -1 lets the new scheme's
        // default apply when the original URL did not name one.
        if (new Uri(url).IsDefaultPort) builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>
    /// Pumps frames both ways until one side closes or fails. A close is forwarded with its status and
    /// description, and the other side is then given a moment to complete the handshake; a failure on
    /// either side closes the other with 1011 and drops both.
    /// </summary>
    private static async Task PumpAsync(WebSocket client, WebSocket upstream, CancellationToken requestAborted)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        var toUpstream = PumpOneWayAsync(client, upstream, cts.Token);
        var toClient = PumpOneWayAsync(upstream, client, cts.Token);

        var first = await Task.WhenAny(toUpstream, toClient);
        var other = first == toUpstream ? toClient : toUpstream;

        if (first.IsCompletedSuccessfully)
        {
            var finished = await Task.WhenAny(other, Task.Delay(CloseHandshakeTimeout, CancellationToken.None));
            if (finished != other) cts.Cancel();
        }
        else
        {
            _ = first.Exception; // observed: the failure is handled by closing the other side
            var survivor = first == toUpstream ? client : upstream;
            await TryCloseAsync(survivor, WebSocketCloseStatus.InternalServerError, "proxy peer failed");
            cts.Cancel();
        }

        try { await other; } catch { }
        client.Abort();
        upstream.Abort();
    }

    private static async Task PumpOneWayAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await from.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await to.CloseOutputAsync(
                        from.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        from.CloseStatusDescription,
                        ct);
                }
                return;
            }

            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
            await socket.CloseOutputAsync(status, description, timeout.Token);
        }
        catch
        {
            // Best effort: the socket is aborted right after either way.
        }
    }
}

/// <summary>
/// A <see cref="LocalCredentialProxy"/> running in the background. Holds no credential — only the
/// server and the port it bound.
/// </summary>
public sealed class RunningCredentialProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _stopped;

    internal RunningCredentialProxy(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    /// <summary>The base URL a client should be pointed at, without a trailing slash.</summary>
    public string Endpoint => $"http://127.0.0.1:{Port}";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        try
        {
            // Open WebSocket sessions would otherwise hold StopAsync for its full timeout.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(timeout.Token);
        }
        catch
        {
        }
        await _app.DisposeAsync();
    }

    /// <summary>Synchronous stop, for callers like <c>IManifestResolver.Release</c> that are not async.</summary>
    public void Stop() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
