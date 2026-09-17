using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

// DTO for Application Insights REST API responses
public class AppInsightsQueryResponse
{
    [JsonPropertyName("tables")]
    public List<AppInsightsTable> Tables { get; set; } = new();
}

public class AppInsightsTable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<AppInsightsColumn> Columns { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<List<JsonElement>> Rows { get; set; } = new();
}

public class AppInsightsColumn
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

// Adapter interface for testability (wraps HttpClient calls)
public interface IAppInsightsHttpAdapter
{
    Task<AppInsightsQueryResponse> QueryAsync(
        string appId,
        string bearerToken,
        string kql,
        CancellationToken ct = default);
}

internal class DefaultAppInsightsHttpAdapter : IAppInsightsHttpAdapter
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public DefaultAppInsightsHttpAdapter(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AppInsightsQueryResponse> QueryAsync(
        string appId, string bearerToken, string kql, CancellationToken ct = default)
    {
        var url = $"https://api.applicationinsights.io/v1/apps/{appId}/query";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query = kql }),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AppInsightsQueryResponse>(json, JsonOpts)
               ?? throw new InvalidOperationException("Empty response from Application Insights API");
    }
}

public interface IAppInsightsQueryService
{
    /// <summary>Probe the first enabled resource (what <c>pks appinsights status</c> shows).</summary>
    Task<AppInsightsConnectionResult> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Probe one registered resource with its own tenant's token.</summary>
    Task<AppInsightsConnectionResult> TestConnectionAsync(AzureResourceEntry resource, CancellationToken ct = default);

    // Single-resource queries. <c>appIdOverride</c> targets that App Insights application id
    // instead of the first enabled one; the returned rows carry the resource's registry name.
    Task<List<OtelError>> QueryErrorsAsync(TimeSpan since, int limit, string? appName = null, string? operationId = null, string? appIdOverride = null, CancellationToken ct = default);
    Task<List<OtelTrace>> QueryTracesAsync(TimeSpan since, int limit, bool? hasError = null, string? appName = null, string? appIdOverride = null, CancellationToken ct = default);
    Task<List<OtelLog>> QueryLogsAsync(TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, string? appIdOverride = null, CancellationToken ct = default);
    Task<List<OtelSpan>> QuerySpansAsync(string operationId, string? appIdOverride = null, CancellationToken ct = default);

    // Fan-out over several resources in parallel, each with a token for its own tenant. Rows are
    // merged in the same order the single-resource query uses (newest first; spans oldest first),
    // every row stamped with its resource's registry name. A resource that fails is reported in
    // <see cref="OtelQueryResult{T}.Errors"/> and never stops the others.
    Task<OtelQueryResult<OtelError>> QueryErrorsManyAsync(IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, int limit, string? appName = null, string? operationId = null, CancellationToken ct = default);
    Task<OtelQueryResult<OtelTrace>> QueryTracesManyAsync(IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, int limit, bool? hasError = null, string? appName = null, CancellationToken ct = default);
    Task<OtelQueryResult<OtelLog>> QueryLogsManyAsync(IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, CancellationToken ct = default);
    Task<OtelQueryResult<OtelSpan>> QuerySpansManyAsync(IReadOnlyList<AzureResourceEntry> resources, string operationId, CancellationToken ct = default);

    Task<string?> GetConfiguredAppIdAsync(CancellationToken ct = default);
}

