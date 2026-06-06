using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Agent.Foundry;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// A thin loopback proxy that lets the genuine <c>codex</c> CLI run natively against an Azure AI
/// Foundry Responses deployment. Unlike <c>pks claude codex</c> (which translates Anthropic ⇄
/// Responses), this forwards the Responses request/response <b>verbatim</b> — its only job is to
/// inject fresh Foundry auth (api-key or refreshed bearer) on every request so long sessions never
/// hit the ~1h AAD token expiry that an env-var-once CLI would.
///
/// Codex points <c>base_url</c> at <c>http://127.0.0.1:{Port}/openai/v1</c> and authenticates to the
/// proxy with the per-run token in <c>PKS_CODEX_TOKEN</c>.
/// </summary>
public sealed class FoundryResponsesPassthrough
{
    private readonly FoundryStoredCredentials _creds;
    private readonly IAzureFoundryAuthService _authService;
    private readonly string _cognitiveScope;
    private readonly string _proxyToken;
    private readonly string _upstreamUrl;
    private WebApplication? _app;

    public int Port { get; }

    public FoundryResponsesPassthrough(
        FoundryStoredCredentials creds,
        IAzureFoundryAuthService authService,
        string cognitiveScope,
        string proxyToken,
        int port)
    {
        _creds = creds;
        _authService = authService;
        _cognitiveScope = cognitiveScope;
        _proxyToken = proxyToken;
        Port = port;
        _upstreamUrl = FoundryResponsesEndpoint.BuildResponsesUrl(creds.SelectedResourceEndpoint);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://127.0.0.1:{Port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("codex-passthrough")
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        var app = builder.Build();
        var factory = app.Services.GetRequiredService<IHttpClientFactory>();

        // codex (wire_api=responses) POSTs to {base_url}/responses. Accept the path under the
        // configured base_url plus a couple of tolerant shapes; the query string (api-version) is ignored.
        Task Handle(HttpContext ctx) => ForwardAsync(ctx, factory);
        app.MapPost("/openai/v1/responses", Handle);
        app.MapPost("/v1/responses", Handle);
        app.MapPost("/responses", Handle);

        _app = app;
        await app.StartAsync(ct);
    }

    private async Task ForwardAsync(HttpContext ctx, IHttpClientFactory factory)
    {
        if (!AnthropicProxyUtil.ValidateToken(ctx, _proxyToken)) return;

        // Buffer the request body verbatim — no translation.
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, _upstreamUrl)
        {
            Content = new ByteArrayContent(ms.ToArray()),
        };
        upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(
            upstreamReq, _creds, _authService, _cognitiveScope, ctx.RequestAborted);

        var client = factory.CreateClient("codex-passthrough");
        using var upstream = await client.SendAsync(
            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        if (!upstream.IsSuccessStatusCode)
        {
            await AnthropicProxyUtil.RelayUpstreamErrorAsync(ctx, upstream);
            return;
        }

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        var contentType = upstream.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType)) ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-cache";

        // Raw byte copy with per-chunk flush so SSE streams incrementally back to codex.
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        try { await _app.StopAsync(); }
        catch { /* never started (e.g. bind failure) — dispose is enough */ }
        await _app.DisposeAsync();
        _app = null;
    }
}
