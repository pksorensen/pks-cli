namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Configuration for starting an MCP server
/// </summary>
public class McpServerConfig
{
    /// <summary>
    /// Transport mode (stdio, http, sse)
    /// </summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>
    /// Port number for HTTP/SSE transports
    /// </summary>
    public int Port { get; set; } = 3000;

    /// <summary>
    /// Enable debug mode with verbose logging
    /// </summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Path to configuration file
    /// </summary>
    public string? ConfigFile { get; set; }

    /// <summary>
    /// Server name identifier
    /// </summary>
    public string ServerName { get; set; } = "pks-cli";

    /// <summary>
    /// Server version
    /// </summary>
    public string ServerVersion { get; set; } = "1.0.0";
}

/// <summary>
/// Result of MCP server operations
/// </summary>
public class McpServerResult
{
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Port the server is running on (for HTTP/SSE)
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Server status
    /// </summary>
    public McpServerStatus Status { get; set; } = McpServerStatus.Stopped;

    /// <summary>
    /// Transport type
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// Process ID (for stdio transport)
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Additional result data
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static McpServerResult CreateSuccess(string message, int port = 0, McpServerStatus status = McpServerStatus.Running)
    {
        return new McpServerResult
        {
            Success = true,
            Message = message,
            Port = port,
            Status = status
        };
    }

    /// <summary>
    /// Create a failure result
    /// </summary>
    public static McpServerResult CreateFailure(string message, McpServerStatus status = McpServerStatus.Error)
    {
        return new McpServerResult
        {
            Success = false,
            Message = message,
            Status = status
        };
    }
}

/// <summary>
/// Status of the MCP server
/// </summary>
public enum McpServerStatus
{
    /// <summary>
    /// Server is stopped
    /// </summary>
    Stopped = 0,

    /// <summary>
    /// Server is starting
    /// </summary>
    Starting = 1,

    /// <summary>
    /// Server is running
    /// </summary>
    Running = 2,

    /// <summary>
    /// Server is stopping
    /// </summary>
    Stopping = 3,

    /// <summary>
    /// Server encountered an error
    /// </summary>
    Error = 4
}

/// <summary>
/// Detailed server status information
/// </summary>
public class McpServerStatusInfo
{
    /// <summary>
    /// Current server status
    /// </summary>
    public McpServerStatus Status { get; set; } = McpServerStatus.Stopped;

    /// <summary>
    /// Whether the server is currently running
    /// </summary>
    public bool IsRunning => Status == McpServerStatus.Running;

    /// <summary>
    /// Server version
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Server start time
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Transport type
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// Port number (for HTTP/SSE transports)
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Number of active connections
    /// </summary>
    public int ActiveConnections { get; set; }
}

/// <summary>
/// MCP tool definition
/// </summary>
public class McpTool
{
    /// <summary>
    /// Tool name (unique identifier)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tool display name
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Tool description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Tool category
    /// </summary>
    public string Category { get; set; } = "General";

    /// <summary>
    /// Tool input schema (JSON Schema)
    /// </summary>
    public object? InputSchema { get; set; }

    /// <summary>
    /// Tool implementation type
    /// </summary>
    public Type? ImplementationType { get; set; }

    /// <summary>
    /// Whether the tool is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tool metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Result of MCP tool execution
/// </summary>
public class McpToolResult
{
    /// <summary>
    /// Whether the tool execution was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Result message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Tool output data
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// Execution duration in milliseconds
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Execution time in milliseconds (alias for DurationMs for compatibility)
    /// </summary>
    public long ExecutionTimeMs
    {
        get => DurationMs;
        set => DurationMs = value;
    }

    /// <summary>
    /// Error details (if any)
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Create a successful result
    /// </summary>
    public static McpToolResult CreateSuccess(string message, object? data = null, long durationMs = 0)
    {
        return new McpToolResult
        {
            Success = true,
            Message = message,
            Data = data,
            DurationMs = durationMs
        };
    }

    /// <summary>
    /// Create a failure result
    /// </summary>
    public static McpToolResult CreateFailure(string message, string? error = null, long durationMs = 0)
    {
        return new McpToolResult
        {
            Success = false,
            Message = message,
            Error = error,
            DurationMs = durationMs
        };
    }
}

/// <summary>
/// MCP resource definition
/// </summary>
public class McpResource
{
    /// <summary>
    /// Resource URI (unique identifier)
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Resource display name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Resource description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Resource MIME type
    /// </summary>
    public string MimeType { get; set; } = "text/plain";

    /// <summary>
    /// Whether the resource is available
    /// </summary>
    public bool Available { get; set; } = true;

    /// <summary>
    /// Resource metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}