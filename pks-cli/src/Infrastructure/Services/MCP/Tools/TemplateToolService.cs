using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.Infrastructure.Services;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS template management operations
/// This service provides MCP tools for template discovery, management, and operations
/// </summary>
public class TemplateToolService
{
    private readonly ILogger<TemplateToolService> _logger;
    private readonly INuGetTemplateDiscoveryService _templateDiscoveryService;

    public TemplateToolService(
        ILogger<TemplateToolService> logger,
        INuGetTemplateDiscoveryService templateDiscoveryService)
    {
        _logger = logger;
        _templateDiscoveryService = templateDiscoveryService;
    }

    /// <summary>
    /// Search and list available NuGet templates
    /// This tool connects to the real PKS template discovery functionality
    /// </summary>
    [McpServerTool]
    [Description("Search and list available NuGet templates")]
    public async Task<object> SearchTemplatesAsync(
        string? searchQuery = null,
        string? category = null,
        string? language = null,
        bool includePreRelease = false,
        int maxResults = 20)
    {
        _logger.LogInformation("MCP Tool: Searching templates, query: '{SearchQuery}', category: '{Category}', language: '{Language}', maxResults: {MaxResults}", 
            searchQuery, category, language, maxResults);

        try
        {
            // Search templates using the service method directly
            var searchResults = await _templateDiscoveryService.SearchTemplatesAsync(
                searchQuery ?? "",
                "pks-devcontainers",
                null,
                maxResults);

            if (searchResults != null && searchResults.Any())
            {
                var templates = searchResults.ToArray();
                
                return new
                {
                    success = true,
                    searchQuery = searchQuery ?? "all templates",
                    category = category ?? "all categories",
                    language = language ?? "all languages",
                    includePreRelease,
                    maxResults,
                    totalFound = templates.Length,
                    templates = templates.Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        description = t.Description,
                        version = t.Version,
                        authors = t.Authors,
                        categories = t.Categories,
                        languages = t.Languages,
                        projectUrl = t.ProjectUrl,
                        packageUrl = t.PackageUrl,
                        downloadCount = t.DownloadCount,
                        isPreRelease = t.IsPreRelease,
                        publishedDate = t.PublishedDate,
                        tags = t.Tags
                    }).ToArray(),
                    categoryDistribution = templates.GroupBy(t => t.Categories.FirstOrDefault() ?? "Other").Select(g => new
                    {
                        category = g.Key,
                        count = g.Count()
                    }).OrderByDescending(x => x.count).ToArray(),
                    languageDistribution = templates.SelectMany(t => t.Languages).GroupBy(l => l).Select(g => new
                    {
                        language = g.Key,
                        count = g.Count()
                    }).OrderByDescending(x => x.count).ToArray(),
                    searchedAt = DateTime.UtcNow,
                    message = $"Found {templates.Length} templates matching the criteria"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    searchQuery,
                    category,
                    language,
                    error = "No templates found",
                    message = "No templates found matching the search criteria"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search templates");
            return new
            {
                success = false,
                searchQuery,
                category,
                language,
                error = ex.Message,
                message = $"Template search failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get detailed information about a specific template
    /// </summary>
    [McpServerTool]
    [Description("Get detailed information about a specific template")]
    public async Task<object> GetTemplateInfoAsync(
        string templateId,
        bool includeInstructions = true,
        bool includeOptions = true)
    {
        _logger.LogInformation("MCP Tool: Getting template info for '{TemplateId}', includeInstructions: {IncludeInstructions}, includeOptions: {IncludeOptions}", 
            templateId, includeInstructions, includeOptions);

        try
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return new
                {
                    success = false,
                    error = "Template ID cannot be empty",
                    message = "Please provide a valid template ID or name"
                };
            }

            // Get template details - get latest version first
            var latestVersion = await _templateDiscoveryService.GetLatestVersionAsync(templateId, false);
            if (string.IsNullOrEmpty(latestVersion))
            {
                return new
                {
                    success = false,
                    error = "Template not found",
                    message = $"Could not find template with ID: {templateId}"
                };
            }
            
            var result = await _templateDiscoveryService.GetTemplateDetailsAsync(templateId, latestVersion);

            if (result != null)
            {
                var template = result;
                
                var baseInfo = new
                {
                    success = true,
                    templateId,
                    id = template.Id,
                    title = template.Title,
                    description = template.Description,
                    version = template.Version,
                    latestVersion = template.LatestVersion,
                    authors = template.Authors,
                    categories = template.Categories,
                    languages = template.Languages,
                    tags = template.Tags,
                    projectUrl = template.ProjectUrl,
                    packageUrl = template.PackageUrl,
                    repositoryUrl = template.RepositoryUrl,
                    downloadCount = template.DownloadCount,
                    isPreRelease = template.IsPreRelease,
                    publishedDate = template.PublishedDate,
                    lastUpdated = template.LastUpdated,
                    license = template.License,
                    requiresLicenseAcceptance = template.RequiresLicenseAcceptance,
                    retrievedAt = DateTime.UtcNow,
                    message = $"Template information retrieved for {templateId}"
                };

                var responseData = new Dictionary<string, object>();
                
                // Copy base properties
                foreach (var prop in baseInfo.GetType().GetProperties())
                {
                    responseData[prop.Name] = prop.GetValue(baseInfo) ?? "";
                }

                if (includeInstructions)
                {
                    responseData["installation"] = new
                    {
                        dotnetNewCommand = $"dotnet new install {template.Id}",
                        usage = template.Usage ?? $"dotnet new {GetShortName(template.Id)} -n MyProject",
                        prerequisites = template.Prerequisites ?? new[] { ".NET 8.0 SDK" },
                        installationSteps = new[]
                        {
                            $"Install template: dotnet new install {template.Id}",
                            "List available options: dotnet new {shortname} --help",
                            "Create project: dotnet new {shortname} -n YourProjectName"
                        },
                        uninstallCommand = $"dotnet new uninstall {template.Id}"
                    };
                }

                if (includeOptions && template.Options != null && template.Options.Any())
                {
                    responseData["options"] = new
                    {
                        totalOptions = template.Options.Count(),
                        availableOptions = template.Options.Select(o => new
                        {
                            name = o.Name,
                            description = o.Description,
                            type = o.Type,
                            defaultValue = o.DefaultValue,
                            choices = o.Choices,
                            isRequired = o.IsRequired,
                            displayName = o.DisplayName
                        }).ToArray(),
                        exampleUsage = $"dotnet new {GetShortName(template.Id)} -n MyProject"
                    };
                }

                // Add related templates if available
                if (template.RelatedTemplates != null && template.RelatedTemplates.Any())
                {
                    responseData["relatedTemplates"] = template.RelatedTemplates.Select(rt => new
                    {
                        id = rt.Id,
                        title = rt.Title,
                        description = rt.Description,
                        relationship = rt.Relationship // "similar", "extends", "part-of", etc.
                    }).ToArray();
                }

                return responseData;
            }
            else
            {
                return new
                {
                    success = false,
                    templateId,
                    error = "Template not found",
                    message = $"Template '{templateId}' not found or could not be retrieved"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template info for '{TemplateId}'", templateId);
            return new
            {
                success = false,
                templateId,
                error = ex.Message,
                message = $"Failed to get template information: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Install a template from NuGet
    /// </summary>
    [McpServerTool]
    [Description("Install a template from NuGet")]
    public async Task<object> InstallTemplateAsync(
        string templateId,
        string? version = null,
        bool force = false)
    {
        _logger.LogInformation("MCP Tool: Installing template '{TemplateId}', version: '{Version}', force: {Force}", 
            templateId, version, force);

        try
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return new
                {
                    success = false,
                    error = "Template ID cannot be empty",
                    message = "Please provide a valid template ID or name"
                };
            }

            // Check if template is already installed
            var installedTemplates = await _templateDiscoveryService.GetInstalledTemplatesAsync();
            var existingTemplate = installedTemplates.FirstOrDefault(t => 
                t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase) ||
                t.Title.Equals(templateId, StringComparison.OrdinalIgnoreCase));

            if (existingTemplate != null && !force)
            {
                return new
                {
                    success = true,
                    alreadyInstalled = true,
                    templateId,
                    installedVersion = existingTemplate.Version,
                    installPath = existingTemplate.InstallPath,
                    message = $"Template '{templateId}' is already installed (version {existingTemplate.Version}). Use force=true to reinstall."
                };
            }

            // Install template
            var result = await _templateDiscoveryService.InstallTemplateAsync(templateId, version);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    alreadyInstalled = false,
                    templateId,
                    installedVersion = version ?? "latest",
                    installPath = result.ExtractedPath,
                    force,
                    shortNames = new[] { templateId.Split('.').Last() },
                    installationSize = result.TotalSize,
                    installedAt = DateTime.UtcNow,
                    usage = new
                    {
                        createProject = $"dotnet new {templateId.Split('.').Last()} -n YourProjectName",
                        listOptions = $"dotnet new {templateId.Split('.').Last()} --help",
                        uninstall = $"dotnet new uninstall {templateId}"
                    },
                    message = result.Message ?? $"Template '{templateId}' installed successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    templateId,
                    version,
                    force,
                    error = result.ErrorMessage ?? "Installation failed",
                    message = $"Template installation failed: {result.ErrorMessage ?? "Unknown error"}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install template '{TemplateId}'", templateId);
            return new
            {
                success = false,
                templateId,
                version,
                force,
                error = ex.Message,
                message = $"Template installation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// List installed templates
    /// </summary>
    [McpServerTool]
    [Description("List installed templates")]
    public async Task<object> ListInstalledTemplatesAsync(
        bool includeDetails = false,
        string? language = null)
    {
        _logger.LogInformation("MCP Tool: Listing installed templates, includeDetails: {IncludeDetails}, language: '{Language}'", 
            includeDetails, language);

        try
        {
            var installedTemplates = await _templateDiscoveryService.GetInstalledTemplatesAsync();

            // Filter by language if specified
            if (!string.IsNullOrWhiteSpace(language))
            {
                installedTemplates = installedTemplates.Where(t => 
                    t.Languages.Any(l => l.Contains(language, StringComparison.OrdinalIgnoreCase))).ToList();
            }

            var templates = installedTemplates.ToArray();

            if (includeDetails)
            {
                return new
                {
                    success = true,
                    totalInstalled = templates.Length,
                    language = language ?? "all languages",
                    templates = templates.Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        description = t.Description,
                        version = t.Version,
                        shortNames = t.ShortNames,
                        languages = t.Languages,
                        categories = t.Categories,
                        installPath = t.InstallPath,
                        installedDate = t.InstalledDate,
                        source = t.Source,
                        authors = t.Authors,
                        tags = t.Tags,
                        usage = $"dotnet new {t.ShortNames?.FirstOrDefault()} -n YourProjectName"
                    }).ToArray(),
                    message = $"Retrieved details for {templates.Length} installed templates"
                };
            }
            else
            {
                return new
                {
                    success = true,
                    totalInstalled = templates.Length,
                    language = language ?? "all languages",
                    templates = templates.Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        shortNames = t.ShortNames,
                        languages = t.Languages,
                        version = t.Version
                    }).ToArray(),
                    languageDistribution = templates.SelectMany(t => t.Languages).GroupBy(l => l).Select(g => new
                    {
                        language = g.Key,
                        count = g.Count()
                    }).OrderByDescending(x => x.count).ToArray(),
                    categoryDistribution = templates.SelectMany(t => t.Categories).GroupBy(c => c).Select(g => new
                    {
                        category = g.Key,
                        count = g.Count()
                    }).OrderByDescending(x => x.count).ToArray(),
                    message = $"Found {templates.Length} installed templates"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list installed templates");
            return new
            {
                success = false,
                language,
                error = ex.Message,
                message = $"Failed to list installed templates: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Uninstall a template
    /// </summary>
    [McpServerTool]
    [Description("Uninstall a template")]
    public async Task<object> UninstallTemplateAsync(
        string templateId)
    {
        _logger.LogInformation("MCP Tool: Uninstalling template '{TemplateId}'", templateId);

        try
        {
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return new
                {
                    success = false,
                    error = "Template ID cannot be empty",
                    message = "Please provide a valid template ID or name"
                };
            }

            // Check if template is installed
            var installedTemplates = await _templateDiscoveryService.GetInstalledTemplatesAsync();
            var templateToUninstall = installedTemplates.FirstOrDefault(t => 
                t.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase) ||
                t.Title.Equals(templateId, StringComparison.OrdinalIgnoreCase) ||
                t.ShortNames?.Any(sn => sn.Equals(templateId, StringComparison.OrdinalIgnoreCase)) == true);

            if (templateToUninstall == null)
            {
                return new
                {
                    success = false,
                    templateId,
                    error = "Template not installed",
                    installedTemplates = installedTemplates.Select(t => new { t.Id, t.Title }).Take(10).ToArray(),
                    message = $"Template '{templateId}' is not currently installed"
                };
            }

            // Uninstall template
            var result = await _templateDiscoveryService.UninstallTemplateAsync(templateToUninstall.Id);

            if (result)
            {
                return new
                {
                    success = true,
                    templateId,
                    uninstalledId = templateToUninstall.Id,
                    uninstalledTitle = templateToUninstall.Title,
                    uninstalledVersion = templateToUninstall.Version,
                    shortNames = templateToUninstall.ShortNames,
                    uninstalledAt = DateTime.UtcNow,
                    message = $"Template '{templateToUninstall.Title}' uninstalled successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    templateId,
                    templateTitle = templateToUninstall.Title,
                    error = "Uninstallation failed",
                    message = "Template uninstallation failed"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall template '{TemplateId}'", templateId);
            return new
            {
                success = false,
                templateId,
                error = ex.Message,
                message = $"Template uninstallation failed: {ex.Message}"
            };
        }
    }

    // Helper methods

    private string GetShortName(string templateId)
    {
        // Extract a reasonable short name from template ID
        // This is a simplified version - real implementation would be more sophisticated
        var parts = templateId.Split('.', '-', '_');
        return parts.LastOrDefault()?.ToLowerInvariant() ?? "template";
    }

}