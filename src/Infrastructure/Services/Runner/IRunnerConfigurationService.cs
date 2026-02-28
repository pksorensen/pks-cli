namespace PKS.Infrastructure.Services.Runner;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Service for managing runner configuration stored in ~/.pks-cli/runners.json
/// </summary>
public interface IRunnerConfigurationService
{
    /// <summary>
    /// Loads the runner configuration from disk, returning defaults if no file exists
    /// </summary>
    Task<RunnerConfiguration> LoadAsync();

    /// <summary>
    /// Saves the runner configuration to disk
    /// </summary>
    Task SaveAsync(RunnerConfiguration configuration);

    /// <summary>
    /// Adds a new runner registration for the given owner/repository and persists it
    /// </summary>
    Task<RunnerRegistration> AddRegistrationAsync(string owner, string repository, string? labels = null);

    /// <summary>
    /// Removes a registration by ID. Returns true if found and removed, false otherwise
    /// </summary>
    Task<bool> RemoveRegistrationAsync(string registrationId);

    /// <summary>
    /// Lists all current registrations
    /// </summary>
    Task<List<RunnerRegistration>> ListRegistrationsAsync();

    /// <summary>
    /// Gets a single registration by ID, or null if not found
    /// </summary>
    Task<RunnerRegistration?> GetRegistrationAsync(string registrationId);
}
