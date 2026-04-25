namespace PKS.Infrastructure.Services.Firecracker;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Service for managing firecracker runner configuration stored in ~/.pks-cli/firecracker-runners.json
/// </summary>
public interface IFirecrackerRunnerConfigurationService
{
    /// <summary>
    /// Loads the firecracker runner configuration from disk, returning defaults if no file exists
    /// </summary>
    Task<FirecrackerRunnerConfiguration> LoadAsync();

    /// <summary>
    /// Saves the firecracker runner configuration to disk
    /// </summary>
    Task SaveAsync(FirecrackerRunnerConfiguration configuration);

    /// <summary>
    /// Adds a new runner registration and persists it
    /// </summary>
    Task<FirecrackerRunnerRegistration> AddRegistrationAsync(FirecrackerRunnerRegistration registration);

    /// <summary>
    /// Lists all current registrations
    /// </summary>
    Task<List<FirecrackerRunnerRegistration>> ListRegistrationsAsync();

    /// <summary>
    /// Gets a single registration by ID, or null if not found
    /// </summary>
    Task<FirecrackerRunnerRegistration?> GetRegistrationAsync(string registrationId);
}
