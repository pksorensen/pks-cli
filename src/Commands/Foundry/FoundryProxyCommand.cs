using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Starts a local HTTP proxy that accepts requests authenticated with a throwaway proxy token,
/// acquires a real Azure AI Foundry bearer token via the stored OAuth2 credentials,
/// and forwards the request to the real Azure endpoint.
///
/// Usage pattern (bash):
///   eval $(pks foundry proxy)
///   # Now FOUNDRY_PROXY_URL and FOUNDRY_PROXY_TOKEN are set in the current shell.
///   # Point NarrationGenerator or any client at the proxy URL with the proxy token.
/// </summary>
[Description("Start a local HTTP proxy that swaps a proxy token for a real Azure AI Foundry bearer token")]
public class FoundryProxyCommand : AsyncCommand<FoundryProxyCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public FoundryProxyCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public class Settings : FoundrySettings
    {
        [CommandOption("-p|--port")]
        [Description("Port to listen on (default: random free port)")]
        public int? Port { get; set; }

        [CommandOption("-t|--token")]
        [Description("Proxy token clients must send in Authorization header (default: random UUID)")]
        public string? Token { get; set; }

        [CommandOption("-s|--scope")]
        [Description("Azure token scope (default: cognitive services)")]
        public string? Scope { get; set; }
    }

    // Headers that must not be forwarded to the upstream (hop-by-hop + security)
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
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] first.[/]");
            return 1;
        }

        var port = settings.Port ?? FindFreePort();
        var proxyToken = settings.Token ?? Guid.NewGuid().ToString("N");
        var scope = settings.Scope ?? _config.CognitiveScope;

        // Print eval-friendly env vars BEFORE starting the server so the caller
        // can capture them via: eval $(pks foundry proxy)
        Console.WriteLine($"export FOUNDRY_PROXY_URL=http://localhost:{port}");
        Console.WriteLine($"export FOUNDRY_PROXY_TOKEN={proxyToken}");

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        // Use a dedicated singleton HttpClient for upstream calls
        builder.Services.AddHttpClient("foundry-proxy");

        var app = builder.Build();

        var httpClientFactory = app.Services.GetRequiredService<IHttpClientFactory>();

        app.MapMethods("{**path}", AllHttpMethods, async (HttpContext ctx) =>
        {
            // Validate proxy token — use raw indexer to avoid typed accessor quirks
            var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? "";
            if (authHeader != $"Bearer {proxyToken}")
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync("Unauthorized: invalid proxy token");
                return;
            }

            // Acquire real Azure token
            var realToken = await _authService.GetAccessTokenAsync(scope);
            if (string.IsNullOrEmpty(realToken))
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync("Failed to acquire Azure access token");
                return;
            }

            // Get upstream endpoint from stored credentials
            var creds = await _authService.GetStoredCredentialsAsync();
            if (creds == null || string.IsNullOrEmpty(creds.SelectedResourceEndpoint))
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync("No Foundry endpoint configured — run pks foundry init");
                return;
            }

            var upstreamBase = creds.SelectedResourceEndpoint.TrimEnd('/');
            var upstreamUrl = $"{upstreamBase}{ctx.Request.Path}{ctx.Request.QueryString}";

            using var upstreamRequest = new HttpRequestMessage(
                new HttpMethod(ctx.Request.Method), upstreamUrl);

            // Forward body for methods that carry one
            if (ctx.Request.ContentLength > 0 ||
                ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamRequest.Content = new StreamContent(ctx.Request.Body);
                if (ctx.Request.ContentType != null)
                    upstreamRequest.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
            }

            // Forward request headers, replacing Authorization with the real token
            foreach (var header in ctx.Request.Headers)
            {
                if (SkipHeaders.Contains(header.Key)) continue;
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            upstreamRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", realToken);

            // Send to Azure and stream response back
            var client = httpClientFactory.CreateClient("foundry-proxy");
            using var upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);

            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

            foreach (var header in upstreamResponse.Headers)
                ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
            foreach (var header in upstreamResponse.Content.Headers)
                ctx.Response.Headers.Append(header.Key, header.Value.ToArray());

            // Kestrel handles chunked encoding — strip the upstream header
            ctx.Response.Headers.Remove("Transfer-Encoding");

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
