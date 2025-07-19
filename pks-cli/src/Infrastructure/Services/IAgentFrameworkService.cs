using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services;

/// <summary>
/// Service interface for managing the agent framework
/// </summary>
public interface IAgentFrameworkService
{
    /// <summary>
    /// Create a new agent with the specified configuration
    /// </summary>
    /// <param name="configuration">Agent configuration</param>
    /// <returns>Result of the create operation</returns>
    Task<AgentResult> CreateAgentAsync(AgentConfiguration configuration);

    /// <summary>
    /// List all available agents
    /// </summary>
    /// <returns>List of agent information</returns>
    Task<List<AgentInfo>> ListAgentsAsync();

    /// <summary>
    /// Get detailed status for a specific agent
    /// </summary>
    /// <param name="agentId">Agent identifier</param>
    /// <returns>Agent status information</returns>
    Task<AgentStatus> GetAgentStatusAsync(string agentId);

    /// <summary>
    /// Start an agent
    /// </summary>
    /// <param name="agentId">Agent identifier</param>
    /// <returns>Result of the start operation</returns>
    Task<AgentResult> StartAgentAsync(string agentId);

    /// <summary>
    /// Stop an agent
    /// </summary>
    /// <param name="agentId">Agent identifier</param>
    /// <returns>Result of the stop operation</returns>
    Task<AgentResult> StopAgentAsync(string agentId);

    /// <summary>
    /// Remove an agent completely
    /// </summary>
    /// <param name="agentId">Agent identifier</param>
    /// <returns>True if removal was successful</returns>
    Task<bool> RemoveAgentAsync(string agentId);

    /// <summary>
    /// Send a message to an agent
    /// </summary>
    /// <param name="message">Message to send</param>
    /// <returns>True if message was queued successfully</returns>
    Task<bool> SendMessageAsync(AgentMessage message);

    /// <summary>
    /// Get the message queue for an agent
    /// </summary>
    /// <param name="agentId">Agent identifier</param>
    /// <returns>List of pending messages</returns>
    Task<List<AgentMessage>> GetMessageQueueAsync(string agentId);

    /// <summary>
    /// Load agent configuration from file
    /// </summary>
    /// <param name="configFilePath">Path to configuration file</param>
    /// <returns>Agent configuration</returns>
    Task<AgentConfiguration> LoadConfigurationAsync(string configFilePath);

    /// <summary>
    /// Get the .pks/agents directory path for the current project
    /// </summary>
    /// <returns>Path to agents directory</returns>
    string GetAgentsDirectory();

    /// <summary>
    /// Initialize the agents infrastructure (create .pks/agents directory)
    /// </summary>
    /// <param name="projectPath">Path to the project</param>
    /// <returns>True if initialization was successful</returns>
    Task<bool> InitializeAgentsInfrastructureAsync(string projectPath);
}