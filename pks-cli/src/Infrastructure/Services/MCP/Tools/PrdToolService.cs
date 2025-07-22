using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS PRD (Product Requirements Document) operations
/// This service provides MCP tools for PRD generation, management, and validation
/// </summary>
public class PrdToolService
{
    private readonly ILogger<PrdToolService> _logger;
    private readonly IPrdService _prdService;

    public PrdToolService(
        ILogger<PrdToolService> logger,
        IPrdService prdService)
    {
        _logger = logger;
        _prdService = prdService;
    }

    /// <summary>
    /// Generate a Product Requirements Document (PRD)
    /// This tool connects to the real PKS PRD command functionality
    /// </summary>
    [McpServerTool]
    [Description("Generate a Product Requirements Document (PRD)")]
    public async Task<object> GeneratePrdAsync(
        string productName,
        string? description = null,
        string? targetAudience = null,
        string template = "standard",
        bool includeTechnical = true,
        bool includeUserStories = true,
        bool includeAcceptanceCriteria = true)
    {
        _logger.LogInformation("MCP Tool: Generating PRD for product '{ProductName}' with template '{Template}'", 
            productName, template);

        try
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(productName))
            {
                return new
                {
                    success = false,
                    error = "Product name cannot be empty",
                    message = "Please provide a valid product name"
                };
            }

            // Get available templates
            var availableTemplates = await _prdService.GetAvailableTemplatesAsync();
            if (!availableTemplates.Any(t => t.Id == template))
            {
                return new
                {
                    success = false,
                    error = "Invalid template",
                    template,
                    availableTemplates = availableTemplates.Select(t => t.Id).ToArray(),
                    message = $"Template '{template}' not found"
                };
            }

            // Create PRD configuration
            var config = new PKS.Infrastructure.Services.Models.PrdGenerationOptions
            {
                ProductName = productName,
                Description = description ?? $"Product requirements document for {productName}",
                TargetAudience = targetAudience,
                Template = template,
                IncludeTechnicalSpecs = includeTechnical,
                IncludeUserStories = includeUserStories,
                IncludeAcceptanceCriteria = includeAcceptanceCriteria
            };

