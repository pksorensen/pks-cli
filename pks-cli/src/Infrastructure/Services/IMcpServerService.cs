using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services;

/// <summary>
/// Service for managing the Model Context Protocol (MCP) server
/// </summary>
public interface IMcpServerService
{
    /// <summary>
    /// Start the MCP server with the specified configuration
    /// </summary>
    /// <param name="config">Server configuration</param>
    /// <returns>Result containing server status and port information</returns>
    Task<McpServerResult> StartServerAsync(McpServerConfig config);

    /// <summary>
    /// Stop the currently running MCP server
    /// </summary>
    /// <returns>True if server was stopped successfully</returns>
    Task<bool> StopServerAsync();

    /// <summary>
    /// Get the current status of the MCP server
    /// </summary>
    /// <returns>Server status information</returns>
    Task<McpServerStatus> GetServerStatusAsync();

    /// <summary>
    /// Restart the MCP server with current configuration
    /// </summary>
    /// <returns>Result containing server status and port information</returns>
    Task<McpServerResult> RestartServerAsync();

    /// <summary>
    /// Get available MCP resources (agents, tasks, etc.)
    /// </summary>
    /// <returns>List of available resources</returns>
    Task<IEnumerable<McpResource>> GetResourcesAsync();

    /// <summary>
    /// Get available MCP tools
    /// </summary>
    /// <returns>List of available tools</returns>
    Task<IEnumerable<McpTool>> GetToolsAsync();
}