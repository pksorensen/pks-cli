using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Lightweight OTLP HTTP broadcast proxy for runner job telemetry.
///
/// Implemented on Kestrel with a dual-bind: TCP loopback for in-process callers
/// (vibecast/Claude on the runner host) and an optional Unix socket for spawn-mode
/// containers. The container side runs a tiny TCP→Unix bridge so vibecast/Claude
/// keep using the standard OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318 form.
///
/// Listens on a random localhost port. Each received OTLP request is fanned out to:
///   1. The real Aspire OTLP endpoint (from OTEL_EXPORTER_OTLP_ENDPOINT env var)
///   2. The Next.js /api/otel endpoint (for project-level analysis) — JSON only
///
/// Add OTEL_EXPORTER_OTLP_PROTOCOL=http/json on the agent so both targets receive data.
///
/// Resource attribute enrichment (job.id, run.id, task.id) is via OTEL_RESOURCE_ATTRIBUTES
/// — no protobuf parsing needed.
/// </summary>
internal sealed class OtlpProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _forwardClient;
    private readonly string? _aspireEndpoint;
    private readonly string? _aspireHeaders;
    private readonly string? _analysisEndpoint;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public int Port { get; }

    /// <summary>Socket directory bind-mounted into spawn-mode containers (null for in-process).</summary>
    public string? SocketDir { get; }

    /// <summary>Full path to the Unix socket file (null when no socket was created).</summary>
    public string? SocketPath { get; }

    /// <summary>UTC time of the last OTLP request received (from any sender).</summary>
    public DateTime LastActivityAt => new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    private readonly bool _socketDirIsExternal;

    private OtlpProxy(int port, string? socketDir, string? socketPath, string? aspireEndpoint, string? analysisEndpoint, bool socketDirIsExternal = false)
    {
        Port = port;
        SocketDir = socketDir;
        SocketPath = socketPath;
        _socketDirIsExternal = socketDirIsExternal;
        _aspireEndpoint = aspireEndpoint?.TrimEnd('/');
        _aspireHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        _analysisEndpoint = analysisEndpoint?.TrimEnd('/');

        // Disable SSL validation for localhost OTLP endpoints (Aspire uses a self-signed cert).
        _forwardClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
        { Timeout = TimeSpan.FromSeconds(10) };

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

        _app = builder.Build();

        _app.MapMethods("/v1/{**path}", new[] { "POST", "PUT", "GET" }, HandleOtlpAsync);
        _app.MapMethods("{**path}", new[] { "POST", "PUT", "GET" }, HandleOtlpAsync);
    }

    /// <summary>
    /// Start the proxy. Returns when bound and ready.
    /// <paramref name="analysisBaseUrl"/> is the base URL of the ws-relay/Next.js
    /// server (e.g. "http://localhost:37411") — /api/otel will be appended.
    /// When <paramref name="createSocket"/> is true, an additional Unix socket is
    /// created in a per-job directory suitable for bind-mounting into a container.
    /// </summary>
    public static async Task<OtlpProxy> StartAsync(
        string? analysisBaseUrl = null,
        string? jobId = null,
        bool createSocket = false,
        string? socketDirOverride = null,
        CancellationToken ct = default)
    {
        var aspireEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var analysisEndpoint = string.IsNullOrEmpty(analysisBaseUrl)
            ? null
            : analysisBaseUrl.TrimEnd('/') + "/api/otel";

        string? socketDir = null;
        string? socketPath = null;
        bool socketDirIsExternal = false;
        if (createSocket)
        {
            // socketDirOverride keeps the dir stable per task (ADR 0003) so the warm container's
            // bind mount stays valid across job cycles.
            if (socketDirOverride != null)
            {
                socketDir = socketDirOverride;
                socketDirIsExternal = true;
            }
            else
            {
                var dirSuffix = string.IsNullOrEmpty(jobId) ? Guid.NewGuid().ToString("N") : jobId;
                socketDir = Path.Combine(Path.GetTempPath(), $"pks-otlp-{dirSuffix}");
            }
            Directory.CreateDirectory(socketDir);
            socketPath = Path.Combine(socketDir, "otlp.sock");
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }

        var port = FindFreePort();
        var proxy = new OtlpProxy(port, socketDir, socketPath, aspireEndpoint, analysisEndpoint, socketDirIsExternal);
        await proxy._app.StartAsync(ct);

        if (socketPath != null && File.Exists(socketPath) &&
            (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
        {
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute  |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }

        return proxy;
    }

    /// <summary>
    /// Backwards-compatible synchronous-looking entry point used by in-process mode.
    /// Equivalent to <see cref="StartAsync(string?, string?, bool, CancellationToken)"/> with no socket.
    /// </summary>
    public static OtlpProxy Start(string? analysisBaseUrl = null)
        => StartAsync(analysisBaseUrl).GetAwaiter().GetResult();

    private async Task HandleOtlpAsync(HttpContext ctx)
    {
        // Record activity — any OTLP request means the job is actively working.
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        var contentType = ctx.Request.ContentType ?? "application/x-protobuf";
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var body = ms.ToArray();

        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;

        var tasks = new List<Task>();
        if (!string.IsNullOrEmpty(_aspireEndpoint))
            tasks.Add(ForwardToAspireAsync(method, path, contentType, body, ctx.RequestAborted));
        if (!string.IsNullOrEmpty(_analysisEndpoint))
            tasks.Add(ForwardToAnalysisAsync(method, path, contentType, body, ctx.RequestAborted));

        try { await Task.WhenAll(tasks); } catch { /* per-target errors swallowed below */ }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"partialSuccess\":{}}", ctx.RequestAborted);
    }

    private async Task ForwardToAspireAsync(string method, string path, string contentType, byte[] body, CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(new HttpMethod(method), _aspireEndpoint + path)
            {
                Content = new ByteArrayContent(body)
            };
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

            // Forward Aspire API key headers (e.g. x-otlp-api-key)
            if (!string.IsNullOrEmpty(_aspireHeaders))
            {
                foreach (var pair in _aspireHeaders.Split(','))
                {
                    var eq = pair.IndexOf('=');
                    if (eq > 0)
                        req.Headers.TryAddWithoutValidation(pair[..eq].Trim(), pair[(eq + 1)..].Trim());
                }
            }

            await _forwardClient.SendAsync(req, ct);
        }
        catch { /* upstream unreachable — silently drop */ }
    }

    private async Task ForwardToAnalysisAsync(string method, string path, string contentType, byte[] body, CancellationToken ct)
    {
        // Next.js routes use request.json() so only forward JSON-encoded payloads.
        // Set OTEL_EXPORTER_OTLP_PROTOCOL=http/json on the agent to enable this path.
        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var req = new HttpRequestMessage(new HttpMethod(method), _analysisEndpoint + path)
            {
                Content = new ByteArrayContent(body)
            };
            req.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            await _forwardClient.SendAsync(req, ct);
        }
        catch { /* analysis endpoint unreachable */ }
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
        try { await _app.StopAsync(); } catch { }
        await _app.DisposeAsync();
        _forwardClient.Dispose();
        // Externally-managed dir (per-task per ADR 0003): leave it in place. Just clean the socket.
        if (_socketDirIsExternal && SocketPath != null)
        {
            try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
            return;
        }
        if (SocketDir != null && Directory.Exists(SocketDir))
        {
            try { Directory.Delete(SocketDir, recursive: true); } catch { }
        }
    }
}
