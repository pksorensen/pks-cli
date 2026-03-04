namespace PKS.Infrastructure.Services.Runner;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Service for managing agentics runner configuration stored in ~/.pks-cli/agentics-runners.json
/// </summary>
public interface IAgenticsRunnerConfigurationService
{
    /// <summary>
    /// Loads the agentics runner configuration from disk, returning defaults if no file exists
    /// </summary>
    Task<AgenticsRunnerConfiguration> LoadAsync();

    /// <summary>
    /// Saves the agentics runner configuration to disk
    /// </summary>
    Task SaveAsync(AgenticsRunnerConfiguration configuration);

    /// <summary>
    /// Adds a new runner registration and persists it
    /// </summary>
    Task<AgenticsRunnerRegistration> AddRegistrationAsync(AgenticsRunnerRegistration registration);

    /// <summary>
    /// Lists all current registrations
    /// </summary>
    Task<List<AgenticsRunnerRegistration>> ListRegistrationsAsync();

    /// <summary>
    /// Gets a single registration by ID, or null if not found
    /// </summary>
    Task<AgenticsRunnerRegistration?> GetRegistrationAsync(string registrationId);
}
