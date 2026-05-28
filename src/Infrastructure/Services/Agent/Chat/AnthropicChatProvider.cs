using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace PKS.Infrastructure.Services.Agent.Chat;

/// <summary>
/// Anthropic Messages API provider. Hand-rolled HTTP + SSE so the streaming surface
/// stays stable across Anthropic.SDK versions.
///
/// Supports two auth modes:
/// - <c>x-api-key</c> against Anthropic's public API (api.anthropic.com)
/// - <c>Authorization: Bearer</c> against Microsoft Foundry's `/anthropic/v1/messages` endpoint
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    private const string AnthropicVersion = "2023-06-01";
    /// <summary>Default Entra ID scope for Foundry-served Claude models.</summary>
    public const string FoundryScope = "https://ai.azure.com/.default";

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private readonly Func<CancellationToken, ValueTask<(string HeaderName, string HeaderValue)>> _getAuthHeader;

    public AnthropicChatProvider(Uri endpoint, string apiKey, HttpClient httpClient)
    {
        _endpoint = endpoint ?? new Uri("https://api.anthropic.com");
        if (string.IsNullOrEmpty(apiKey)) throw new ArgumentNullException(nameof(apiKey));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _getAuthHeader = _ => new ValueTask<(string, string)>(("x-api-key", apiKey));
    }

    public AnthropicChatProvider(string apiKey, HttpClient httpClient)
        : this(new Uri("https://api.anthropic.com"), apiKey, httpClient)
    {
    }

    /// <summary>
    /// Construct against a Foundry-style endpoint with Microsoft Entra ID auth.
    /// <paramref name="endpoint"/> should be the base URL including the <c>/anthropic</c> segment
    /// (e.g. <c>https://contextand-cs-foundry.services.ai.azure.com/anthropic</c>); the provider
    /// appends <c>/v1/messages</c>.
    /// </summary>
    public AnthropicChatProvider(Uri endpoint, TokenCredential credential, HttpClient httpClient, string scope = FoundryScope)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (credential is null) throw new ArgumentNullException(nameof(credential));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _getAuthHeader = async ct =>
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct).ConfigureAwait(false);
            return ("Authorization", "Bearer " + token.Token);
        };
    }

    public string ProviderId => "anthropic";

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        string modelId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var body = BuildRequestBody(request, modelId);

        // Preserve any path on _endpoint (e.g. /anthropic) by combining as strings.
        var baseStr = _endpoint.AbsoluteUri.TrimEnd('/');
        var url = new Uri(baseStr + "/v1/messages");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var (authName, authValue) = await _getAuthHeader(cancellationToken).ConfigureAwait(false);
        httpRequest.Headers.TryAddWithoutValidation(authName, authValue);
        httpRequest.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var blockIdByIndex = new Dictionary<int, string>();
        string? stopReason = null;
        ChatUsage? usage = null;
        string? currentEvent = null;
        var dataBuf = new StringBuilder();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (currentEvent is not null && dataBuf.Length > 0)
                {
                    var evt = ParseSseEvent(currentEvent, dataBuf.ToString(), blockIdByIndex, ref stopReason, ref usage);
                    if (evt is not null)
                    {
                        yield return evt;
                        if (evt is MessageStopEvent) yield break;
                    }
                }
                currentEvent = null;
                dataBuf.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (dataBuf.Length > 0) dataBuf.Append('\n');
                dataBuf.Append(line.Substring(5).TrimStart());
            }
        }

        // Flush trailing event if no terminal blank line.
        if (currentEvent is not null && dataBuf.Length > 0)
        {
            var evt = ParseSseEvent(currentEvent, dataBuf.ToString(), blockIdByIndex, ref stopReason, ref usage);
            if (evt is not null) yield return evt;
        }
    }

    internal static string BuildRequestBody(ChatRequest request, string modelId)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", modelId);
            w.WriteNumber("max_tokens", request.MaxOutputTokens);

            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                w.WriteString("system", request.SystemPrompt);
            }

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (var msg in request.Messages)
            {
                WriteMessage(w, msg);
            }
            w.WriteEndArray();

            if (request.Tools is { Count: > 0 })
            {
                w.WritePropertyName("tools");
                w.WriteStartArray();
                foreach (var t in request.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("input_schema");
                    t.InputSchema.WriteTo(w);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            w.WriteBoolean("stream", true);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteMessage(Utf8JsonWriter w, ChatMessage msg)
    {
        switch (msg.Role)
        {
            case ChatRole.System:
                // System prompt is top-level; skip any system-role messages.
                return;

            case ChatRole.User:
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WritePropertyName("content");
                w.WriteStartArray();
                foreach (var block in msg.Content)
                {
                    if (block is TextBlock tb)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "text");
                        w.WriteString("text", tb.Text);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
                return;

            case ChatRole.Assistant:
                w.WriteStartObject();
                w.WriteString("role", "assistant");
                w.WritePropertyName("content");
                w.WriteStartArray();
                foreach (var block in msg.Content)
                {
                    switch (block)
                    {
                        case TextBlock tb:
                            w.WriteStartObject();
                            w.WriteString("type", "text");
                            w.WriteString("text", tb.Text);
                            w.WriteEndObject();
                            break;
                        case ToolUseBlock tu:
                            w.WriteStartObject();
                            w.WriteString("type", "tool_use");
                            w.WriteString("id", tu.Id);
                            w.WriteString("name", tu.Name);
                            w.WritePropertyName("input");
                            tu.Arguments.WriteTo(w);
                            w.WriteEndObject();
                            break;
                        case ThinkingBlock th:
                            w.WriteStartObject();
                            w.WriteString("type", "thinking");
                            w.WriteString("thinking", th.Text);
                            w.WriteEndObject();
                            break;
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
                return;

            case ChatRole.Tool:
                // Tool results are emitted as a user-role message with tool_result blocks.
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WritePropertyName("content");
                w.WriteStartArray();
                foreach (var block in msg.Content)
                {
                    if (block is ToolResultBlock tr)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "tool_result");
                        w.WriteString("tool_use_id", tr.ToolUseId);
                        w.WriteString("content", tr.Content);
                        w.WriteBoolean("is_error", tr.IsError);
                        w.WriteEndObject();
                    }
                }
                w.WriteEndArray();
                w.WriteEndObject();
                return;
        }
    }

    internal static ChatStreamEvent? ParseSseEvent(
        string eventName,
        string dataJson,
        Dictionary<int, string> blockIdByIndex,
        ref string? stopReason,
        ref ChatUsage? usage)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var root = doc.RootElement;

        switch (eventName)
        {
            case "content_block_start":
            {
                if (!root.TryGetProperty("content_block", out var cb)) return null;
                var index = root.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var idx) ? idx : -1;
                var type = cb.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (type == "tool_use")
                {
                    var id = cb.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = cb.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                    if (index >= 0) blockIdByIndex[index] = id;
                    return new ToolUseStartEvent(id, name);
                }
                return null;
            }

            case "content_block_delta":
            {
                if (!root.TryGetProperty("delta", out var delta)) return null;
                var deltaType = delta.TryGetProperty("type", out var dtEl) ? dtEl.GetString() : null;
                switch (deltaType)
                {
                    case "text_delta":
                        return new TextDeltaEvent(delta.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "");
                    case "input_json_delta":
                    {
                        var idx = root.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var i) ? i : -1;
                        var id = idx >= 0 && blockIdByIndex.TryGetValue(idx, out var resolved) ? resolved : "";
                        var partial = delta.TryGetProperty("partial_json", out var pj) ? pj.GetString() ?? "" : "";
                        return new ToolUseDeltaEvent(id, partial);
                    }
                    case "thinking_delta":
                        return new ThinkingDeltaEvent(delta.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "");
                }
                return null;
            }

            case "message_delta":
            {
                if (root.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("stop_reason", out var srEl) &&
                    srEl.ValueKind == JsonValueKind.String)
                {
                    stopReason = srEl.GetString();
                }
                if (root.TryGetProperty("usage", out var usageEl))
                {
                    var inTok = usageEl.TryGetProperty("input_tokens", out var itEl) && itEl.TryGetInt32(out var it) ? it : 0;
                    var outTok = usageEl.TryGetProperty("output_tokens", out var otEl) && otEl.TryGetInt32(out var ot) ? ot : 0;
                    usage = new ChatUsage(inTok, outTok);
                }
                return null;
            }

            case "message_stop":
                return new MessageStopEvent(MapStopReason(stopReason), usage);

            default:
                return null;
        }
    }

    internal static ChatFinishReason MapStopReason(string? reason) => reason switch
    {
        "end_turn" => ChatFinishReason.Stop,
        "tool_use" => ChatFinishReason.ToolCalls,
        "max_tokens" => ChatFinishReason.MaxTokens,
        "stop_sequence" => ChatFinishReason.Stop,
        _ => ChatFinishReason.Stop,
    };
}
