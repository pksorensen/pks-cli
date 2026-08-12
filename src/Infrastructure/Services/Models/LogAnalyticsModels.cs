using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Locally stored Log Analytics configuration. <see cref="WorkspaceId"/> is the
/// workspace GUID (ARM <c>properties.customerId</c>), not the ARM resource id —
/// that is what the query API addresses workspaces by.
/// </summary>
public class LogAnalyticsConfig
{
    public string WorkspaceId { get; set; } = string.Empty;
    public string? WorkspaceName { get; set; }
    public string? ResourceId { get; set; }
    public string? SubscriptionId { get; set; }
    public DateTime RegisteredAt { get; set; }
}

public class LogAnalyticsConnectionResult
{
    public bool Success { get; set; }
    public string? WorkspaceName { get; set; }
    public string? ErrorMessage { get; set; }
}

// ARM resource models for Microsoft.OperationalInsights/workspaces
public class LogAnalyticsWorkspace
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public LogAnalyticsWorkspaceProperties Properties { get; set; } = new();
}

public class LogAnalyticsWorkspaceProperties
{
    /// <summary>The workspace GUID used by the query API.</summary>
    [JsonPropertyName("customerId")]
    public string CustomerId { get; set; } = string.Empty;
}

public class LogAnalyticsWorkspaceListResponse
{
    [JsonPropertyName("value")]
    public List<LogAnalyticsWorkspace> Value { get; set; } = new();
}

// Query API response shape (tables/columns/rows). Identical on the Log Analytics
// and Application Insights query APIs.
public class KustoQueryResponse
{
    [JsonPropertyName("tables")]
    public List<KustoTable> Tables { get; set; } = new();
}

public class KustoTable
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<KustoColumn> Columns { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<List<JsonElement>> Rows { get; set; } = new();
}

public class KustoColumn
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
