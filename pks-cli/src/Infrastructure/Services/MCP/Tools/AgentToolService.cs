using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS agent management operations
/// This service provides MCP tools for agent creation, management, and status checking
/// </summary>
public class AgentToolService
{
    private readonly ILogger<AgentToolService> _logger;
    private readonly IAgentFrameworkService _agentFrameworkService;

    public AgentToolService(
        ILogger<AgentToolService> logger,
        IAgentFrameworkService agentFrameworkService)
    {
        _logger = logger;
        _agentFrameworkService = agentFrameworkService;
    }

    /// <summary>
    /// Create a new agent in the PKS agent framework
    /// This tool connects to the real PKS agent command functionality
    /// </summary>
    [McpServerTool("pks_create_agent", "Create a new agent in the PKS agent framework", "agent-management", true)]
    public async Task<object> CreateAgentAsync(
        [McpToolParameter("Agent name", required: true)] string agentName,
        [McpToolParameter("Agent type", defaultValue: "automation")] string agentType = "automation",
        [McpToolParameter("Agent configuration as JSON")] string? config = null)
    {
        _logger.LogInformation("MCP Tool: Creating agent '{AgentName}' of type '{AgentType}'", agentName, agentType);

        try
        {
            // Build agent configuration
            var agentConfiguration = new AgentConfiguration
            {
                Name = agentName,
                Type = agentType,
                Settings = new Dictionary<string, object>()
            };

            // Parse config if provided
            if (!string.IsNullOrWhiteSpace(config))
            {
                try
                {
                    var configDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(config);
                    if (configDict != null)
                    {
                        foreach (var kvp in configDict)
                        {
                            agentConfiguration.Settings[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse agent configuration JSON, using defaults");
                }
            }

            // Create the agent
            var result = await _agentFrameworkService.CreateAgentAsync(agentConfiguration);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    agentId = result.AgentId,
                    agentName,
                    agentType,
                    status = "created",
                    configuration = agentConfiguration.Settings,
                    createdAt = DateTime.UtcNow,
                    message = result.Message ?? $"Agent '{agentName}' created successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    agentName,
                    agentType,
                    error = result.Message,
                    message = $"Failed to create agent '{agentName}': {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent '{AgentName}'", agentName);
            return new
            {
                success = false,
                agentName,
                agentType,
                error = ex.Message,
                message = $"Agent creation failed with exception: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get agent status and information
    /// This tool connects to the real PKS agent status functionality
    /// </summary>
    [McpServerTool("pks_get_agent_status", "Check availability and status of development agents", "agent-management", true)]
    public async Task<object> GetAgentStatusAsync(
        [McpToolParameter("Agent ID to check status for")] string? agentId = null,
        [McpToolParameter("Include detailed agent information")] bool detailed = false)
    {
        _logger.LogInformation("MCP Tool: Getting agent status for ID '{AgentId}', detailed: {Detailed}", agentId, detailed);

        try
        {
            if (string.IsNullOrWhiteSpace(agentId))
            {
                // Return list of all agents
                var agents = await _agentFrameworkService.ListAgentsAsync();
                
                return new
                {
                    success = true,
                    agentCount = agents.Count(),
                    agents = agents.Select(agent => new
                    {
                        id = agent.Id,
                        name = agent.Name,
                        type = agent.Type,
                        status = agent.Status,
                        createdAt = agent.CreatedAt,
                        lastActivity = agent.LastActivity
                    }).ToArray(),
                    timestamp = DateTime.UtcNow,
                    message = $"Retrieved status for {agents.Count()} agents"
                };
            }
            else
            {
                // Get specific agent status
                var status = await _agentFrameworkService.GetAgentStatusAsync(agentId);
                
                var result = new
                {
                    success = true,
                    id = status.Id,
                    status = status.Status,
                    lastActivity = status.LastActivity,
                    messageQueueCount = status.MessageQueueCount,
                    currentTasks = status.CurrentTasks.ToArray(),
                    timestamp = DateTime.UtcNow,
                    message = $"Agent status retrieved successfully"
                };

                if (detailed)
                {
                    return new
                    {
                        success = result.success,
                        id = result.id,
                        status = result.status,
                        lastActivity = result.lastActivity,
                        messageQueueCount = result.messageQueueCount,
                        currentTasks = result.currentTasks,
                        timestamp = result.timestamp,
                        message = result.message,
                        detailedInfo = new
                        {
                            details = status.Details,
                            uptime = status.LastActivity?.Subtract(DateTime.UtcNow.AddHours(-1)) ?? TimeSpan.Zero,
                            memoryUsage = "N/A", // Would need to be implemented in the agent framework
                            lastHeartbeat = status.LastActivity
                        }
                    };
                }

                return result;
            }
        }
        catch (AgentNotFoundException ex)
        {
            return new
            {
                success = false,
                agentId,
                error = "Agent not found",
                message = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get agent status for ID '{AgentId}'", agentId);
            return new
            {
                success = false,
                agentId,
                error = ex.Message,
                message = $"Failed to retrieve agent status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Start an agent
    /// </summary>
    [McpServerTool("pks_start_agent", "Start a specific agent", "agent-management", true)]
    public async Task<object> StartAgentAsync(
        [McpToolParameter("Agent ID to start", required: true)] string agentId)
    {
        _logger.LogInformation("MCP Tool: Starting agent '{AgentId}'", agentId);

        try
        {
            var result = await _agentFrameworkService.StartAgentAsync(agentId);

            return new
            {
                success = result.Success,
                agentId,
                message = result.Message ?? (result.Success ? $"Agent '{agentId}' started successfully" : $"Failed to start agent '{agentId}'"),
                startedAt = result.Success ? DateTime.UtcNow : (DateTime?)null
            };
        }
        catch (AgentNotFoundException ex)
        {
            return new
            {
                success = false,
                agentId,
                error = "Agent not found",
                message = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start agent '{AgentId}'", agentId);
            return new
            {
                success = false,
                agentId,
                error = ex.Message,
                message = $"Failed to start agent: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Stop an agent
    /// </summary>
    [McpServerTool("pks_stop_agent", "Stop a specific agent", "agent-management", true)]
    public async Task<object> StopAgentAsync(
        [McpToolParameter("Agent ID to stop", required: true)] string agentId)
    {
        _logger.LogInformation("MCP Tool: Stopping agent '{AgentId}'", agentId);

        try
        {
            var result = await _agentFrameworkService.StopAgentAsync(agentId);

            return new
            {
                success = result.Success,
                agentId,
                message = result.Message ?? (result.Success ? $"Agent '{agentId}' stopped successfully" : $"Failed to stop agent '{agentId}'"),
                stoppedAt = result.Success ? DateTime.UtcNow : (DateTime?)null
            };
        }
        catch (AgentNotFoundException ex)
        {
            return new
            {
                success = false,
                agentId,
                error = "Agent not found",
                message = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop agent '{AgentId}'", agentId);
            return new
            {
                success = false,
                agentId,
                error = ex.Message,
                message = $"Failed to stop agent: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Remove an agent
    /// </summary>
    [McpServerTool("pks_remove_agent", "Remove a specific agent", "agent-management", true)]
    public async Task<object> RemoveAgentAsync(
        [McpToolParameter("Agent ID to remove", required: true)] string agentId)
    {
        _logger.LogInformation("MCP Tool: Removing agent '{AgentId}'", agentId);

        try
        {
            var success = await _agentFrameworkService.RemoveAgentAsync(agentId);

            return new
            {
                success,
                agentId,
                message = success ? $"Agent '{agentId}' removed successfully" : $"Failed to remove agent '{agentId}'",
                removedAt = success ? DateTime.UtcNow : (DateTime?)null
            };
        }
        catch (AgentNotFoundException ex)
        {
            return new
            {
                success = false,
                agentId,
                error = "Agent not found",
                message = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove agent '{AgentId}'", agentId);
            return new
            {
                success = false,
                agentId,
                error = ex.Message,
                message = $"Failed to remove agent: {ex.Message}"
            };
        }
    }
}