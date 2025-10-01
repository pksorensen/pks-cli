using PKS.CLI.Infrastructure.Services.Models;
using PKS.CLI.Infrastructure.Services.MCP;

namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Modern SDK-based MCP server hosting service interface
/// </summary>
public interface IMcpHostingService
{
    /// <summary>
    /// Start the MCP server with the specified configuration using SDK hosting
    /// </summary>
    /// <param name="config">Server configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing server status and connection information</returns>
    Task<McpServerResult> StartServerAsync(McpServerConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the currently running MCP server
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if server was stopped successfully</returns>
    Task<bool> StopServerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current status of the MCP server
    /// </summary>
    /// <returns>Server status information</returns>
    Task<McpServerStatusInfo> GetServerStatusAsync();

    /// <summary>
    /// Restart the MCP server with current configuration
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing server status and connection information</returns>
    Task<McpServerResult> RestartServerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the server lifecycle state
    /// </summary>
    /// <returns>Current server lifecycle state</returns>
    McpServerLifecycleState GetLifecycleState();

    /// <summary>
    /// Register a custom tool service with the MCP server
    /// </summary>
    /// <typeparam name="T">Tool service type</typeparam>
    /// <param name="toolService">Tool service instance</param>
    void RegisterToolService<T>(T toolService) where T : class;

    /// <summary>
    /// Register a custom resource service with the MCP server
    /// </summary>
    /// <typeparam name="T">Resource service type</typeparam>
    /// <param name="resourceService">Resource service instance</param>
    void RegisterResourceService<T>(T resourceService) where T : class;

    /// <summary>
    /// Check if the MCP server is currently running
    /// </summary>
    /// <returns>True if the server is running</returns>
    Task<bool> IsRunningAsync();

    /// <summary>
    /// Get detailed server information
    /// </summary>
    /// <returns>Server information including transport, connections, and configuration</returns>
    Task<McpServerInfo> GetServerInfoAsync();

    /// <summary>
    /// Start the MCP server with the specified transport
    /// </summary>
    /// <param name="transport">Transport type (stdio, http, sse)</param>
    /// <returns>True if the server started successfully</returns>
    Task<bool> StartAsync(string transport = "stdio");

    /// <summary>
    /// Stop the MCP server
    /// </summary>
    /// <param name="gracePeriodSeconds">Grace period in seconds for graceful shutdown</param>
    /// <param name="force">Force stop without waiting for connections to close</param>
    /// <returns>True if the server stopped successfully</returns>
    Task<bool> StopAsync(int gracePeriodSeconds = 10, bool force = false);

    /// <summary>
    /// Get the current MCP server configuration
    /// </summary>
    /// <returns>Current server configuration</returns>
    Task<McpConfiguration> GetConfigurationAsync();

    /// <summary>
    /// Update the MCP server configuration
    /// </summary>
    /// <param name="configuration">New configuration settings</param>
    /// <returns>True if the configuration was updated successfully</returns>
    Task<bool> UpdateConfigurationAsync(McpConfiguration configuration);

    /// <summary>
    /// Get server logs with optional filtering
    /// </summary>
    /// <param name="entryCount">Maximum number of log entries to return</param>
    /// <param name="logLevel">Optional log level filter</param>
    /// <returns>Collection of log entries</returns>
    Task<IEnumerable<McpLogEntry>> GetLogsAsync(int entryCount = 50, string? logLevel = null);

    /// <summary>
    /// Get server performance metrics
    /// </summary>
    /// <returns>Performance metrics including response times, memory usage, etc.</returns>
    Task<McpPerformanceMetrics> GetPerformanceMetricsAsync();
}

/// <summary>
/// MCP server lifecycle states
/// </summary>
public enum McpServerLifecycleState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

/// <summary>
/// Extended MCP server information
/// </summary>
public class McpServerInfo
{
    /// <summary>
    /// Transport type (stdio, http, sse)
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// Server start time
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of active connections
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Default transport mode
    /// </summary>
    public string DefaultTransport { get; set; } = "stdio";

    /// <summary>
    /// Supported transport modes
    /// </summary>
    public string[] SupportedTransports { get; set; } = { "stdio", "http", "sse" };

    /// <summary>
    /// Whether automatic tool discovery is enabled
    /// </summary>
    public bool EnableAutoToolDiscovery { get; set; } = true;

    /// <summary>
    /// Enabled tool categories
    /// </summary>
    public string[] EnabledCategories { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Disabled tools
    /// </summary>
    public string[] DisabledTools { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Maximum allowed connections
    /// </summary>
    public int MaxConnections { get; set; } = 100;

    /// <summary>
    /// Timeout settings
    /// </summary>
    public McpTimeoutSettings TimeoutSettings { get; set; } = new();
}


/// <summary>
/// MCP log entry
/// </summary>
public class McpLogEntry
{
    /// <summary>
    /// Log entry timestamp
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Log level
    /// </summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>
    /// Log message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Log category
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Exception information (if any)
    /// </summary>
    public string? Exception { get; set; }
}

/// <summary>
/// MCP server performance metrics
/// </summary>
public class McpPerformanceMetrics
{
    /// <summary>
    /// Total number of requests processed
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Average response time in milliseconds
    /// </summary>
    public double AverageResponseTime { get; set; }

    /// <summary>
    /// Peak memory usage in bytes
    /// </summary>
    public long PeakMemoryUsage { get; set; }

    /// <summary>
    /// Total uptime
    /// </summary>
    public TimeSpan TotalUptime { get; set; }

    /// <summary>
    /// Error rate as percentage
    /// </summary>
    public double ErrorRate { get; set; }

    /// <summary>
    /// Current active connections
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Total tool invocations
    /// </summary>
    public long ToolInvocations { get; set; }

    /// <summary>
    /// Total resource accesses
    /// </summary>
    public long ResourceAccesses { get; set; }
}

/// <summary>
/// MCP timeout settings
/// </summary>
public class McpTimeoutSettings
{
    /// <summary>
    /// Request timeout in seconds
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Connection timeout in seconds
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Idle timeout in seconds
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 300;
}