            // Generate PRD
            // Convert options to generation request
            var request = new PKS.Infrastructure.Services.Models.PrdGenerationRequest
            {
                ProjectName = config.ProductName,
                IdeaDescription = config.Description ?? "",
                TargetAudience = config.TargetAudience ?? ""
            };
            var result = await _prdService.GeneratePrdAsync(request);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    productName,
                    template,
                    description = config.Description,
                    targetAudience = targetAudience ?? "Not specified",
                    features = new
                    {
                        technicalSpecs = includeTechnical,
                        userStories = includeUserStories,
                        acceptanceCriteria = includeAcceptanceCriteria
                    },
                    outputFile = result.OutputFile,
                    sections = result.Sections,
                    wordCount = result.WordCount,
                    estimatedReadTime = result.EstimatedReadTime,
                    generatedAt = DateTime.UtcNow,
                    message = result.Message ?? $"PRD generated successfully for '{productName}'"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    productName,
                    template,
                    error = result.Message,
                    message = $"PRD generation failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate PRD for product '{ProductName}'", productName);
            return new
            {
                success = false,
                productName,
                template,
                error = ex.Message,
                message = $"PRD generation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Load and analyze an existing PRD
    /// </summary>
    [McpServerTool]
    [Description("Load and analyze an existing PRD")]
    public async Task<object> LoadPrdAsync(
        string filePath,
        bool analyze = true,
        bool extractRequirements = true)
    {
        _logger.LogInformation("MCP Tool: Loading PRD from '{FilePath}', analyze: {Analyze}", filePath, analyze);

        try
        {
            // Validate file path
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return new
                {
                    success = false,
                    error = "File path cannot be empty",
                    message = "Please provide a valid file path to the PRD"
                };
            }

            if (!File.Exists(filePath))
            {
                return new
                {
                    success = false,
                    error = "File not found",
                    filePath,
                    message = $"PRD file not found at path: {filePath}"
                };
            }

            // Load PRD
            var document = await _prdService.LoadPrdAsync(filePath);

            if (document != null)
            {
                var baseResponse = new
                {
                    success = true,
                    filePath,
                    fileName = Path.GetFileName(filePath),
                    fileSize = new FileInfo(filePath).Length,
                    lastModified = File.GetLastWriteTime(filePath),
                    productName = document.ProductName,
                    template = document.Template,
                    sections = document.Sections,
                    loadedAt = DateTime.UtcNow,
                    message = document.Message ?? "PRD loaded successfully"
                };

                if (analyze)
                {
                    return new
                    {
                        success = baseResponse.success,
                        filePath = baseResponse.filePath,
                        fileName = baseResponse.fileName,
                        fileSize = baseResponse.fileSize,
                        lastModified = baseResponse.lastModified,
                        productName = baseResponse.productName,
                        template = baseResponse.template,
                        sections = baseResponse.sections,
                        loadedAt = baseResponse.loadedAt,
                        message = baseResponse.message,
                        analysis = new
                        {
                            wordCount = document.Analysis?.WordCount ?? 0,
                            sectionCount = document.Analysis?.SectionCount ?? 0,
                            estimatedReadTime = document.Analysis?.EstimatedReadTime ?? "N/A",
                            completeness = document.Analysis?.Completeness ?? "Unknown",
                            missingElements = document.Analysis?.MissingElements ?? Array.Empty<string>(),
                            recommendations = document.Analysis?.Recommendations ?? Array.Empty<string>()
                        }
                    };
                }

                if (extractRequirements)
                {
                    return new
                    {
                        success = baseResponse.success,
                        filePath = baseResponse.filePath,
                        fileName = baseResponse.fileName,
                        fileSize = baseResponse.fileSize,
                        lastModified = baseResponse.lastModified,
                        productName = baseResponse.productName,
                        template = baseResponse.template,
                        sections = baseResponse.sections,
                        loadedAt = baseResponse.loadedAt,
                        message = baseResponse.message,
                        requirements = new
                        {
                            functional = document.Requirements?.Functional ?? Array.Empty<string>(),
                            nonFunctional = document.Requirements?.NonFunctional ?? Array.Empty<string>(),
                            userStories = document.Requirements?.UserStories ?? Array.Empty<string>(),
                            acceptanceCriteria = document.Requirements?.AcceptanceCriteria ?? Array.Empty<string>(),
                            totalCount = (document.Requirements?.Functional?.Length ?? 0) +
                                       (document.Requirements?.NonFunctional?.Length ?? 0) +
                                       (document.Requirements?.UserStories?.Length ?? 0) +
                                       (document.Requirements?.AcceptanceCriteria?.Length ?? 0)
                        }
                    };
                }

                return baseResponse;
            }
            else
            {
                return new
                {
                    success = false,
                    filePath,
                    error = "Failed to load PRD",
                    message = $"PRD loading failed: Unable to load document"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load PRD from '{FilePath}'", filePath);
            return new
            {
                success = false,
                filePath,
                error = ex.Message,
                message = $"PRD loading failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Validate a PRD for completeness and quality
    /// </summary>
    [McpServerTool]
    [Description("Validate a PRD for completeness and quality")]
    public async Task<object> ValidatePrdAsync(
        string filePath,
        string strictness = "standard",
        bool includeSuggestions = true)
    {
        _logger.LogInformation("MCP Tool: Validating PRD at '{FilePath}' with strictness '{Strictness}'", 
            filePath, strictness);

        try
        {
            if (!File.Exists(filePath))
            {
                return new
                {
                    success = false,
                    error = "File not found",
                    filePath,
                    message = $"PRD file not found at path: {filePath}"
                };
            }

            var validationOptions = new PrdValidationOptions
            {
                FilePath = filePath,
                Strictness = strictness,
                IncludeSuggestions = includeSuggestions
            };

            // Validate PRD
            var result = await _prdService.ValidatePrdAsync(validationOptions);

            if (result.Success)
            {
                var validationResult = new
                {
                    success = true,
                    filePath,
                    fileName = Path.GetFileName(filePath),
                    strictness,
                    isValid = result.IsValid,
                    overallScore = result.OverallScore,
                    validatedAt = DateTime.UtcNow,
                    validation = new
                    {
                        completeness = result.CompletenessScore,
                        clarity = result.ClarityScore,
                        consistency = result.ConsistencyScore,
                        feasibility = result.FeasibilityScore
                    },
                    issues = new
                    {
                        errors = result.Errors?.ToArray() ?? Array.Empty<object>(),
                        warnings = result.Warnings?.ToArray() ?? Array.Empty<object>(),
                        errorCount = result.Errors?.Count() ?? 0,
                        warningCount = result.Warnings?.Count() ?? 0
                    },
                    message = result.Message ?? $"PRD validation completed with score: {result.OverallScore}%"
                };

                if (includeSuggestions && result.Suggestions != null && result.Suggestions.Any())
                {
                    return new
                    {
                        success = validationResult.success,
                        filePath = validationResult.filePath,
                        fileName = validationResult.fileName,
                        strictness = validationResult.strictness,
                        isValid = validationResult.isValid,
                        overallScore = validationResult.overallScore,
                        validatedAt = validationResult.validatedAt,
                        validation = validationResult.validation,
                        issues = validationResult.issues,
                        message = validationResult.message,
                        suggestions = new
                        {
                            improvements = result.Suggestions.Where(s => s.Type == "improvement").Select(s => s.Description).ToArray(),
                            additions = result.Suggestions.Where(s => s.Type == "addition").Select(s => s.Description).ToArray(),
                            restructuring = result.Suggestions.Where(s => s.Type == "restructure").Select(s => s.Description).ToArray(),
                            totalSuggestions = result.Suggestions.Count()
                        }
                    };
                }

                return validationResult;
            }
            else
            {
                return new
                {
                    success = false,
                    filePath,
                    strictness,
                    error = result.Message,
                    message = $"PRD validation failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate PRD at '{FilePath}'", filePath);
            return new
            {
                success = false,
                filePath,
                strictness,
                error = ex.Message,
                message = $"PRD validation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get PRD templates and their information
    /// </summary>
    [McpServerTool]
    [Description("Get available PRD templates and their information")]
    public async Task<object> GetTemplatesAsync(
        bool detailed = false,
        string? category = null)
    {
        _logger.LogInformation("MCP Tool: Getting PRD templates, detailed: {Detailed}, category: {Category}", 
            detailed, category);

        try
        {
            var templates = await _prdService.GetAvailableTemplatesAsync();

            // Filter by category if specified
            if (!string.IsNullOrWhiteSpace(category))
            {
                templates = templates.Where(t => t.Category.Contains(category, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var templateList = templates.ToArray();

            if (detailed)
            {
                return new
                {
                    success = true,
                    totalTemplates = templateList.Length,
                    category = category ?? "all",
                    templates = templateList.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        description = t.Description,
                        category = t.Category,
                        version = "1.0", // Default version
                        sections = new[] { "Overview", "Requirements", "User Stories" }, // Default sections
                        features = new[] { "Basic template features" }, // Default features
                        complexity = t.Complexity,
                        estimatedTime = "10 minutes", // Default estimation
                        author = "PKS CLI", // Default author
                        lastUpdated = t.LastUpdated,
                        isDefault = t.IsDefault,
                        requirements = t.Requirements?.ToArray() ?? Array.Empty<string>()
                    }).ToArray(),
                    message = $"Retrieved {templateList.Length} PRD templates"
                };
            }
            else
            {
                return new
                {
                    success = true,
                    totalTemplates = templateList.Length,
                    category = category ?? "all",
                    templates = templateList.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        description = t.Description,
                        category = t.Category,
                        complexity = t.Complexity,
                        isDefault = t.IsDefault
                    }).ToArray(),
                    categories = templateList.GroupBy(t => t.Category).Select(g => new
                    {
                        category = g.Key,
                        count = g.Count(),
                        templates = g.Select(t => t.Id).ToArray()
                    }).ToArray(),
                    recommendedTemplate = templateList.FirstOrDefault(t => t.IsDefault)?.Id ?? "standard",
                    message = $"Retrieved {templateList.Length} PRD templates"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get PRD templates");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to retrieve PRD templates: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Update an existing PRD with new requirements or sections
    /// </summary>
    [McpServerTool]
    [Description("Update an existing PRD with new requirements or sections")]
    public async Task<object> UpdatePrdAsync(
        string filePath,
        string? section = null,
        string? content = null,
        string mode = "append",
        bool createBackup = true)
    {
        _logger.LogInformation("MCP Tool: Updating PRD at '{FilePath}', section: {Section}, mode: {Mode}", 
            filePath, section, mode);

        try
        {
            if (!File.Exists(filePath))
            {
                return new
                {
                    success = false,
                    error = "File not found",
                    filePath,
                    message = $"PRD file not found at path: {filePath}"
                };
            }

            var updateOptions = new PrdUpdateOptions
            {
                FilePath = filePath,
                Section = section,
                Content = content,
                Mode = mode,
                CreateBackup = createBackup
            };

            // Update PRD
            var result = await _prdService.UpdatePrdAsync(updateOptions);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    filePath,
                    fileName = Path.GetFileName(filePath),
                    section = section ?? "entire document",
                    mode,
                    createBackup,
                    backupFile = result.BackupFile,
                    updatedSections = result.UpdatedSections?.ToArray() ?? Array.Empty<string>(),
                    addedContent = result.AddedContent,
                    updatedAt = DateTime.UtcNow,
                    changesSummary = result.ChangesSummary,
                    message = result.Message ?? "PRD updated successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    filePath,
                    section,
                    mode,
                    error = result.Message,
                    message = $"PRD update failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update PRD at '{FilePath}'", filePath);
            return new
            {
                success = false,
                filePath,
                section,
                mode,
                error = ex.Message,
                message = $"PRD update failed: {ex.Message}"
            };
        }
    }
}