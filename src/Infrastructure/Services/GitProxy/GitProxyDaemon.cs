using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.GitProxy;

/// <summary>
/// In-process credential-injecting reverse proxy for git smart-HTTP operations.
/// Mirrors the working pattern from AdoGitProxyCommand: client-side `git config
/// url.&lt;proxy&gt;.insteadOf &lt;upstream&gt;` routes the clone through here; the proxy
/// strips any inbound Authorization, looks up a token source for the matched
/// upstream, and forwards with `Authorization: Bearer &lt;token&gt;`.
///
/// This implementation is intentionally consumer-agnostic — the registration
/// table is the single source of truth for "what URL prefix gets what token
/// source." The agentics runner uses this for marketplace plugin clones; the
/// existing AdoGitProxyCommand can be refactored onto this in a follow-up.
///
/// Lifetime: instance-per-consumer. The agentics runner spins one up per job
/// on an ephemeral port, registers entries, configures git inside the
/// container, then disposes when the job finishes. No shared global daemon —
/// keeps credentials isolated to the job that needs them.
/// </summary>
public sealed class GitProxyDaemon : IAsyncDisposable
{
    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Transfer-Encoding",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Upgrade",
    };

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private readonly ILogger<GitProxyDaemon>? _logger;
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication? _app;
    private int _port;
    private bool _started;

    public GitProxyDaemon(ILogger<GitProxyDaemon>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>The TCP port the proxy is bound to. Valid only after StartAsync.</summary>
    public int Port => _started ? _port : throw new InvalidOperationException("GitProxyDaemon not started");

    /// <summary>
    /// Starts the proxy on an ephemeral port bound to all interfaces (so the
    /// devcontainer can reach it via <c>host.docker.internal:&lt;port&gt;</c> or
    /// <c>172.17.0.1:&lt;port&gt;</c> depending on the container's networking).
    /// </summary>
    public async Task StartAsync(int port = 0, CancellationToken ct = default)
    {
        if (_started) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.ListenAnyIP(port);
        });
        // Quiet by default — host process owns the user-facing console.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddHttpClient("git-proxy-upstream");

        _app = builder.Build();
        var httpClientFactory = _app.Services.GetRequiredService<IHttpClientFactory>();

        _app.MapMethods("{**path}", AllHttpMethods, (HttpContext ctx) => HandleAsync(ctx, httpClientFactory, ct));

        await _app.StartAsync(ct);

        // Resolve the actual bound port (we passed 0 → ephemeral).
        var server = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var addr = addresses?.Addresses.FirstOrDefault();
        if (addr != null && Uri.TryCreate(addr, UriKind.Absolute, out var parsed))
        {
            _port = parsed.Port;
        }
        _started = true;
        _logger?.LogInformation("GitProxyDaemon listening on port {Port}", _port);
    }

    /// <summary>
    /// Adds (or replaces) the token source for an upstream URL prefix. The
    /// prefix must end with a slash, matching the upstream side of <c>git config
    /// url.X.insteadOf Y</c>. Example: <c>https://x.devtunnels.ms/</c>.
    ///
    /// Replacement is intentional — re-registering with the same prefix swaps
    /// the token source (useful when a token is refreshed for a long job).
    /// </summary>
    public void Register(string upstreamPrefix, IGitProxyTokenSource tokenSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(upstreamPrefix);
        if (!upstreamPrefix.EndsWith('/')) upstreamPrefix += "/";
        _registrations[NormalizeUpstream(upstreamPrefix)] = new Registration(upstreamPrefix, tokenSource);
        _logger?.LogInformation("Registered upstream {Upstream}", upstreamPrefix);
    }

    public void Deregister(string upstreamPrefix)
    {
        if (string.IsNullOrEmpty(upstreamPrefix)) return;
        if (!upstreamPrefix.EndsWith('/')) upstreamPrefix += "/";
        _registrations.TryRemove(NormalizeUpstream(upstreamPrefix), out _);
        _logger?.LogInformation("Deregistered upstream {Upstream}", upstreamPrefix);
    }

    public IReadOnlyCollection<string> RegisteredUpstreams() => _registrations.Values.Select(r => r.UpstreamPrefix).ToList();

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            try { await _app.StopAsync(); } catch { /* shutting down */ }
            await _app.DisposeAsync();
        }
        _registrations.Clear();
        _started = false;
    }

    // ── Request handling ─────────────────────────────────────────────────────

    private async Task HandleAsync(HttpContext ctx, IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var hostHeader = ctx.Request.Headers.Host.FirstOrDefault() ?? "";
        var path = ctx.Request.Path.Value ?? "";
        var match = ResolveRegistration(hostHeader, path);
        if (match is null)
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("No upstream registered for this request");
            return;
        }

        // Fetch a token. We don't try to be clever about caching here — token
        // sources own that policy. Null → 502 (operator misconfiguration).
        var token = await match.TokenSource.GetTokenAsync(ctx.RequestAborted);
        if (string.IsNullOrEmpty(token))
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"No token available for upstream {match.UpstreamPrefix}");
            return;
        }

        var upstreamUrl = $"{match.UpstreamPrefix.TrimEnd('/')}{path}{ctx.Request.QueryString}";
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamUrl);

        // Forward request body when present (push pack data, etc.)
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstreamRequest.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType != null)
            {
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
            }
        }

        // Forward headers except for hop-by-hop + Authorization (we own auth).
        foreach (var header in ctx.Request.Headers)
        {
            if (SkipHeaders.Contains(header.Key)) continue;
            upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var client = httpClientFactory.CreateClient("git-proxy-upstream");
        using var upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        foreach (var header in upstreamResponse.Headers)
        {
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
        }
        foreach (var header in upstreamResponse.Content.Headers)
        {
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
        }
        ctx.Response.Headers.Remove("Transfer-Encoding");
        await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    /// <summary>
    /// Picks the registration whose upstream authority matches the inbound
    /// Host header. Today the lookup is keyed purely on host — multiple
    /// upstreams sharing a host but differing in path prefix (e.g. two
    /// marketplaces under the same devtunnel) would need a path-aware match;
    /// not a v1 requirement.
    /// </summary>
    private Registration? ResolveRegistration(string hostHeader, string path)
    {
        // The inbound Host header is the proxy itself (host.docker.internal:port).
        // We rely on the registered upstream's authority to route — i.e. the
        // operator told us "any request landing here goes to https://X/".
        //
        // For multi-upstream proxies on a single port we'd need a routing key.
        // The agentics runner currently spins one daemon per job; if a job has
        // N marketplaces under N authorities, all N are registered and we pick
        // by path prefix. Keep the lookup tolerant by checking path-prefix too.
        Registration? best = null;
        int bestPrefixLen = -1;
        foreach (var reg in _registrations.Values)
        {
            // Match by host suffix or path prefix — whichever is present.
            // For marketplace clones we register `https://x.devtunnels.ms/` and
            // the inbound path will be `/agentics-core/default/plugins/...`. The
            // most-specific prefix wins.
            var prefixPath = TryGetUpstreamPath(reg.UpstreamPrefix);
            if (prefixPath.Length > bestPrefixLen && path.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase))
            {
                best = reg;
                bestPrefixLen = prefixPath.Length;
            }
        }
        if (best != null) return best;

        // Last-resort: if there's exactly one registration, use it. Common case
        // for a single-marketplace job.
        if (_registrations.Count == 1) return _registrations.Values.First();
        return null;
    }

    private static string TryGetUpstreamPath(string upstreamPrefix)
    {
        if (Uri.TryCreate(upstreamPrefix, UriKind.Absolute, out var u))
        {
            return u.AbsolutePath; // "/" for a bare-host registration
        }
        return "/";
    }

    private static string NormalizeUpstream(string upstreamPrefix) => upstreamPrefix.TrimEnd('/').ToLowerInvariant();

    private sealed record Registration(string UpstreamPrefix, IGitProxyTokenSource TokenSource);
}
