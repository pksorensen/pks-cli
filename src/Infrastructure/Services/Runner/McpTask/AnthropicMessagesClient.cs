using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services.Runner.McpTask;

/// <summary>Thrown for a non-2xx Messages API response. The message carries only the status code
/// and the API's error <c>type</c> -- never the request body (which holds tool results).</summary>
public sealed class AnthropicApiException(int statusCode, string? errorType)
    : Exception($"Anthropic API returned {statusCode}{(errorType != null ? $" ({errorType})" : "")}")
{
    public int StatusCode { get; } = statusCode;
    public string? ErrorType { get; } = errorType;
    public bool IsAuth => StatusCode is 401 or 403;
}

/// <summary>
/// Minimal <c>POST /v1/messages</c> client over a caller-supplied <see cref="HttpClient"/> (so tests
/// inject a handler; no network). Plain HTTP rather than an SDK: the project already references the
/// unofficial <c>Anthropic.SDK</c> package, whose <c>Anthropic</c> namespace would collide with the
/// official SDK's, and this loop needs one endpoint.
/// </summary>
public sealed class AnthropicMessagesClient
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public AnthropicMessagesClient(HttpClient http, string apiKey, string? baseUrl = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        // Tolerate a base URL that already ends in /v1 (some gateways are configured that way).
        if (_baseUrl.EndsWith("/v1", StringComparison.Ordinal)) _baseUrl = _baseUrl[..^3];
    }

    public async Task<JsonObject> CreateMessageAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string? errType = null;
            try { errType = JsonNode.Parse(text)?["error"]?["type"]?.GetValue<string>(); } catch { /* non-JSON error body */ }
            throw new AnthropicApiException((int)resp.StatusCode, errType);
        }

        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException("Anthropic API returned a non-object body");
    }
}
