using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKS.Infrastructure.Services.Agent.Anthropic;
using PKS.Infrastructure.Services.Agent.Foundry;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Agent.Codex;

/// <summary>
/// A thin loopback proxy that lets the genuine <c>codex</c> CLI run natively against an Azure AI
/// Foundry Responses deployment. Unlike <c>pks claude codex</c> (which translates Anthropic ⇄
/// Responses), this forwards the Responses request/response <b>verbatim</b> — its only job is to
/// inject fresh Foundry bearer auth on every request so long sessions never hit the ~1h AAD token
/// expiry that an env-var-once CLI would.
///
/// Codex points <c>base_url</c> at <c>http://127.0.0.1:{Port}/openai/v1</c> and authenticates to the
/// proxy with the per-run token in <c>PKS_CODEX_TOKEN</c>.
/// </summary>
public sealed class FoundryResponsesPassthrough
{
    private readonly FoundryStoredCredentials _creds;
    private readonly IAzureFoundryAuthService _authService;
    private readonly string _foundryScope;
    private readonly string _proxyToken;
    private readonly string _upstreamUrl;
    private static readonly string PksDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli");
    private static readonly string FailureLogPath = Path.Combine(PksDir, "codex-passthrough-failures.log");
    private WebApplication? _app;

    public int Port { get; }

    public FoundryResponsesPassthrough(
        FoundryStoredCredentials creds,
        IAzureFoundryAuthService authService,
        string foundryScope,
        string proxyToken,
        int port)
    {
        _creds = creds;
        _authService = authService;
        _foundryScope = foundryScope;
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

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
        var requestBytes = FilterFoundryIncompatibleAdditionalTools(ms.ToArray(), out var filterSummary);
        var requestSummary = BuildRequestSummary(requestBytes);
        if (filterSummary is not null)
        {
            await WriteLocalFailureAsync("request.filtered", requestSummary, filterSummary, ctx.RequestAborted);
        }

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, _upstreamUrl)
        {
            Content = new ByteArrayContent(requestBytes),
        };
        upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        try
        {
            await FoundryResponsesEndpoint.ApplyUpstreamAuthAsync(
                upstreamReq, _creds, _authService, _foundryScope, ctx.RequestAborted, forceBearer: true);
        }
        catch (Exception ex)
        {
            await WriteLocalFailureAsync("auth", requestSummary, ex.ToString(), ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync(
                "Could not obtain Foundry access token. Run `pks foundry init` or `pks foundry select` and retry.",
                ctx.RequestAborted);
            return;
        }

        var client = factory.CreateClient("codex-passthrough");
        using var upstream = await client.SendAsync(
            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);

        if (!upstream.IsSuccessStatusCode)
        {
            await WriteLocalFailureAsync(
                "http",
                requestSummary,
                $"HTTP {(int)upstream.StatusCode}: {await upstream.Content.ReadAsStringAsync(ctx.RequestAborted)}",
                ctx.RequestAborted);
            await AnthropicProxyUtil.RelayUpstreamErrorAsync(ctx, upstream);
            return;
        }

        ctx.Response.StatusCode = (int)upstream.StatusCode;
        var contentType = upstream.Content.Headers.ContentType?.ToString();
        if (!string.IsNullOrEmpty(contentType)) ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-cache";

        if (IsEventStream(contentType))
        {
            await RelaySseWithFailureLoggingAsync(ctx, upstream, requestSummary, ctx.RequestAborted);
            return;
        }

        // Raw byte copy with per-chunk flush so non-SSE responses stream incrementally back to codex.
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        var buffer = new byte[8192];
        int read;
        while ((read = await upstreamStream.ReadAsync(buffer, ctx.RequestAborted)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }

    internal static byte[] FilterFoundryIncompatibleAdditionalTools(byte[] requestBytes, out string? summary)
    {
        summary = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(requestBytes) as JsonObject;
        }
        catch (JsonException)
        {
            return requestBytes;
        }

        if (root?["input"] is not JsonArray input)
        {
            return requestBytes;
        }

        var removed = 0;
        foreach (var item in input.OfType<JsonObject>())
        {
            var type = item["type"]?.GetValue<string>();
            if (!string.Equals(type, "additional_tools", StringComparison.OrdinalIgnoreCase)
                || item["tools"] is not JsonArray tools)
            {
                continue;
            }

            for (var i = tools.Count - 1; i >= 0; i--)
            {
                if (tools[i] is not JsonObject tool || !IsReservedCollaborationTool(tool))
                {
                    continue;
                }

                tools.RemoveAt(i);
                removed++;
            }
        }

        if (removed == 0)
        {
            return requestBytes;
        }

        summary = $"Removed {removed} `collaboration` additional_tools entr{(removed == 1 ? "y" : "ies")} for Azure AI Foundry compatibility.";
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static bool IsReservedCollaborationTool(JsonObject tool)
    {
        var name = tool["name"]?.GetValue<string>();
        if (string.Equals(name, "collaboration", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ns = tool["namespace"]?.GetValue<string>();
        return string.Equals(ns, "collaboration", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEventStream(string? contentType)
    {
        return !string.IsNullOrEmpty(contentType)
               && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RelaySseWithFailureLoggingAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        string requestSummary,
        CancellationToken ct)
    {
        await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
        var data = new StringBuilder();
        string? eventName = null;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            await ctx.Response.WriteAsync(line, ct);
            await ctx.Response.WriteAsync("\n", ct);

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    await LogResponseFailedIfPresentAsync(eventName, data.ToString(), requestSummary, ct);
                    data.Clear();
                }

                eventName = null;
                await ctx.Response.Body.FlushAsync(ct);
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line.Length > 6 && line[6] == ' ' ? line[7..] : line[6..];
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var chunk = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (data.Length > 0) data.Append('\n');
                data.Append(chunk);
            }
        }

        if (data.Length > 0)
        {
            await LogResponseFailedIfPresentAsync(eventName, data.ToString(), requestSummary, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    private static async Task LogResponseFailedIfPresentAsync(
        string? eventName,
        string payload,
        string requestSummary,
        CancellationToken ct)
    {
        if (payload == "[DONE]") return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var payloadType = root.TryGetProperty("type", out var type) ? type.GetString() : null;
            var isFailure =
                string.Equals(eventName, "response.failed", StringComparison.Ordinal)
                || string.Equals(payloadType, "response.failed", StringComparison.Ordinal);
            if (!isFailure)
            {
                return;
            }

            var summary = BuildFailureSummary(root);
            await WriteLocalFailureAsync("response.failed", requestSummary, summary, ct);
        }
        catch (JsonException)
        {
            // Invalid partial/debug SSE payloads are relayed unchanged; they just are not diagnosable here.
        }
    }

    private static string BuildFailureSummary(JsonElement root)
    {
        try
        {
            var response = root.TryGetProperty("response", out var responseProp) ? responseProp : default;
            var id = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("id", out var idProp)
                ? idProp.GetString()
                : null;
            var status = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;
            var error = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out var errorProp)
                ? errorProp.ToString()
                : null;
            var incomplete = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("incomplete_details", out var incompleteProp)
                ? incompleteProp.ToString()
                : null;
            var usage = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("usage", out var usageProp)
                ? usageProp.ToString()
                : null;

            return new StringBuilder()
                .AppendLine($"id={id ?? "<none>"} status={status ?? "<none>"}")
                .AppendLine($"error={NullIfBlank(error) ?? "<null>"}")
                .AppendLine($"incomplete_details={NullIfBlank(incomplete) ?? "<null>"}")
                .AppendLine($"usage={NullIfBlank(usage) ?? "<null>"}")
                .AppendLine("payload_prefix=")
                .AppendLine(AnthropicProxyUtil.Truncate(root.ToString(), 2000))
                .ToString();
        }
        catch
        {
            return AnthropicProxyUtil.Truncate(root.ToString(), 4000);
        }
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "null" ? null : value;
    }

    private static string BuildRequestSummary(byte[] requestBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBytes);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
            var stream = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;
            var previousResponseId = root.TryGetProperty("previous_response_id", out var prevProp) ? prevProp.GetString() : null;
            var inputKind = root.TryGetProperty("input", out var inputProp) ? inputProp.ValueKind.ToString() : "missing";
            var truncation = root.TryGetProperty("truncation", out var truncationProp) ? truncationProp.GetString() : null;
            var tools = SummarizeTools(root);
            var markers = SummarizeRequestMarkers(root);
            return $"model={model ?? "<unset>"} stream={stream} truncation={truncation ?? "<unset>"} previous_response_id={(previousResponseId is null ? "<none>" : "<set>")} input_kind={inputKind} tools={tools} markers={markers} bytes={requestBytes.Length}";
        }
        catch (JsonException)
        {
            return $"invalid_json bytes={requestBytes.Length}";
        }
    }

    private static string SummarizeTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return "<none>";
        }

        var summaries = new List<string>();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                summaries.Add(tool.ValueKind.ToString());
                continue;
            }

