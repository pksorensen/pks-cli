using System.Text.Json.Serialization;

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

public class OtelError
{
    public DateTimeOffset Timestamp { get; set; }
    public string ExceptionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? OuterMessage { get; set; }
    public string? Stack { get; set; }
}

public class OtelTrace
{
    public DateTimeOffset Timestamp { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string? ResultCode { get; set; }
    public bool HasError { get; set; }
}

public class OtelLog
{
    public DateTimeOffset Timestamp { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string? TraceId { get; set; }
}

public class OtelSpan
{
    public DateTimeOffset Timestamp { get; set; }
    public string SpanId { get; set; } = string.Empty;
    public string ParentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string? Target { get; set; }
}
