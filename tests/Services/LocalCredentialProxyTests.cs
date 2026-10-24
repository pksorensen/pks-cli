using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// The loopback proxy that swaps a throwaway token for a stored credential. What matters is the swap:
/// the upstream must see the real credential and never the proxy token, the child must never see the
/// real credential, and a WebSocket session must come through as faithfully as a plain request does —
/// message types, fragmentation and the close handshake included.
/// </summary>
public class LocalCredentialProxyTests : IAsyncLifetime
{
    private const string ProxyToken = "proxy-token-123";
    private const string UpstreamCredential = "real-upstream-credential";

    private WebApplication _upstream = null!;
    private string _upstreamBase = "";
    private RunningCredentialProxy _proxy = null!;

    /// <summary>The Authorization header of every request or upgrade the upstream saw.</summary>
    private readonly ConcurrentQueue<string> _upstreamAuth = new();

    /// <summary>Whole messages the upstream echo received, reassembled from their frames.</summary>
    private readonly ConcurrentQueue<(WebSocketMessageType Type, string Text)> _upstreamMessages = new();

    private readonly TaskCompletionSource<(WebSocketCloseStatus? Status, string? Description)> _upstreamSawClose =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _upstream = builder.Build();
        _upstream.UseWebSockets();

        _upstream.MapGet("/http/whoami", (HttpContext ctx) =>
        {
            _upstreamAuth.Enqueue(ctx.Request.Headers.Authorization.ToString());
            return Results.Text($"auth={ctx.Request.Headers.Authorization}|path={ctx.Request.Path}{ctx.Request.QueryString}");
        });

