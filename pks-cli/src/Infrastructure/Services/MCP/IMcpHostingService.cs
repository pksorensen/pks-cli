using PKS.CLI.Infrastructure.Services.Models;

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