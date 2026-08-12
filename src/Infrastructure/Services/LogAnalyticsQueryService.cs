using System.Text;
using System.Text.Json;
using System.Xml;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Thrown when the Log Analytics query API rejects a query. The message carries
/// the Kusto error text (including the syntax error position when there is one),
/// which is the whole point of running ad-hoc KQL from an agent.
/// </summary>
public class LogAnalyticsQueryException : Exception
{
    public LogAnalyticsQueryException(string message) : base(message) { }
}

// Adapter interface for testability (wraps HttpClient calls)
public interface ILogAnalyticsHttpAdapter
{
    Task<KustoQueryResponse> QueryAsync(
        string workspaceId,
        string bearerToken,
        string kql,
        string? timespan = null,
        CancellationToken ct = default);
}

internal class DefaultLogAnalyticsHttpAdapter : ILogAnalyticsHttpAdapter
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public DefaultLogAnalyticsHttpAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<KustoQueryResponse> QueryAsync(
        string workspaceId, string bearerToken, string kql, string? timespan = null, CancellationToken ct = default)
    {
        var url = $"https://api.loganalytics.io/v1/workspaces/{workspaceId}/query";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        var body = string.IsNullOrWhiteSpace(timespan)
            ? JsonSerializer.Serialize(new { query = kql })
            : JsonSerializer.Serialize(new { query = kql, timespan });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new LogAnalyticsQueryException(
                LogAnalyticsQueryService.FormatApiError((int)response.StatusCode, json));

        return JsonSerializer.Deserialize<KustoQueryResponse>(json, JsonOpts)
               ?? throw new LogAnalyticsQueryException("Empty response from the Log Analytics query API");
    }
}

public interface ILogAnalyticsQueryService
{
    Task<LogAnalyticsConnectionResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>
    /// Run raw KQL. <paramref name="since"/> maps to the API's <c>timespan</c>
    /// property, so it applies without rewriting the query; pass null to let the
    /// query decide its own time range.
    /// </summary>
    Task<KustoQueryResponse> QueryAsync(
        string kql,
        TimeSpan? since = null,
        string? workspaceIdOverride = null,
        CancellationToken ct = default);

    Task<string?> GetConfiguredWorkspaceIdAsync(CancellationToken ct = default);
}

public class LogAnalyticsQueryService : ILogAnalyticsQueryService
{
    private const string QueryScope = "https://api.loganalytics.io/.default";

    private readonly ILogAnalyticsConfigService _configService;
    private readonly ILogAnalyticsHttpAdapter _httpAdapter;
    private readonly IAzureFoundryAuthService _authService;

    public LogAnalyticsQueryService(
        ILogAnalyticsConfigService configService,
        ILogAnalyticsHttpAdapter httpAdapter,
        IAzureFoundryAuthService authService)
    {
        _configService = configService;
        _httpAdapter = httpAdapter;
        _authService = authService;
    }

    public async Task<LogAnalyticsConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            if (config is null)
                return new LogAnalyticsConnectionResult { Success = false, ErrorMessage = "Not configured" };

            var token = await _authService.GetAccessTokenAsync(QueryScope, ct);
            if (string.IsNullOrEmpty(token))
                return new LogAnalyticsConnectionResult { Success = false, ErrorMessage = "Not authenticated. Run 'pks loganalytics init' first." };

            await _httpAdapter.QueryAsync(config.WorkspaceId, token, "print ok = 1", null, ct);

            return new LogAnalyticsConnectionResult { Success = true, WorkspaceName = config.WorkspaceName };
        }
        catch (Exception ex)
        {
            return new LogAnalyticsConnectionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<KustoQueryResponse> QueryAsync(
        string kql, TimeSpan? since = null, string? workspaceIdOverride = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kql))
            throw new ArgumentException("Query must not be empty.", nameof(kql));

        var workspaceId = workspaceIdOverride;
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            var config = await _configService.GetConfigAsync()
                ?? throw new InvalidOperationException("Log Analytics not configured. Run 'pks loganalytics init' first.");
            workspaceId = config.WorkspaceId;
        }

        var token = await _authService.GetAccessTokenAsync(QueryScope, ct)
            ?? throw new InvalidOperationException("Not authenticated. Run 'pks loganalytics init' to sign in.");

        return await _httpAdapter.QueryAsync(workspaceId, token, kql, FormatTimespan(since), ct);
    }

    public async Task<string?> GetConfiguredWorkspaceIdAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        return config?.WorkspaceId;
    }

    /// <summary>ISO 8601 duration for the API's <c>timespan</c> property (1h → PT1H).</summary>
    internal static string? FormatTimespan(TimeSpan? since)
        => since is null || since.Value <= TimeSpan.Zero ? null : XmlConvert.ToString(since.Value);

    /// <summary>
    /// Turn an error response body into the most specific message it contains.
    /// The query API nests the real Kusto diagnostic (syntax error, position,
    /// semantic error) inside <c>error.innererror[.innererror…]</c>, so the outer
    /// "The request had some invalid properties" alone is useless.
    /// </summary>
    internal static string FormatApiError(int statusCode, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"Log Analytics query failed (HTTP {statusCode}).";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return $"Log Analytics query failed (HTTP {statusCode}): {Truncate(body)}";

            var parts = new List<string>();
            var current = error;
            while (true)
            {
                var code = current.TryGetProperty("code", out var c) ? c.GetString() : null;
                var message = current.TryGetProperty("message", out var m) ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(message))
                    parts.Add(string.IsNullOrWhiteSpace(code) ? message! : $"{code}: {message}");

                if (!current.TryGetProperty("innererror", out var inner) || inner.ValueKind != JsonValueKind.Object)
                    break;
                current = inner;
            }

            return parts.Count == 0
                ? $"Log Analytics query failed (HTTP {statusCode}): {Truncate(body)}"
                : $"Log Analytics query failed (HTTP {statusCode}): {string.Join(" → ", parts)}";
        }
        catch (JsonException)
        {
            return $"Log Analytics query failed (HTTP {statusCode}): {Truncate(body)}";
        }
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "…";
}
