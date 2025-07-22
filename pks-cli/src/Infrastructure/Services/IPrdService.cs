using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing Product Requirements Documents (PRDs)
/// </summary>
public interface IPrdService
{
    /// <summary>
    /// Generate a PRD document from an idea description
    /// </summary>
    /// <param name="request">PRD generation request with idea description and context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated PRD result</returns>
    Task<PrdGenerationResult> GeneratePrdAsync(
        PrdGenerationRequest request, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load and parse an existing PRD file
    /// </summary>
    /// <param name="filePath">Path to the PRD file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Loaded PRD result</returns>
    Task<PrdLoadResult> LoadPrdAsync(
        string filePath, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a PRD document to file
    /// </summary>
    /// <param name="document">PRD document to save</param>
    /// <param name="filePath">Output file path</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if saved successfully</returns>
    Task<bool> SavePrdAsync(
        PrdDocument document, 
        string filePath, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get status information about a PRD file
    /// </summary>
    /// <param name="filePath">Path to the PRD file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PRD status information</returns>
    Task<PrdStatus> GetPrdStatusAsync(
        string filePath, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extract requirements from a PRD document
    /// </summary>
    /// <param name="document">PRD document</param>
    /// <param name="filterByStatus">Optional status filter</param>
    /// <param name="filterByPriority">Optional priority filter</param>
    /// <returns>Filtered list of requirements</returns>
    Task<List<PrdRequirement>> GetRequirementsAsync(
        PrdDocument document,
        RequirementStatus? filterByStatus = null,
        RequirementPriority? filterByPriority = null);

    /// <summary>
    /// Extract user stories from a PRD document
    /// </summary>
    /// <param name="document">PRD document</param>
    /// <param name="filterByPriority">Optional priority filter</param>
    /// <returns>Filtered list of user stories</returns>
    Task<List<UserStory>> GetUserStoriesAsync(
        PrdDocument document,
        UserStoryPriority? filterByPriority = null);

    /// <summary>
    /// Update a requirement in a PRD document
    /// </summary>
    /// <param name="document">PRD document</param>
    /// <param name="requirementId">ID of the requirement to update</param>
    /// <param name="updates">Updates to apply</param>
    /// <returns>True if updated successfully</returns>
    Task<bool> UpdateRequirementAsync(
        PrdDocument document,
        string requirementId,
        Action<PrdRequirement> updates);

    /// <summary>
    /// Add a new requirement to a PRD document
    /// </summary>
    /// <param name="document">PRD document</param>
    /// <param name="requirement">Requirement to add</param>
    /// <returns>True if added successfully</returns>
    Task<bool> AddRequirementAsync(
        PrdDocument document,
        PrdRequirement requirement);

    /// <summary>
    /// Validate a PRD document for completeness and consistency
    /// </summary>
    /// <param name="options">Validation options</param>
    /// <returns>Validation result with any issues found</returns>
    Task<PrdValidationResult> ValidatePrdAsync(PrdValidationOptions options);

    /// <summary>
    /// Find PRD files in a directory
    /// </summary>
    /// <param name="searchPath">Directory to search</param>
    /// <param name="recursive">Whether to search recursively</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of found PRD file paths</returns>
    Task<List<string>> FindPrdFilesAsync(
        string searchPath,
        bool recursive = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate a PRD template for manual editing
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="templateType">Type of template to generate</param>
    /// <param name="outputPath">Output path for the template</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Path to the generated template</returns>
    Task<string> GenerateTemplateAsync(
        string projectName,
        PrdTemplateType templateType = PrdTemplateType.Standard,
        string? outputPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available PRD templates
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available templates</returns>
    Task<List<PrdTemplateInfo>> GetAvailableTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing PRD document
    /// </summary>
    /// <param name="options">Update options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Update result</returns>
    Task<PrdUpdateResult> UpdatePrdAsync(
        PrdUpdateOptions options, 
        CancellationToken cancellationToken = default);
}


/// <summary>
/// Types of PRD templates available
/// </summary>
public enum PrdTemplateType
{
    Standard,      // Standard business PRD
    Technical,     // Technical/API focused PRD
    Mobile,        // Mobile application PRD
    Web,          // Web application PRD
    Api,          // API service PRD
    Minimal,      // Lightweight PRD for small projects
    Enterprise    // Comprehensive enterprise PRD
}