using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Registry for managing devcontainer features
/// </summary>
public interface IDevcontainerFeatureRegistry
{
    /// <summary>
    /// Gets all available features
    /// </summary>
    /// <returns>List of available features</returns>
    Task<List<DevcontainerFeature>> GetAvailableFeaturesAsync();

    /// <summary>
    /// Gets a specific feature by ID
    /// </summary>
    /// <param name="id">Feature ID</param>
    /// <returns>Feature or null if not found</returns>
    Task<DevcontainerFeature?> GetFeatureAsync(string id);

    /// <summary>
    /// Searches features by query string
    /// </summary>
    /// <param name="query">Search query</param>
    /// <returns>Matching features</returns>
    Task<List<DevcontainerFeature>> SearchFeaturesAsync(string query);

    /// <summary>
    /// Gets features by category
    /// </summary>
    /// <param name="category">Feature category</param>
    /// <returns>Features in the category</returns>
    Task<List<DevcontainerFeature>> GetFeaturesByCategory(string category);

    /// <summary>
    /// Validates feature configuration options
    /// </summary>
    /// <param name="featureId">Feature ID</param>
    /// <param name="configuration">Configuration to validate</param>
    /// <returns>Validation result</returns>
    Task<FeatureValidationResult> ValidateFeatureConfiguration(string featureId, object configuration);

    /// <summary>
    /// Gets feature dependencies
    /// </summary>
    /// <param name="featureId">Feature ID</param>
    /// <returns>List of dependency feature IDs</returns>
    Task<List<string>> GetFeatureDependenciesAsync(string featureId);

    /// <summary>
    /// Gets features that conflict with the specified feature
    /// </summary>
    /// <param name="featureId">Feature ID</param>
    /// <returns>List of conflicting feature IDs</returns>
    Task<List<string>> GetFeatureConflictsAsync(string featureId);

    /// <summary>
    /// Gets all available feature categories
    /// </summary>
    /// <returns>List of categories</returns>
    Task<List<string>> GetAvailableCategoriesAsync();

    /// <summary>
    /// Refreshes the feature registry from remote sources
    /// </summary>
    /// <returns>True if refresh was successful</returns>
    Task<bool> RefreshFeaturesAsync();

    /// <summary>
    /// Gets features that are compatible with a specific base image
    /// </summary>
    /// <param name="baseImage">Base image name</param>
    /// <returns>Compatible features</returns>
    Task<List<DevcontainerFeature>> GetCompatibleFeaturesAsync(string baseImage);
}