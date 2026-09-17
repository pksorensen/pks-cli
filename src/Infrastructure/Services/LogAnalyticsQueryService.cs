using System.Text;
using System.Text.Json;
using System.Xml;
using PKS.Infrastructure.Services.Azure;
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
    /// <summary>Probe the first enabled workspace (what <c>pks loganalytics status</c> shows).</summary>
    Task<LogAnalyticsConnectionResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Probe one registered workspace with its own tenant's token.</summary>
    Task<LogAnalyticsConnectionResult> TestConnectionAsync(AzureResourceEntry workspace, CancellationToken ct = default);

    /// <summary>
    /// Run raw KQL against one workspace. <paramref name="since"/> maps to the API's <c>timespan</c>
    /// property, so it applies without rewriting the query; pass null to let the
    /// query decide its own time range.
    /// </summary>
    Task<KustoQueryResponse> QueryAsync(
        string kql,
        TimeSpan? since = null,
        string? workspaceIdOverride = null,
        CancellationToken ct = default);

    /// <summary>
    /// Run the same KQL against several workspaces in parallel, each with a token for its own
    /// tenant. One result per workspace, in input order; a failure is captured in that
    /// workspace's result and never stops the others.
    /// </summary>
    Task<IReadOnlyList<WorkspaceQueryResult>> QueryManyAsync(
        IReadOnlyList<AzureResourceEntry> workspaces,
        string kql,
        TimeSpan? since = null,
        CancellationToken ct = default);

    Task<string?> GetConfiguredWorkspaceIdAsync(CancellationToken ct = default);
}

public class LogAnalyticsQueryService : ILogAnalyticsQueryService
{
    private const string QueryScope = "https://api.loganalytics.io/.default";
    private const string InitCommand = "pks loganalytics init";

    private readonly ILogAnalyticsConfigService _configService;
    private readonly ILogAnalyticsHttpAdapter _httpAdapter;
    private readonly IAzureResourceRegistry _registry;
    private readonly AzureQueryTokenProvider _tokens;

    public LogAnalyticsQueryService(
        ILogAnalyticsConfigService configService,
        ILogAnalyticsHttpAdapter httpAdapter,
        IAzureFoundryAuthService authService,
        IAzureTenantCredentialStore tenantStore,
        IAzureResourceRegistry registry)
    {
        _configService = configService;
        _httpAdapter = httpAdapter;
        _registry = registry;
        _tokens = new AzureQueryTokenProvider(tenantStore, authService, InitCommand);
    }

    public async Task<LogAnalyticsConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        if (config is null)
            return new LogAnalyticsConnectionResult { Success = false, ErrorMessage = "Not configured" };

        return await TestConnectionAsync(await ResolveEntryAsync(config.WorkspaceId, config.WorkspaceName), ct);
    }

    public async Task<LogAnalyticsConnectionResult> TestConnectionAsync(AzureResourceEntry workspace, CancellationToken ct = default)
    {
        try
        {
            var token = await _tokens.GetTokenAsync(workspace, QueryScope, ct);
            await _httpAdapter.QueryAsync(workspace.Key, token, "print ok = 1", null, ct);
            return new LogAnalyticsConnectionResult { Success = true, WorkspaceName = workspace.Name };
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

        AzureResourceEntry workspace;
        if (!string.IsNullOrWhiteSpace(workspaceIdOverride))
        {
            workspace = await ResolveEntryAsync(workspaceIdOverride, null);
        }
        else
        {
            var config = await _configService.GetConfigAsync()
                ?? throw new InvalidOperationException($"Log Analytics not configured. Run '{InitCommand}' first.");
            workspace = await ResolveEntryAsync(config.WorkspaceId, config.WorkspaceName);
        }

        return await QueryOneAsync(workspace, kql, FormatTimespan(since), ct);
    }

    public async Task<IReadOnlyList<WorkspaceQueryResult>> QueryManyAsync(
        IReadOnlyList<AzureResourceEntry> workspaces, string kql, TimeSpan? since = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kql))
            throw new ArgumentException("Query must not be empty.", nameof(kql));
        if (workspaces.Count == 0)
            return Array.Empty<WorkspaceQueryResult>();

        var timespan = FormatTimespan(since);
        return await Task.WhenAll(workspaces.Select(async workspace =>
        {
            try
            {
                var response = await QueryOneAsync(workspace, kql, timespan, ct);
                return new WorkspaceQueryResult { Workspace = workspace, Response = response };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new WorkspaceQueryResult { Workspace = workspace, Error = ex };
            }
        }));
    }

    public async Task<string?> GetConfiguredWorkspaceIdAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        return config?.WorkspaceId;
    }

    private async Task<KustoQueryResponse> QueryOneAsync(AzureResourceEntry workspace, string kql, string? timespan, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(workspace, QueryScope, ct);
        return await _httpAdapter.QueryAsync(workspace.Key, token, kql, timespan, ct);
    }

    /// <summary>
    /// The registry entry for a workspace GUID — it carries the tenant the token must come from.
    /// A GUID the registry does not know (an ad-hoc <c>--workspace</c>) becomes a tenant-less
    /// entry, which the token provider resolves to the only signed-in tenant.
    /// </summary>
    private async Task<AzureResourceEntry> ResolveEntryAsync(string workspaceId, string? name)
        => await _registry.FindAsync(AzureResourceKind.LogAnalytics, workspaceId)
           ?? new AzureResourceEntry
           {
               Kind = AzureResourceKind.LogAnalytics,
               Key = workspaceId,
               Name = name ?? workspaceId,
               Enabled = true
           };

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
