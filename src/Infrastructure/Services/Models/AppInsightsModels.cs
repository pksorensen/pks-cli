using System.Text.Json.Serialization;
using PKS.Infrastructure.Services.Azure;

namespace PKS.Infrastructure.Services.Models;

public class AppInsightsConfig
{
    public string AppId { get; set; } = string.Empty;
    public string? ResourceName { get; set; }
    public string? SubscriptionId { get; set; }
    public DateTime RegisteredAt { get; set; }
}

// ARM resource models for Microsoft.Insights/components
public class AppInsightsComponent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public AppInsightsComponentProperties Properties { get; set; } = new();
}

public class AppInsightsComponentProperties
{
    [JsonPropertyName("AppId")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("ApplicationType")]
    public string ApplicationType { get; set; } = string.Empty;
}

public class AppInsightsComponentListResponse
{
    [JsonPropertyName("value")]
    public List<AppInsightsComponent> Value { get; set; } = new();
}

public class AppInsightsConnectionResult
{
    public bool Success { get; set; }
    public string? ResourceName { get; set; }
    public string? ErrorMessage { get; set; }
}

public class OtelError : IOtelRecord
{
    /// <summary>Registry name of the App Insights resource this row came from.</summary>
    public string Resource { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? OuterMessage { get; set; }
    public string? Stack { get; set; }
}

public class OtelTrace : IOtelRecord
{
    /// <summary>Registry name of the App Insights resource this row came from.</summary>
    public string Resource { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string? ResultCode { get; set; }
    public bool HasError { get; set; }
}

public class OtelLog : IOtelRecord
{
    /// <summary>Registry name of the App Insights resource this row came from.</summary>
    public string Resource { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? TraceId { get; set; }
}

public class OtelSpan : IOtelRecord
{
    /// <summary>Registry name of the App Insights resource this row came from.</summary>
    public string Resource { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string SpanId { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string? Target { get; set; }
}

/// <summary>
/// What every row the <c>otel</c> queries return has in common: a timestamp to merge on and the
/// registry name of the App Insights resource it came from, stamped by the query service so a
/// fan-out over several resources stays attributable row by row.
/// </summary>
public interface IOtelRecord
{
    DateTimeOffset Timestamp { get; }
    string Resource { get; set; }
}

/// <summary>One resource's failure inside a fan-out; the others' rows are still returned.</summary>
public sealed class OtelResourceError
{
    public required AzureResourceEntry Resource { get; init; }
    public required Exception Error { get; init; }
}

/// <summary>The merged rows of a fan-out plus the resources that failed to answer.</summary>
public sealed class OtelQueryResult<T> where T : IOtelRecord
{
    public List<T> Items { get; init; } = new();
    public List<OtelResourceError> Errors { get; init; } = new();

    /// <summary>How many resources were queried; <see cref="AllFailed"/> is measured against it.</summary>
    public int Attempted { get; init; }

    public bool AllFailed => Attempted > 0 && Errors.Count == Attempted;
}
