using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    Task<AppInsightsConnectionResult> TestConnectionAsync(CancellationToken ct = default);
    Task<List<OtelError>> QueryErrorsAsync(TimeSpan since, int limit, string? appName = null, string? operationId = null, CancellationToken ct = default);
    Task<List<OtelTrace>> QueryTracesAsync(TimeSpan since, int limit, bool? hasError = null, string? appName = null, CancellationToken ct = default);
    Task<List<OtelLog>> QueryLogsAsync(TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, CancellationToken ct = default);
    Task<List<OtelSpan>> QuerySpansAsync(string operationId, CancellationToken ct = default);
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

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsHttpAdapter _httpAdapter;
    private readonly IAzureFoundryAuthService _authService;

    public AppInsightsQueryService(
        IAppInsightsConfigService configService,
        IAppInsightsHttpAdapter httpAdapter,
        IAzureFoundryAuthService authService)
    {
        _configService = configService;
        _httpAdapter = httpAdapter;
        _authService = authService;
    }

    public async Task<AppInsightsConnectionResult> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var config = await _configService.GetConfigAsync();
            if (config is null)
                return new AppInsightsConnectionResult { Success = false, ErrorMessage = "Not configured" };

            var token = await _authService.GetAccessTokenAsync(QueryScope, ct);
            if (string.IsNullOrEmpty(token))
                return new AppInsightsConnectionResult { Success = false, ErrorMessage = "Not authenticated. Run 'pks foundry init' first." };

            var kql = "requests | take 1 | project cloud_RoleName";
            var response = await _httpAdapter.QueryAsync(config.AppId, token, kql, ct);

            var resourceName = response.Tables.FirstOrDefault()?.Rows.FirstOrDefault()
                ?.ElementAtOrDefault(0).GetString();

            return new AppInsightsConnectionResult { Success = true, ResourceName = resourceName ?? config.ResourceName };
        }
        catch (Exception ex)
        {
            return new AppInsightsConnectionResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<List<OtelError>> QueryErrorsAsync(
        TimeSpan since, int limit, string? appName = null, string? operationId = null, CancellationToken ct = default)
    {
        var (config, token) = await RequireConfigAndTokenAsync(ct);
        var response = await _httpAdapter.QueryAsync(config.AppId, token, BuildErrorsKql(since, limit, appName, operationId), ct);
        return MapErrors(response);
    }

    public async Task<List<OtelTrace>> QueryTracesAsync(
        TimeSpan since, int limit, bool? hasError = null, string? appName = null, CancellationToken ct = default)
    {
        var (config, token) = await RequireConfigAndTokenAsync(ct);
        var response = await _httpAdapter.QueryAsync(config.AppId, token, BuildTracesKql(since, limit, hasError, appName), ct);
        return MapTraces(response);
    }

    public async Task<List<OtelLog>> QueryLogsAsync(
        TimeSpan since, string? severity = null, string? traceId = null, string? appName = null, CancellationToken ct = default)
    {
        var (config, token) = await RequireConfigAndTokenAsync(ct);
        var response = await _httpAdapter.QueryAsync(config.AppId, token, BuildLogsKql(since, severity, traceId, appName), ct);
        return MapLogs(response);
    }

    public async Task<List<OtelSpan>> QuerySpansAsync(string operationId, CancellationToken ct = default)
    {
        var (config, token) = await RequireConfigAndTokenAsync(ct);
        var response = await _httpAdapter.QueryAsync(config.AppId, token, BuildSpansKql(operationId), ct);
        return MapSpans(response);
    }

    public async Task<string?> GetConfiguredAppIdAsync(CancellationToken ct = default)
    {
        var config = await _configService.GetConfigAsync();
        return config?.AppId;
    }

    private async Task<(AppInsightsConfig config, string token)> RequireConfigAndTokenAsync(CancellationToken ct)
    {
        var config = await _configService.GetConfigAsync()
            ?? throw new InvalidOperationException("Application Insights not configured. Run 'pks appinsights init' first.");
        var token = await _authService.GetAccessTokenAsync(QueryScope, ct)
            ?? throw new InvalidOperationException("Not authenticated. Run 'pks foundry init' to sign in.");
        return (config, token);
    }

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
