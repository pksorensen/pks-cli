using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace PKS.Infrastructure.Services.Agent.Anthropic;

/// <summary>
/// Provider-agnostic plumbing shared by the local <c>pks claude</c> translating proxies
/// (codex/Responses and Scaleway/Chat-Completions): SSE parsing, the loopback auth-token check,
/// free-port discovery, and upstream-error relaying. These are pure helpers with no provider
/// knowledge so both proxies can reuse them unchanged.
/// </summary>
public static class AnthropicProxyUtil
{
    /// <summary>Reads an SSE stream and yields the JSON object inside each <c>data:</c> payload.</summary>
    public static async IAsyncEnumerable<JsonElement> ReadSseEventsAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    if (payload != "[DONE]" && TryParse(payload, out var el))
                    {
                        yield return el;
                    }
                }
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var chunk = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (data.Length > 0) data.Append('\n');
                data.Append(chunk);
            }
            // ignore "event:" / ":" comment / id: lines — the type lives inside the JSON
        }

        // trailing event with no terminating blank line
        if (data.Length > 0)
        {
            var payload = data.ToString();
            if (payload != "[DONE]" && TryParse(payload, out var el)) yield return el;
        }
    }

    private static bool TryParse(string json, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    /// <summary>Validates the loopback proxy token Claude Code sends (x-api-key or Authorization: Bearer).</summary>
    public static bool ValidateToken(HttpContext ctx, string proxyToken)
    {
        var apiKey = ctx.Request.Headers["x-api-key"].FirstOrDefault();
        var auth = ctx.Request.Headers["Authorization"].FirstOrDefault();
        if (apiKey == proxyToken || auth == $"Bearer {proxyToken}")
        {
            return true;
        }
        ctx.Response.StatusCode = 401;
        return false;
    }

    /// <summary>Relays an upstream non-success response back to Claude Code as an Anthropic error body.</summary>
    public static async Task RelayUpstreamErrorAsync(HttpContext ctx, HttpResponseMessage upstream)
    {
        var body = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = "application/json";
        var error = new JsonObject
        {
            ["type"] = "error",
            ["error"] = new JsonObject
            {
                ["type"] = "api_error",
                ["message"] = $"Upstream {(int)upstream.StatusCode}: {Truncate(body, 2000)}",
            },
        };
        await ctx.Response.WriteAsync(error.ToJsonString(), ctx.RequestAborted);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Finds a free loopback TCP port for the in-process proxy.</summary>
    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
