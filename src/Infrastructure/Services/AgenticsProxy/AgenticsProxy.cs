using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Services.AgenticsProxy;

/// <summary>
/// Credential-injecting proxy for runner jobs.
///
/// Agents obtain a short-lived capability token from POST /api/token, then use it
/// to call any pre-approved host through the proxy. The proxy acquires a real Azure
/// bearer token server-side and forwards the request — agents never see real credentials.
///
/// Also exposes:
///   GET  /api/resources  — lists allowed hosts, available endpoints, and token endpoint schema
///   POST /api/token      — issues a capability token for a named host
///
/// In spawn/container mode the proxy additionally listens on a Unix socket whose directory
/// is bind-mounted into the container. Container-side agents use:
///   curl --unix-socket $AGENTICS_PROXY_SOCKET http://localhost/api/token
///
/// Lifecycle: call StartAsync() at job start; dispose when the job completes.
/// </summary>
public sealed class AgenticsProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly AgenticsProxyOptions _options;
    private readonly IAzureFoundryAuthService _authService;
    private readonly ConcurrentDictionary<string, CapabilityToken> _tokens = new();

    public int Port { get; }
    public string BootstrapToken => _options.BootstrapToken;

    /// <summary>Socket directory bind-mounted into spawn-mode containers.</summary>
    public string? SocketDir { get; }

    /// <summary>Full path to the Unix socket file (null if no socket was created).</summary>
    public string? SocketPath { get; }

    private sealed record CapabilityToken(string Host, string JobId, DateTimeOffset IssuedAt);

    private static readonly HashSet<string> SkipHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Host", "Connection", "Transfer-Encoding",
        "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailers", "Upgrade",
    };

    private static readonly string[] AllHttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    private readonly bool _socketDirIsExternal;

    private AgenticsProxy(
        int port,
        string? socketDir,
        string? socketPath,
        AgenticsProxyOptions options,
        IAzureFoundryAuthService authService,
        bool socketDirIsExternal = false)
    {
        Port = port;
        SocketDir = socketDir;
        SocketPath = socketPath;
        _options = options;
        _authService = authService;
        _socketDirIsExternal = socketDirIsExternal;

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        builder.WebHost.UseSetting("suppressStatusMessages", "true");
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenLocalhost(port);
            if (socketPath != null)
                kestrel.ListenUnixSocket(socketPath);
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);
        builder.Services.AddHttpClient("agentics-proxy");

        _app = builder.Build();

        _app.MapGet("/api/resources", HandleResourcesRequestAsync);
        _app.MapPost("/api/token", HandleTokenRequestAsync);
        _app.MapMethods("{**path}", AllHttpMethods, HandleProxyRequestAsync);
    }

    public static async Task<AgenticsProxy> StartAsync(
        AgenticsProxyOptions options,
        IAzureFoundryAuthService authService,
        bool createSocket = false,
        string? socketDirOverride = null,
        CancellationToken ct = default)
    {
        var port = FindFreePort();

        string? socketDir = null;
        string? socketPath = null;
        bool socketDirIsExternal = false;
        if (createSocket)
        {
            // socketDirOverride lets the runner use a stable per-task path so the warm container's
            // bind mount stays valid across job cycles within the same task — see ADR 0003.
            // Without it, every job created /tmp/pks-agentics-{jobId} but the container's mount
            // kept pointing at the FIRST job's dir.
            socketDir = socketDirOverride ?? Path.Combine(Path.GetTempPath(), $"pks-agentics-{options.JobId}");
            socketDirIsExternal = socketDirOverride != null;
            Directory.CreateDirectory(socketDir);
            socketPath = Path.Combine(socketDir, "proxy.sock");
            // Unix sockets don't bind over an existing file — remove any stale one from a
            // previous proxy that wrote to this dir.
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }

        var proxy = new AgenticsProxy(port, socketDir, socketPath, options, authService, socketDirIsExternal);
        await proxy._app.StartAsync(ct);

        if (socketPath != null && File.Exists(socketPath) &&
            (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        return proxy;
    }

    private Task HandleResourcesRequestAsync(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? "";
        if (authHeader != $"Bearer {_options.BootstrapToken}")
        {
            ctx.Response.StatusCode = 401;
            return ctx.Response.WriteAsync("Unauthorized: invalid bootstrap token");
        }

        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            hosts = _options.AllowedHosts.Select(kvp => new
            {
                host = kvp.Key,
                scope = kvp.Value.TokenScope,
            }).ToArray(),
            endpoints = new[]
            {
                new
                {
                    name = "tts",
                    description = "Azure OpenAI Text-to-Speech",
                    path = "/openai/deployments/tts-hd/audio/speech",
                    api_version = "2025-03-01-preview",
                    method = "POST",
                    model = "tts-hd",
                    voices = new[] { "alloy", "echo", "fable", "onyx", "nova", "shimmer" },
                    host_suffix = ".cognitiveservices.azure.com",
                    example_body = """{"model":"tts-hd","input":"Hello world","voice":"alloy"}""",
                    response = "audio/mpeg (MP3 bytes)",
                },
            },
            token_endpoint = new
            {
                description = "Exchange bootstrap token for a short-lived capability token scoped to one host",
                path = "/api/token",
                method = "POST",
                auth_header = "Authorization: Bearer $AGENTICS_PROXY_TOKEN",
                body_example = """{"host":"<hostname from hosts list>"}""",
                response_example = """{"token":"<cap-token>","host":"...","expires_in":3600}""",
            },
        }));
    }

    private async Task HandleTokenRequestAsync(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? "";
        if (authHeader != $"Bearer {_options.BootstrapToken}")
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized: invalid bootstrap token");
            return;
        }

        string? host;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            host = doc.RootElement.GetProperty("host").GetString();
        }
        catch
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Bad request: expected JSON body with \"host\" field");
            return;
        }

        if (string.IsNullOrEmpty(host) || !_options.AllowedHosts.ContainsKey(host))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync($"Forbidden: host '{host}' is not in the allowed list");
            return;
        }

        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new CapabilityToken(host, _options.JobId, DateTimeOffset.UtcNow);

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            token,
            host,
            expires_in = 3600,
        }));
    }

    private async Task HandleProxyRequestAsync(HttpContext ctx)
    {
        var authHeader = ctx.Request.Headers["Authorization"].FirstOrDefault() ?? "";
        if (!authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized: missing capability token");
            return;
        }

        var capToken = authHeader["Bearer ".Length..];
        if (!_tokens.TryGetValue(capToken, out var tokenInfo))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized: invalid or expired capability token");
            return;
        }

        var policy = _options.AllowedHosts[tokenInfo.Host];
        var path = ctx.Request.Path.Value ?? "/";

        if (!IsPathAllowed(policy, path))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsync($"Forbidden: path '{path}' is not permitted for this host");
            return;
        }

        var realToken = await _authService.GetAccessTokenAsync(policy.TokenScope);
        if (string.IsNullOrEmpty(realToken))
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync("Failed to acquire Azure access token");
            return;
        }

        var upstreamUrl = $"https://{tokenInfo.Host}{ctx.Request.Path}{ctx.Request.QueryString}";

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
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", realToken);

        var httpClientFactory = ctx.RequestServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("agentics-proxy");

        using var upstreamResponse = await client.SendAsync(
            upstreamRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ctx.RequestAborted);

        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

        foreach (var header in upstreamResponse.Headers)
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());
        foreach (var header in upstreamResponse.Content.Headers)
            ctx.Response.Headers.Append(header.Key, header.Value.ToArray());

        ctx.Response.Headers.Remove("Transfer-Encoding");

        await upstreamResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private static bool IsPathAllowed(HostPolicy policy, string path)
    {
        if (policy.DeniedPaths.Any(p => MatchesGlob(p, path)))
            return false;
        if (policy.AllowedPaths.Count == 0)
            return true;
        return policy.AllowedPaths.Any(p => MatchesGlob(p, path));
    }

    private static bool MatchesGlob(string pattern, string path)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _tokens.Clear();
        await _app.StopAsync();
        await _app.DisposeAsync();
        // Externally-managed dir (per-task per ADR 0003): leave it in place so the warm
        // container's bind mount remains valid for the next job. Just clean the socket file.
        if (_socketDirIsExternal && SocketPath != null)
        {
            try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
            return;
        }
        if (SocketDir != null && Directory.Exists(SocketDir))
            Directory.Delete(SocketDir, recursive: true);
    }
}