            var type = tool.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var name = tool.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var ns = tool.TryGetProperty("namespace", out var nsProp) ? nsProp.GetString() : null;
            summaries.Add($"type={type ?? "<unset>"},namespace={ns ?? "<unset>"},name={name ?? "<unset>"}");
        }

        return summaries.Count == 0
            ? "[]"
            : AnthropicProxyUtil.Truncate(string.Join(" | ", summaries), 2000);
    }

    private static string SummarizeRequestMarkers(JsonElement root)
    {
        var markers = new List<string>();
        CollectRequestMarkers(root, "$", markers);
        return markers.Count == 0
            ? "<none>"
            : AnthropicProxyUtil.Truncate(string.Join(" | ", markers), 3000);
    }

    private static void CollectRequestMarkers(JsonElement element, string path, ICollection<string> markers)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}.{property.Name}";
                    if (property.Name.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("collaboration", StringComparison.OrdinalIgnoreCase))
                    {
                        markers.Add($"{propertyPath}={SummarizeMarkerValue(property.Value)}");
                    }

                    CollectRequestMarkers(property.Value, propertyPath, markers);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    CollectRequestMarkers(item, $"{path}[{index}]", markers);
                    index++;
                }
                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (value is not null &&
                    (value.Contains("tool", StringComparison.OrdinalIgnoreCase)
                     || value.Contains("collaboration", StringComparison.OrdinalIgnoreCase)))
                {
                    markers.Add($"{path}={AnthropicProxyUtil.Truncate(value, 240)}");
                }
                break;
        }
    }

    private static string SummarizeMarkerValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => AnthropicProxyUtil.Truncate(value.GetString() ?? "", 240),
            JsonValueKind.Array => $"Array({value.GetArrayLength()})",
            JsonValueKind.Object => AnthropicProxyUtil.Truncate(value.ToString(), 240),
            _ => value.ToString(),
        };
    }

    private static async Task WriteLocalFailureAsync(
        string kind,
        string requestSummary,
        string details,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(PksDir);
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.UtcNow:O}] {kind}")
                .AppendLine(requestSummary)
                .AppendLine(AnthropicProxyUtil.Truncate(details, 8000))
                .AppendLine()
                .ToString();
            await File.AppendAllTextAsync(FailureLogPath, entry, ct);
        }
        catch
        {
            // Diagnostics should never break the proxy response path.
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
