using System.Net;
using System.Net.Sockets;
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

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

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
        upstreamBase = upstreamBase.TrimEnd('/');

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("credential-proxy")
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

            if (!SecretSink.SetBearerToken(upstreamRequest, await credential()))
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
        });

        // Not app.RunAsync(token) — that overload's single argument is a URL, not a CancellationToken,
        // and WaitForShutdownAsync is an IWebHost extension that WebApplication does not satisfy.
        await app.StartAsync(cancellationToken);
        await ((IHost)app).WaitForShutdownAsync(cancellationToken);
    }
}
