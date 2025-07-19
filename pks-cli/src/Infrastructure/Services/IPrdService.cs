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
    /// <param name="outputPath">Optional output path for the generated PRD file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Generated PRD document</returns>
    Task<PrdDocument> GeneratePrdAsync(
        PrdGenerationRequest request, 
        string? outputPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Load and parse an existing PRD file
    /// </summary>
    /// <param name="filePath">Path to the PRD file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Parsing result with PRD document</returns>
    Task<PrdParsingResult> LoadPrdAsync(
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
    /// <param name="document">PRD document to validate</param>
    /// <returns>Validation result with any issues found</returns>
    Task<PrdValidationResult> ValidatePrdAsync(PrdDocument document);

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
}

/// <summary>
/// PRD validation result
/// </summary>
public class PrdValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public double CompletenessScore { get; set; } // 0-100
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