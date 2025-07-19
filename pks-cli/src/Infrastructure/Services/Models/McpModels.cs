namespace PKS.CLI.Infrastructure.Services.Models;

/// <summary>
/// Configuration for MCP server startup
/// </summary>
public class McpServerConfig
{
    /// <summary>
    /// Port number for HTTP transport (ignored for stdio)
    /// </summary>
    public int Port { get; set; } = 3000;

    /// <summary>
    /// Transport mode: "stdio" or "http" or "sse"
    /// </summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>
    /// Additional server settings
    /// </summary>
    public Dictionary<string, object> Settings { get; set; } = new();

    /// <summary>
    /// Enable debug mode
    /// </summary>
    public bool Debug { get; set; } = false;

    /// <summary>
    /// Configuration file path
    /// </summary>
    public string? ConfigFile { get; set; }

    /// <summary>
    /// Enable stateless mode for HTTP transport
    /// </summary>
    public bool Stateless { get; set; } = true;
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
    /// Port the server is running on (if applicable)
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Result message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Transport mode used
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>
    /// Process ID for stdio transport
    /// </summary>
    public int? ProcessId { get; set; }
}

/// <summary>
/// Current status of the MCP server
/// </summary>
public class McpServerStatus
{
    /// <summary>
    /// Whether the server is currently running
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Port the server is running on (if applicable)
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Transport mode being used
    /// </summary>
    public string? Transport { get; set; }

    /// <summary>
    /// When the server was started
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Process ID for stdio transport
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    /// Server version
    /// </summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Number of active connections
    /// </summary>
    public int ActiveConnections { get; set; }
}

/// <summary>
/// MCP resource definition
/// </summary>
public class McpResource
{
    /// <summary>
    /// Unique resource identifier
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Resource description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the resource
    /// </summary>
    public string MimeType { get; set; } = "text/plain";

    /// <summary>
    /// Resource metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// MCP tool definition
/// </summary>
public class McpTool
{
    /// <summary>
    /// Tool name/identifier
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tool description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Input schema for the tool
    /// </summary>
    public object InputSchema { get; set; } = new();

    /// <summary>
    /// Tool category
    /// </summary>
    public string Category { get; set; } = "general";

    /// <summary>
    /// Whether the tool is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
}