public class AppInsightsQueryService : IAppInsightsQueryService
{
    private static readonly Dictionary<string, int> SeverityMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["trace"] = 0,
        ["info"] = 1,
        ["information"] = 1,
        ["warning"] = 2,
        ["error"] = 3,
        ["critical"] = 4
    };

    private const string QueryScope = "https://api.applicationinsights.io/.default";
    private const string InitCommand = "pks appinsights init";

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsHttpAdapter _httpAdapter;
    private readonly IAzureResourceRegistry _registry;
    private readonly AzureQueryTokenProvider _tokens;

    public AppInsightsQueryService(
        IAppInsightsConfigService configService,
        IAppInsightsHttpAdapter httpAdapter,
        IAzureFoundryAuthService authService,
        IAzureTenantCredentialStore tenantStore,
        IAzureResourceRegistry registry)
    {
        _configService = configService;
        _httpAdapter = httpAdapter;
        _registry = registry;
        _tokens = new AzureQueryTokenProvider(tenantStore, authService, InitCommand);
    }

    public async Task<AppInsightsConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        if (config is null)
            return new AppInsightsConnectionResult { Success = false, ErrorMessage = "Not configured" };

        return await TestConnectionAsync(await ResolveEntryAsync(config.AppId, config.ResourceName), ct);
    }

    public async Task<AppInsightsConnectionResult> TestConnectionAsync(AzureResourceEntry resource, CancellationToken ct = default)
    {
        try
        {
            var token = await _tokens.GetTokenAsync(resource, QueryScope, ct);
            var kql = "requests | take 1 | project cloud_RoleName";
            var response = await _httpAdapter.QueryAsync(resource.Key, token, kql, ct);

            var resourceName = response.Tables.FirstOrDefault()?.Rows.FirstOrDefault()
                ?.ElementAtOrDefault(0).GetString();

            return new AppInsightsConnectionResult { Success = true, ResourceName = resourceName ?? resource.Name };
        }
        catch (Exception ex)
        {
            return new AppInsightsConnectionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<List<OtelError>> QueryErrorsAsync(
        TimeSpan since, int limit, string? appName = null, string? operationId = null, string? appIdOverride = null, CancellationToken ct = default)
        => await QueryOneAsync(await RequireEntryAsync(appIdOverride), BuildErrorsKql(since, limit, appName, operationId), MapErrors, ct);

    public async Task<List<OtelTrace>> QueryTracesAsync(
        TimeSpan since, int limit, bool? hasError = null, string? appName = null, string? appIdOverride = null, CancellationToken ct = default)
        => await QueryOneAsync(await RequireEntryAsync(appIdOverride), BuildTracesKql(since, limit, hasError, appName), MapTraces, ct);

    public async Task<List<OtelLog>> QueryLogsAsync(
        TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, string? appIdOverride = null, CancellationToken ct = default)
        => await QueryOneAsync(await RequireEntryAsync(appIdOverride), BuildLogsKql(since, severity, traceId, appName), MapLogs, ct);

    public async Task<List<OtelSpan>> QuerySpansAsync(string operationId, string? appIdOverride = null, CancellationToken ct = default)
        => await QueryOneAsync(await RequireEntryAsync(appIdOverride), BuildSpansKql(operationId), MapSpans, ct);

    public Task<OtelQueryResult<OtelError>> QueryErrorsManyAsync(
        IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, int limit, string? appName = null, string? operationId = null, CancellationToken ct = default)
        => QueryManyAsync(resources, BuildErrorsKql(since, limit, appName, operationId), MapErrors, newestFirst: true, ct);

    public Task<OtelQueryResult<OtelTrace>> QueryTracesManyAsync(
        IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, int limit, bool? hasError = null, string? appName = null, CancellationToken ct = default)
        => QueryManyAsync(resources, BuildTracesKql(since, limit, hasError, appName), MapTraces, newestFirst: true, ct);

    public Task<OtelQueryResult<OtelLog>> QueryLogsManyAsync(
        IReadOnlyList<AzureResourceEntry> resources, TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, CancellationToken ct = default)
        => QueryManyAsync(resources, BuildLogsKql(since, severity, traceId, appName), MapLogs, newestFirst: true, ct);

    public Task<OtelQueryResult<OtelSpan>> QuerySpansManyAsync(
        IReadOnlyList<AzureResourceEntry> resources, string operationId, CancellationToken ct = default)
        => QueryManyAsync(resources, BuildSpansKql(operationId), MapSpans, newestFirst: false, ct);

    public async Task<string?> GetConfiguredAppIdAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        return config?.AppId;
    }

    /// <summary>One resource's rows, each stamped with the resource's registry name.</summary>
    private async Task<List<T>> QueryOneAsync<T>(
        AzureResourceEntry resource, string kql, Func<AppInsightsQueryResponse, List<T>> map, CancellationToken ct)
        where T : IOtelRecord
    {
        var token = await _tokens.GetTokenAsync(resource, QueryScope, ct);
        var rows = map(await _httpAdapter.QueryAsync(resource.Key, token, kql, ct));
        foreach (var row in rows)
            row.Resource = resource.Name;
        return rows;
    }

    /// <summary>
    /// The same query against every resource at once. Each resource's failure lands in
    /// <see cref="OtelQueryResult{T}.Errors"/> (in input order) instead of aborting the batch,
    /// and the surviving rows are merged into one timestamp-ordered list.
    /// </summary>
    private async Task<OtelQueryResult<T>> QueryManyAsync<T>(
        IReadOnlyList<AzureResourceEntry> resources, string kql, Func<AppInsightsQueryResponse, List<T>> map,
        bool newestFirst, CancellationToken ct)
        where T : IOtelRecord
    {
        var outcomes = await Task.WhenAll(resources.Select(async resource =>
        {
            try
            {
                return (rows: await QueryOneAsync(resource, kql, map, ct), error: (OtelResourceError?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (rows: new List<T>(), error: new OtelResourceError { Resource = resource, Error = ex });
            }
        }));

        var items = outcomes.SelectMany(o => o.rows);
        return new OtelQueryResult<T>
        {
            Attempted = resources.Count,
            Items = (newestFirst ? items.OrderByDescending(r => r.Timestamp) : items.OrderBy(r => r.Timestamp)).ToList(),
            Errors = outcomes.Where(o => o.error is not null).Select(o => o.error!).ToList()
        };
    }

    private async Task<AzureResourceEntry> RequireEntryAsync(string? appIdOverride)
    {
        if (!string.IsNullOrWhiteSpace(appIdOverride))
            return await ResolveEntryAsync(appIdOverride, null);

        var config = await _configService.GetConfigAsync()
            ?? throw new InvalidOperationException($"Application Insights not configured. Run '{InitCommand}' first.");
        return await ResolveEntryAsync(config.AppId, config.ResourceName);
    }

    /// <summary>
    /// The registry entry for an application id — it carries the tenant the token must come from.
    /// An id the registry does not know becomes a tenant-less entry, which the token provider
    /// resolves to the only signed-in tenant.
    /// </summary>
    private async Task<AzureResourceEntry> ResolveEntryAsync(string appId, string? name)
        => await _registry.FindAsync(AzureResourceKind.AppInsights, appId)
           ?? new AzureResourceEntry
           {
               Kind = AzureResourceKind.AppInsights,
               Key = appId,
               Name = name ?? appId,
               Enabled = true
           };

    private static string FormatSince(TimeSpan since)
    {
        if (since.TotalDays >= 1) return $"{(int)since.TotalDays}d";
        if (since.TotalHours >= 1) return $"{(int)since.TotalHours}h";
        return $"{(int)since.TotalMinutes}m";
    }

    internal static string BuildErrorsKql(TimeSpan since, int limit, string? appName, string? operationId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("exceptions");
        sb.AppendLine($"| where timestamp > ago({FormatSince(since)})");
        if (!string.IsNullOrWhiteSpace(appName))
            sb.AppendLine($"| where cloud_RoleName == \"{appName}\"");
        if (!string.IsNullOrWhiteSpace(operationId))
            sb.AppendLine($"| where operation_Id == \"{operationId}\"");
        sb.AppendLine("| order by timestamp desc");
        sb.AppendLine($"| take {limit}");
        sb.AppendLine("| project timestamp, type, outerMessage, innermostMessage, operation_Id, cloud_RoleName");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildTracesKql(TimeSpan since, int limit, bool? hasError, string? appName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("requests");
        sb.AppendLine($"| where timestamp > ago({FormatSince(since)})");
        if (!string.IsNullOrWhiteSpace(appName))
            sb.AppendLine($"| where cloud_RoleName == \"{appName}\"");
        if (hasError == true)
            sb.AppendLine("| where success == false");
        sb.AppendLine("| order by timestamp desc");
        sb.AppendLine($"| take {limit}");
        sb.AppendLine("| project timestamp, operation_Id, name, cloud_RoleName, duration, success, resultCode");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildLogsKql(TimeSpan since, string? severity, string? traceId, string? appName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("traces");
        sb.AppendLine($"| where timestamp > ago({FormatSince(since)})");
        if (!string.IsNullOrWhiteSpace(appName))
            sb.AppendLine($"| where cloud_RoleName == \"{appName}\"");
        if (!string.IsNullOrWhiteSpace(severity) && SeverityMap.TryGetValue(severity, out var sevInt))
            sb.AppendLine($"| where severityLevel >= {sevInt}");
        if (!string.IsNullOrWhiteSpace(traceId))
            sb.AppendLine($"| where operation_Id == \"{traceId}\"");
        sb.AppendLine("| order by timestamp desc");
        sb.AppendLine("| project timestamp, severityLevel, message, operation_Id, cloud_RoleName");
        return sb.ToString().TrimEnd();
    }

    private static string BuildSpansKql(string operationId)
    {
        return $"""
                dependencies
                | where timestamp > ago(24h)
                | where operation_Id == "{operationId}"
                | order by timestamp asc
                | project timestamp, id, target, type, name, duration, success
                """;
    }

    private static List<OtelError> MapErrors(AppInsightsQueryResponse response)
    {
        var rows = response.Tables.FirstOrDefault()?.Rows ?? [];
        return rows.Select(row => new OtelError
        {
            Timestamp = ParseDateTimeOffset(row, 0),
            ExceptionType = GetString(row, 1),
            OuterMessage = GetStringOrNull(row, 2),
            Message = GetString(row, 3),
            OperationId = GetString(row, 4),
            AppName = GetString(row, 5)
        }).ToList();
    }

    private static List<OtelTrace> MapTraces(AppInsightsQueryResponse response)
    {
        var rows = response.Tables.FirstOrDefault()?.Rows ?? [];
        return rows.Select(row =>
        {
            var success = GetBool(row, 5);
            return new OtelTrace
            {
                Timestamp = ParseDateTimeOffset(row, 0),
                OperationId = GetString(row, 1),
                Name = GetString(row, 2),
                AppName = GetString(row, 3),
                DurationMs = GetDouble(row, 4),
                Success = success,
                ResultCode = GetStringOrNull(row, 6),
                HasError = !success
            };
        }).ToList();
    }

    private static List<OtelLog> MapLogs(AppInsightsQueryResponse response)
    {
        var rows = response.Tables.FirstOrDefault()?.Rows ?? [];
        return rows.Select(row => new OtelLog
        {
            Timestamp = ParseDateTimeOffset(row, 0),
            Severity = GetString(row, 1),
            Message = GetString(row, 2),
            OperationId = GetString(row, 3),
            AppName = GetString(row, 4)
        }).ToList();
    }

    private static List<OtelSpan> MapSpans(AppInsightsQueryResponse response)
    {
        var rows = response.Tables.FirstOrDefault()?.Rows ?? [];
        return rows.Select(row => new OtelSpan
        {
            Timestamp = ParseDateTimeOffset(row, 0),
            SpanId = GetString(row, 1),
            Target = GetStringOrNull(row, 2),
            Type = GetString(row, 3),
            Name = GetString(row, 4),
            DurationMs = GetDouble(row, 5),
            Success = GetBool(row, 6)
        }).ToList();
    }

    private static DateTimeOffset ParseDateTimeOffset(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return DateTimeOffset.MinValue;
        var el = row[index];
        if (el.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(el.GetString(), out var dt))
            return dt;
        return DateTimeOffset.MinValue;
    }

    private static string GetString(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return string.Empty;
        var el = row[index];
        return el.ValueKind == JsonValueKind.Null ? string.Empty : el.GetString() ?? string.Empty;
    }

    private static string? GetStringOrNull(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return null;
        var el = row[index];
        return el.ValueKind == JsonValueKind.Null ? null : el.GetString();
    }

    private static double GetDouble(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return 0;
        var el = row[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
    }

    private static bool GetBool(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return false;
        var el = row[index];
        if (el.ValueKind == JsonValueKind.True) return true;
        if (el.ValueKind == JsonValueKind.False) return false;
        return false;
    }
}
