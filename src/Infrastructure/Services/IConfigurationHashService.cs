using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for computing configuration hashes to detect devcontainer configuration changes
/// </summary>
public interface IConfigurationHashService
{
    /// <summary>
    /// Computes a hash of the devcontainer configuration and related files
    /// </summary>
    /// <param name="projectPath">Path to the project directory</param>
    /// <param name="devcontainerPath">Path to .devcontainer directory</param>
    /// <returns>SHA256 hash of the configuration</returns>
    Task<string> ComputeConfigurationHashAsync(string projectPath, string devcontainerPath);

    /// <summary>
    /// Computes hash with detailed information about included files
    /// </summary>
    /// <param name="projectPath">Path to the project directory</param>
    /// <param name="devcontainerPath">Path to .devcontainer directory</param>
    /// <returns>Hash result with details</returns>
    Task<ConfigurationHashResult> ComputeConfigurationHashWithDetailsAsync(string projectPath, string devcontainerPath);

    /// <summary>
    /// Normalizes JSON content by removing comments, whitespace, and sorting keys
    /// </summary>
    /// <param name="json">JSON string to normalize</param>
    /// <returns>Normalized JSON string</returns>
    string NormalizeJson(string json);

    /// <summary>
    /// Checks if configuration has changed compared to stored hash
    /// </summary>
    /// <param name="projectPath">Path to the project directory</param>
    /// <param name="devcontainerPath">Path to .devcontainer directory</param>
    /// <param name="storedHash">Previously stored configuration hash</param>
    /// <returns>Change detection result</returns>
    Task<ConfigurationChangeResult> CheckConfigurationChangedAsync(
        string projectPath,
        string devcontainerPath,
        string storedHash);
}
