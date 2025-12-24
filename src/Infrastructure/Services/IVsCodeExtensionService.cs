using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing VS Code extensions in devcontainers
/// </summary>
public interface IVsCodeExtensionService
{
    /// <summary>
    /// Gets recommended extensions for specified categories
    /// </summary>
    /// <param name="categories">Extension categories</param>
    /// <returns>List of recommended extensions</returns>
    Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(string[] categories);

    /// <summary>
    /// Searches extensions by query string
    /// </summary>
    /// <param name="query">Search query</param>
    /// <returns>Matching extensions</returns>
    Task<List<VsCodeExtension>> SearchExtensionsAsync(string query);

    /// <summary>
    /// Validates that an extension exists and is compatible
    /// </summary>
    /// <param name="extensionId">Extension ID</param>
    /// <returns>Validation result</returns>
    Task<ExtensionValidationResult> ValidateExtensionAsync(string extensionId);

    /// <summary>
    /// Gets extensions that are essential for specific features
    /// </summary>
    /// <param name="features">List of feature IDs</param>
    /// <returns>Essential extensions</returns>
    Task<List<VsCodeExtension>> GetEssentialExtensionsAsync(List<string> features);

    /// <summary>
    /// Gets all available extension categories
    /// </summary>
    /// <returns>List of categories</returns>
    Task<List<string>> GetAvailableCategoriesAsync();

    /// <summary>
    /// Gets extensions by category
    /// </summary>
    /// <param name="category">Extension category</param>
    /// <returns>Extensions in the category</returns>
    Task<List<VsCodeExtension>> GetExtensionsByCategoryAsync(string category);

    /// <summary>
    /// Gets the latest version of an extension
    /// </summary>
    /// <param name="extensionId">Extension ID</param>
    /// <returns>Latest version string or null if not found</returns>
    Task<string?> GetLatestVersionAsync(string extensionId);

    /// <summary>
    /// Validates a list of extensions for compatibility
    /// </summary>
    /// <param name="extensionIds">List of extension IDs</param>
    /// <returns>Validation results for each extension</returns>
    Task<Dictionary<string, ExtensionValidationResult>> ValidateExtensionsAsync(List<string> extensionIds);
}