        _upstream.MapPost("/http/length", (HttpContext ctx) =>
            Results.Text($"content-length={ctx.Request.ContentLength?.ToString() ?? "none"}|transfer-encoding={ctx.Request.Headers.TransferEncoding}"));
        _upstream.MapPost("/http/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text($"{ctx.Request.ContentType}|{body}");
        });

        // Echoes every frame back as it came: same type, same EndOfMessage.
        _upstream.Map("/ws/echo", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            _upstreamAuth.Enqueue(ctx.Request.Headers.Authorization.ToString());
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            var assembling = new MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _upstreamSawClose.TrySetResult((result.CloseStatus, result.CloseStatusDescription));
                    await ws.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription, CancellationToken.None);
                    return;
                }

                assembling.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    _upstreamMessages.Enqueue((result.MessageType, Encoding.UTF8.GetString(assembling.ToArray())));
                    assembling.SetLength(0);
                }

                await ws.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage,
                    CancellationToken.None);
            }
        });

        // Says one thing, then closes with an application-defined status.
        _upstream.Map("/ws/hangup", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await ws.SendAsync(Encoding.UTF8.GetBytes("goodbye"), WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.CloseOutputAsync((WebSocketCloseStatus)4001, "session over", CancellationToken.None);
            var buffer = new byte[16];
            try
            {
                while ((await ws.ReceiveAsync(buffer, CancellationToken.None)).MessageType != WebSocketMessageType.Close) { }
            }
            catch (WebSocketException) { }
        });

        await _upstream.StartAsync();
        _upstreamBase = _upstream.Urls.Single();

        _proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken,
            _upstreamBase,
            () => Task.FromResult(SecretValue.From(UpstreamCredential)),
            "no credential");
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private static Uri WsUri(RunningCredentialProxy proxy, string pathAndQuery)
        => new(proxy.Endpoint.Replace("http://", "ws://") + pathAndQuery);

    private static ClientWebSocket NewClient(string? token = ProxyToken)
    {
        var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        if (token is not null)
        {
            ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }
        return ws;
    }

    private static async Task<(WebSocketMessageType Type, string Text, WebSocketReceiveResult Last)> ReceiveMessage(
        WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
        return (result.MessageType, Encoding.UTF8.GetString(message.ToArray()), result);
    }

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    // ─────────────────────────────────────────────
    //  HTTP
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void The_endpoint_is_an_ipv4_loopback_url_with_the_bound_port()
    {
        // 127.0.0.1 rather than localhost: a Go client on Windows can resolve localhost to ::1 first.
        _proxy.Endpoint.Should().Be($"http://127.0.0.1:{_proxy.Port}");
        _proxy.Port.Should().BeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_http_request_reaches_upstream_with_the_credential_instead_of_the_proxy_token()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var body = await client.GetStringAsync($"{_proxy.Endpoint}/http/whoami?api-version=2025-10-15");

        body.Should().Be($"auth=Bearer {UpstreamCredential}|path=/http/whoami?api-version=2025-10-15");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_http_request_body_and_content_type_are_forwarded()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var response = await client.PostAsync($"{_proxy.Endpoint}/http/echo",
            new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().Be("application/json; charset=utf-8|{\"a\":1}");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_http_request_with_the_wrong_proxy_token_is_refused_and_never_forwarded()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        var response = await client.GetAsync($"{_proxy.Endpoint}/http/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _upstreamAuth.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_body_with_a_known_length_keeps_its_Content_Length_upstream()
    {
        // Foundry's Decision-1 route answers 400 no_content_length_header to a chunked body, so a
        // child's Content-Length must survive the hop rather than become Transfer-Encoding: chunked.
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var response = await client.PostAsync($"{_proxy.Endpoint}/http/length",
            new StringContent("{\"model\":\"Decision-1\"}", Encoding.UTF8, "application/json"));

        (await response.Content.ReadAsStringAsync()).Should().Be("content-length=22|transfer-encoding=");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task The_catalog_route_is_served_by_the_proxy_itself_behind_the_same_token()
    {
        await using var proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken, _upstreamBase, () => Task.FromResult(SecretValue.From(UpstreamCredential)), "no credential",
            catalog: _ => Task.FromResult("{\"object\":\"list\",\"data\":[]}"));
        using var client = new HttpClient();

        var anonymous = await client.GetAsync($"{proxy.Endpoint}/pks/v1/models");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);
        var response = await client.GetAsync($"{proxy.Endpoint}/pks/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"object\":\"list\",\"data\":[]}");
        _upstreamAuth.Should().BeEmpty("the catalog never reaches the upstream");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Without_a_catalog_provider_the_catalog_path_is_forwarded_like_any_other()
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var response = await client.GetAsync($"{_proxy.Endpoint}/pks/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the test upstream has no such route");
        _upstreamAuth.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_catalog_that_cannot_be_read_is_a_502_with_the_reason()
    {
        await using var proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken, _upstreamBase, () => Task.FromResult(SecretValue.From(UpstreamCredential)), "no credential",
            catalog: _ => throw new HttpRequestException("deployments: HTTP 403"));
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var response = await client.GetAsync($"{proxy.Endpoint}/pks/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Be("catalog unavailable: deployments: HTTP 403");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_absent_credential_is_a_502_rather_than_an_unauthenticated_upstream_call()
    {
        await using var proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken, _upstreamBase, () => Task.FromResult(SecretValue.None), "sign in first");
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ProxyToken);

        var response = await client.GetAsync($"{proxy.Endpoint}/http/whoami");

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync()).Should().Be("sign in first");
        _upstreamAuth.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_stopped_proxy_no_longer_accepts_connections()
    {
        var proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken, _upstreamBase, () => Task.FromResult(SecretValue.From("x")), "none");
        var endpoint = proxy.Endpoint;

        proxy.Stop();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var call = () => client.GetAsync($"{endpoint}/http/whoami");
        await call.Should().ThrowAsync<HttpRequestException>();
    }

    // ─────────────────────────────────────────────
    //  WebSocket
    // ─────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_websocket_upgrade_reaches_upstream_with_the_credential_instead_of_the_proxy_token()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/echo?intent=transcription"), Timeout());

        ws.State.Should().Be(WebSocketState.Open);
        _upstreamAuth.Should().ContainSingle().Which.Should().Be($"Bearer {UpstreamCredential}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Timeout());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task Text_and_binary_messages_roundtrip_with_their_types_intact()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"session.update\"}"), WebSocketMessageType.Text, true, Timeout());
        var text = await ReceiveMessage(ws, Timeout());
        text.Type.Should().Be(WebSocketMessageType.Text);
        text.Text.Should().Be("{\"type\":\"session.update\"}");

        await ws.SendAsync(new byte[] { 0x41, 0x42, 0x43 }, WebSocketMessageType.Binary, true, Timeout());
        var binary = await ReceiveMessage(ws, Timeout());
        binary.Type.Should().Be(WebSocketMessageType.Binary);
        binary.Text.Should().Be("ABC");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Timeout());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_fragmented_message_arrives_upstream_as_one_message()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        // One message in three frames, then a second message. Merging frames across the boundary,
        // or ending the first one early, would show as the wrong count or the wrong text.
        await ws.SendAsync(Encoding.UTF8.GetBytes("hel"), WebSocketMessageType.Text, false, Timeout());
        await ws.SendAsync(Encoding.UTF8.GetBytes("lo, "), WebSocketMessageType.Text, false, Timeout());
        await ws.SendAsync(Encoding.UTF8.GetBytes("world"), WebSocketMessageType.Text, true, Timeout());
        await ws.SendAsync(Encoding.UTF8.GetBytes("second"), WebSocketMessageType.Text, true, Timeout());

        (await ReceiveMessage(ws, Timeout())).Text.Should().Be("hello, world");
        (await ReceiveMessage(ws, Timeout())).Text.Should().Be("second");
        _upstreamMessages.Select(m => m.Text).Should().Equal("hello, world", "second");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Timeout());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_large_message_comes_through_whole()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        var big = new string('x', 1_000_000);
        await ws.SendAsync(Encoding.UTF8.GetBytes(big), WebSocketMessageType.Text, true, Timeout());

        (await ReceiveMessage(ws, Timeout())).Text.Should().Be(big);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Timeout());
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_close_from_the_client_reaches_upstream_with_its_status_and_description()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        await ws.CloseAsync((WebSocketCloseStatus)4002, "client finished", Timeout());

        var seen = await _upstreamSawClose.Task.WaitAsync(TimeSpan.FromSeconds(10));
        seen.Status.Should().Be((WebSocketCloseStatus)4002);
        seen.Description.Should().Be("client finished");
        ws.State.Should().Be(WebSocketState.Closed);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_close_from_upstream_reaches_the_client_with_its_status_and_description()
    {
        using var ws = NewClient();
        await ws.ConnectAsync(WsUri(_proxy, "/ws/hangup"), Timeout());

        (await ReceiveMessage(ws, Timeout())).Text.Should().Be("goodbye");
        var close = await ReceiveMessage(ws, Timeout());

        close.Type.Should().Be(WebSocketMessageType.Close);
        close.Last.CloseStatus.Should().Be((WebSocketCloseStatus)4001);
        close.Last.CloseStatusDescription.Should().Be("session over");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_websocket_upgrade_with_the_wrong_proxy_token_is_refused_before_upstream_is_contacted()
    {
        using var ws = NewClient("wrong-token");

        var connect = () => ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        await connect.Should().ThrowAsync<WebSocketException>();
        ws.HttpStatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _upstreamAuth.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task A_websocket_upgrade_without_any_token_is_refused()
    {
        using var ws = NewClient(token: null);

        var connect = () => ws.ConnectAsync(WsUri(_proxy, "/ws/echo"), Timeout());

        await connect.Should().ThrowAsync<WebSocketException>();
        ws.HttpStatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_upstream_that_refuses_the_upgrade_is_a_502_to_the_client()
    {
        using var ws = NewClient();

        // Upstream answers this upgrade with a 404, so the handshake fails there — which must surface
        // as an HTTP status here, not as an accepted socket that immediately dies.
        var connect = () => ws.ConnectAsync(WsUri(_proxy, "/no-such-ws"), Timeout());

        await connect.Should().ThrowAsync<WebSocketException>();
        ws.HttpStatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public async Task An_absent_credential_refuses_the_upgrade_with_502()
    {
        await using var proxy = await LocalCredentialProxy.StartAsync(
            ProxyToken, _upstreamBase, () => Task.FromResult(SecretValue.None), "sign in first");
        using var ws = NewClient();

        var connect = () => ws.ConnectAsync(WsUri(proxy, "/ws/echo"), Timeout());

        await connect.Should().ThrowAsync<WebSocketException>();
        ws.HttpStatusCode.Should().Be(HttpStatusCode.BadGateway);
        _upstreamAuth.Should().BeEmpty();
    }
}
