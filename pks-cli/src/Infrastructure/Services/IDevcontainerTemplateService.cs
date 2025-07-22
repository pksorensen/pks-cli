using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing devcontainer templates
/// </summary>
public interface IDevcontainerTemplateService
{
    /// <summary>
    /// Gets all available templates
    /// </summary>
    /// <returns>List of available templates</returns>
    Task<List<DevcontainerTemplate>> GetAvailableTemplatesAsync();

    /// <summary>
    /// Gets a specific template by ID
    /// </summary>
    /// <param name="id">Template ID</param>
    /// <returns>Template or null if not found</returns>
    Task<DevcontainerTemplate?> GetTemplateAsync(string id);

    /// <summary>
    /// Applies a template to create a devcontainer configuration
    /// </summary>
    /// <param name="templateId">Template ID</param>
    /// <param name="options">Configuration options</param>
    /// <returns>Generated devcontainer configuration</returns>
    Task<DevcontainerConfiguration> ApplyTemplateAsync(string templateId, DevcontainerOptions options);

    /// <summary>
    /// Gets templates by category
    /// </summary>
    /// <param name="category">Template category</param>
    /// <returns>Templates in the category</returns>
    Task<List<DevcontainerTemplate>> GetTemplatesByCategoryAsync(string category);

    /// <summary>
    /// Searches templates by query string
    /// </summary>
    /// <param name="query">Search query</param>
    /// <returns>Matching templates</returns>
    Task<List<DevcontainerTemplate>> SearchTemplatesAsync(string query);

    /// <summary>
    /// Gets all available template categories
    /// </summary>
    /// <returns>List of categories</returns>
    Task<List<string>> GetAvailableCategoriesAsync();

    /// <summary>
    /// Validates that a template is compatible with specified options
    /// </summary>
    /// <param name="templateId">Template ID</param>
    /// <param name="options">Options to validate against</param>
    /// <returns>Validation result</returns>
    Task<DevcontainerValidationResult> ValidateTemplateCompatibilityAsync(string templateId, DevcontainerOptions options);

    /// <summary>
    /// Extracts a template to the specified location
    /// </summary>
    /// <param name="templateId">Template ID</param>
    /// <param name="options">Configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Template extraction result</returns>
    Task<NuGetTemplateExtractionResult> ExtractTemplateAsync(string templateId, DevcontainerOptions options, CancellationToken cancellationToken = default);
}