using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenRouter;

/// <summary>
/// Starts a local OpenAI-compatible endpoint that accepts a throwaway proxy token and forwards to
/// OpenRouter signed with the registered API key.
///
/// This exists because the stored credential has no read path out of pks — deliberately, and there
/// will not be an export command. A local script that needs OpenRouter therefore does not get the
/// key; it gets a loopback URL and a token that dies with this process:
///
///   eval $(pks openrouter proxy)
///   NEMO_BASE_URL=$OPENROUTER_PROXY_URL NEMO_API_KEY=$OPENROUTER_PROXY_TOKEN python3 run_llm_cleanup.py
///
/// Same shape as <c>pks foundry proxy</c>. Loopback only, and the token is per-process, so nothing
/// reusable lands in a shell history, an env file or a scrollback buffer.
/// </summary>
[Description("Start a local OpenAI-compatible proxy that signs requests with the stored OpenRouter key")]
public sealed class OpenRouterProxyCommand : AsyncCommand<OpenRouterProxyCommand.Settings>
{
    private readonly IOpenRouterService _openRouter;
    private readonly IAnsiConsole _console;

    public OpenRouterProxyCommand(IOpenRouterService openRouter, IAnsiConsole console)
    {
        _openRouter = openRouter;
        _console = console;
    }

    public sealed class Settings : OpenRouterSettings
    {
        [CommandOption("-p|--port")]
        [Description("Port to listen on (default: random free port)")]
        public int? Port { get; set; }

        [CommandOption("-t|--token")]
        [Description("Proxy token clients must send in the Authorization header (default: random)")]
        public string? Token { get; set; }
    }

    // Hop-by-hop headers plus the two we replace ourselves.
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Transfer-Encoding",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Upgrade",
    };

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var credentials = await _openRouter.GetStoredCredentialsAsync();
        if (credentials?.ApiKey.HasValue != true)
        {
            _console.MarkupLine("[red]No OpenRouter API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks openrouter init[/] first.[/]");
            return 1;
        }

        var port = settings.Port ?? FindFreePort();
        var proxyToken = settings.Token ?? Guid.NewGuid().ToString("N");
        var upstreamBase = OpenRouterService.BaseUrl.TrimEnd('/');

        // Printed before the server starts so `eval $(pks openrouter proxy)` captures them.
        Console.WriteLine($"export OPENROUTER_PROXY_URL=http://localhost:{port}");
        Console.WriteLine($"export OPENROUTER_PROXY_TOKEN={proxyToken}");

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("openrouter-proxy")
            // Streaming completions hold a connection open far past the 100s default.
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        var app = builder.Build();
        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        app.MapMethods("{**path}", AllHttpMethods, async (HttpContext ctx) =>
        {
            var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? "";
            if (authHeader != $"Bearer {proxyToken}")
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Unauthorized: invalid proxy token");
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
            }

            foreach (var header in ctx.Request.Headers)
            {
                if (SkipHeaders.Contains(header.Key)) continue;
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Re-read per request: the command never holds the key as a string, and a rotation
            // through `init --force` takes effect without restarting the proxy.
            var current = await _openRouter.GetStoredCredentialsAsync();
            if (current?.ApiKey.HasValue != true ||
                !SecretSink.SetBearerToken(upstreamRequest, current.ApiKey))
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync("No OpenRouter API key registered — run pks openrouter init");
                return;
            }

            var client = httpClientFactory.CreateClient("openrouter-proxy");
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
        });

        await app.RunAsync();
        return 0;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
