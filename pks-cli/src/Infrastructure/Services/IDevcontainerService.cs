using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Core service for devcontainer operations
/// </summary>
public interface IDevcontainerService
{
    /// <summary>
    /// Creates a complete devcontainer configuration based on options
    /// </summary>
    /// <param name="options">Configuration options</param>
    /// <returns>Result containing the configuration and generated files</returns>
    Task<DevcontainerResult> CreateConfigurationAsync(DevcontainerOptions options);

    /// <summary>
    /// Validates a devcontainer configuration
    /// </summary>
    /// <param name="configuration">Configuration to validate</param>
    /// <returns>Validation result with errors and warnings</returns>
    Task<DevcontainerValidationResult> ValidateConfigurationAsync(DevcontainerConfiguration configuration);

    /// <summary>
    /// Resolves feature dependencies and conflicts
    /// </summary>
    /// <param name="features">List of feature IDs to resolve</param>
    /// <returns>Resolution result with resolved features and conflicts</returns>
    Task<FeatureResolutionResult> ResolveFeatureDependenciesAsync(List<string> features);

    /// <summary>
    /// Merges two devcontainer configurations intelligently
    /// </summary>
    /// <param name="baseConfig">Base configuration</param>
    /// <param name="overlayConfig">Overlay configuration</param>
    /// <returns>Merged configuration</returns>
    Task<DevcontainerConfiguration> MergeConfigurationsAsync(DevcontainerConfiguration baseConfig, DevcontainerConfiguration overlayConfig);

    /// <summary>
    /// Gets recommended extensions based on selected features
    /// </summary>
    /// <param name="features">Selected features</param>
    /// <returns>List of recommended extensions</returns>
    Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(List<string> features);

    /// <summary>
    /// Updates an existing devcontainer configuration
    /// </summary>
    /// <param name="configPath">Path to existing devcontainer.json</param>
    /// <param name="updates">Updates to apply</param>
    /// <returns>Update result</returns>
    Task<DevcontainerResult> UpdateConfigurationAsync(string configPath, DevcontainerOptions updates);

    /// <summary>
    /// Validates that the target path is suitable for devcontainer generation
    /// </summary>
    /// <param name="outputPath">Target output path</param>
    /// <returns>Path validation result</returns>
    Task<PathValidationResult> ValidateOutputPathAsync(string outputPath);

    // Additional methods for MCP tool service compatibility
    /// <summary>
    /// Initialize a new devcontainer configuration
    /// </summary>
    /// <param name="config">Devcontainer configuration</param>
    /// <returns>Initialization result</returns>
    Task<DevcontainerResult> InitializeAsync(DevcontainerConfiguration config);

    /// <summary>
    /// Check if current directory has a devcontainer configuration
    /// </summary>
    /// <returns>True if devcontainer exists</returns>
    Task<bool> HasDevcontainerAsync();

    /// <summary>
    /// Add features to existing devcontainer
    /// </summary>
    /// <param name="features">Features to add</param>
    /// <returns>Operation result</returns>
    Task<DevcontainerResult> AddFeaturesAsync(List<string> features);

    /// <summary>
    /// Check if devcontainer is currently running
    /// </summary>
    /// <returns>True if running</returns>
    Task<bool> IsRunningAsync();

    /// <summary>
    /// Get runtime information for running devcontainer
    /// </summary>
    /// <returns>Runtime information</returns>
    Task<DevcontainerRuntimeInfo> GetRuntimeInfoAsync();

    /// <summary>
    /// Rebuild the devcontainer
    /// </summary>
    /// <param name="force">Force rebuild</param>
    /// <returns>Rebuild result</returns>
    Task<DevcontainerResult> RebuildAsync(bool force = false);

    /// <summary>
    /// Clear devcontainer cache
    /// </summary>
    /// <returns>Task</returns>
    Task ClearCacheAsync();

    /// <summary>
    /// Get current devcontainer configuration
    /// </summary>
    /// <returns>Current configuration</returns>
    Task<DevcontainerConfiguration> GetConfigurationAsync();
}