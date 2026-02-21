using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Features;

/// <summary>
/// Interface for devcontainer features
/// </summary>
public interface IDevcontainerFeature
{
    /// <summary>
    /// Unique identifier for the feature
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name of the feature
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Feature description
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Feature version
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Feature category
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Feature tags for searching and categorization
    /// </summary>
    string[] Tags { get; }

    /// <summary>
    /// Whether this feature is deprecated
    /// </summary>
    bool IsDeprecated { get; }

    /// <summary>
    /// Dependencies required by this feature
    /// </summary>
    string[] Dependencies { get; }

    /// <summary>
    /// Features that conflict with this one
    /// </summary>
    string[] ConflictsWith { get; }

    /// <summary>
    /// Default configuration options for this feature
    /// </summary>
    Dictionary<string, object> DefaultOptions { get; }

    /// <summary>
    /// Available configuration options
    /// </summary>
    Dictionary<string, DevcontainerFeatureOption> AvailableOptions { get; }

    /// <summary>
    /// Validates the feature configuration
    /// </summary>
    /// <param name="configuration">Configuration to validate</param>
    /// <returns>Validation result</returns>
    Task<FeatureValidationResult> ValidateConfigurationAsync(object configuration);

    /// <summary>
    /// Generates the feature configuration for devcontainer.json
    /// </summary>
    /// <param name="options">User-provided options</param>
    /// <returns>Feature configuration</returns>
    Task<Dictionary<string, object>> GenerateConfigurationAsync(Dictionary<string, object>? options = null);

    /// <summary>
    /// Gets the feature repository URL
    /// </summary>
    /// <returns>Repository URL</returns>
    string GetRepositoryUrl();

    /// <summary>
    /// Gets documentation URL for this feature
    /// </summary>
    /// <returns>Documentation URL</returns>
    string GetDocumentationUrl();

    /// <summary>
    /// Checks if this feature is compatible with the specified base image
    /// </summary>
    /// <param name="baseImage">Base image name</param>
    /// <returns>True if compatible</returns>
    Task<bool> IsCompatibleWithImageAsync(string baseImage);

    /// <summary>
    /// Gets recommended VS Code extensions for this feature
    /// </summary>
    /// <returns>List of extension IDs</returns>
    Task<List<string>> GetRecommendedExtensionsAsync();

    /// <summary>
    /// Gets environment variables that should be set when using this feature
    /// </summary>
    /// <returns>Environment variables</returns>
    Task<Dictionary<string, string>> GetEnvironmentVariablesAsync();

    /// <summary>
    /// Gets ports that should be forwarded when using this feature
    /// </summary>
    /// <returns>List of port numbers</returns>
    Task<List<int>> GetForwardedPortsAsync();

    /// <summary>
    /// Gets additional setup commands that should run after container creation
    /// </summary>
    /// <returns>List of commands</returns>
    Task<List<string>> GetPostCreateCommandsAsync();
}