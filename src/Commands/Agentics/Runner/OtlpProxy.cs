using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Lightweight OTLP HTTP broadcast proxy for runner job telemetry.
///
/// Uses TcpListener (HttpListener is broken on Linux/.NET 10 — it starts without
/// error but silently never accepts connections).
///
/// Listens on a random localhost port. Vibecast and Claude point their
/// OTEL_EXPORTER_OTLP_ENDPOINT here. The proxy fans out each request to:
///   1. The real Aspire OTLP endpoint (from OTEL_EXPORTER_OTLP_ENDPOINT env var)
///   2. The Next.js /api/otel endpoint (for project-level analysis)
///
/// Only JSON-format requests are forwarded to Next.js (protobuf-only to Aspire).
/// Add OTEL_EXPORTER_OTLP_PROTOCOL=http/json to vibecast's env to enable
/// both targets simultaneously.
///
/// Resource attribute enrichment (job.id, run.id, task.id) is via
/// OTEL_RESOURCE_ATTRIBUTES — no protobuf parsing needed.
/// </summary>
internal sealed class OtlpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly HttpClient _forwardClient;
    private readonly string? _aspireEndpoint;
    private readonly string? _aspireHeaders;
    private readonly string? _analysisEndpoint; // e.g. "http://localhost:37411/api/otel"
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loopTask;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public int Port { get; }

    /// <summary>UTC time of the last OTLP request received (from any sender).</summary>
    public DateTime LastActivityAt => new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);

    private OtlpProxy(TcpListener listener, int port, string? aspireEndpoint, string? analysisEndpoint)
    {
        _listener = listener;
        Port = port;
        _aspireEndpoint = aspireEndpoint?.TrimEnd('/');
        _aspireHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        _analysisEndpoint = analysisEndpoint?.TrimEnd('/');
        // Disable SSL validation for localhost OTLP endpoints (Aspire uses a self-signed cert).
        _forwardClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        }) { Timeout = TimeSpan.FromSeconds(10) };
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Start the proxy. Returns immediately; proxy runs in the background.
    /// <paramref name="analysisBaseUrl"/> is the base URL of the ws-relay/Next.js
    /// server (e.g. "http://localhost:37411") — /api/otel will be appended.
    /// </summary>
    public static OtlpProxy Start(string? analysisBaseUrl = null)
    {
        var aspireEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var analysisEndpoint = string.IsNullOrEmpty(analysisBaseUrl)
            ? null
            : analysisBaseUrl.TrimEnd('/') + "/api/otel";

        var port = FindFreePort();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        return new OtlpProxy(listener, port, aspireEndpoint, analysisEndpoint);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch { break; }

            // Fire-and-forget each connection
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken ct)
    {
        // Record activity — any OTLP request means the job is actively working.
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

        using (tcpClient)
        {
            try
            {
                using var stream = tcpClient.GetStream();
                var (method, path, contentType, body) = await ReadHttpRequestAsync(stream, ct);

                // Fan out to both upstreams in parallel
                var tasks = new List<Task>();
                if (!string.IsNullOrEmpty(_aspireEndpoint))
                    tasks.Add(ForwardToAspireAsync(method, path, contentType, body, ct));
                if (!string.IsNullOrEmpty(_analysisEndpoint))
                    tasks.Add(ForwardToAnalysisAsync(method, path, contentType, body, ct));

                await Task.WhenAll(tasks);

                // Return OTLP success response
                var ok = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 22\r\nConnection: close\r\n\r\n{\"partialSuccess\":{}}"u8.ToArray();
                await stream.WriteAsync(ok, ct);
            }
            catch
            {
                // Ignore per-connection errors
            }
        }
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
        // Set OTEL_EXPORTER_OTLP_PROTOCOL=http/json in vibecast's env to enable this path.
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

    /// <summary>
    /// Reads a single HTTP/1.1 request from the stream and extracts
    /// method, path, content-type, and body.
    /// </summary>
    private static async Task<(string method, string path, string contentType, byte[] body)>
        ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read header section (terminated by \r\n\r\n)
        var headerBuf = new List<byte>(4096);
        var tail = new byte[4];
        while (true)
        {
            var b = await ReadByteAsync(stream, ct);
            if (b < 0) break;
            headerBuf.Add((byte)b);

            // Shift tail window and check for \r\n\r\n
            tail[0] = tail[1]; tail[1] = tail[2]; tail[2] = tail[3]; tail[3] = (byte)b;
            if (tail[0] == '\r' && tail[1] == '\n' && tail[2] == '\r' && tail[3] == '\n')
                break;
        }

        var headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Parse request line: "POST /v1/traces HTTP/1.1"
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : [];
        var method = requestLine.Length > 0 ? requestLine[0] : "POST";
        var path = requestLine.Length > 1 ? requestLine[1] : "/";

        var contentType = "application/x-protobuf";
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                contentType = value;
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out contentLength);
        }

        // Read body
        var body = new byte[Math.Max(contentLength, 0)];
        if (contentLength > 0)
        {
            var read = 0;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), ct);
                if (n == 0) break;
                read += n;
            }
        }

        return (method, path, contentType, body);
    }

    private static async Task<int> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[1];
        var n = await stream.ReadAsync(buf.AsMemory(0, 1), ct);
        return n == 0 ? -1 : buf[0];
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
        await _cts.CancelAsync();
        _listener.Stop();
        try { await _loopTask; } catch { }
        _forwardClient.Dispose();
        _cts.Dispose();
    }
}
