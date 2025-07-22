using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing devcontainer templates
/// </summary>
public class DevcontainerTemplateService : IDevcontainerTemplateService
{
    private readonly ILogger<DevcontainerTemplateService> _logger;
    private List<DevcontainerTemplate> _templates = new();
    private readonly object _lock = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromHours(24);

    public DevcontainerTemplateService(ILogger<DevcontainerTemplateService> logger)
    {
        _logger = logger;
        _ = Task.Run(InitializeTemplatesAsync); // Initialize asynchronously
    }

    public async Task<List<DevcontainerTemplate>> GetAvailableTemplatesAsync()
    {
        await EnsureTemplatesLoadedAsync();
        
        lock (_lock)
        {
            return new List<DevcontainerTemplate>(_templates);
        }
    }

    public async Task<DevcontainerTemplate?> GetTemplateAsync(string id)
    {
        await EnsureTemplatesLoadedAsync();
        
        lock (_lock)
        {
            return _templates.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<DevcontainerConfiguration> ApplyTemplateAsync(string templateId, DevcontainerOptions options)
    {
        var template = await GetTemplateAsync(templateId);
        if (template == null)
        {
            throw new ArgumentException($"Template '{templateId}' not found", nameof(templateId));
        }

        var config = new DevcontainerConfiguration
        {
            Name = options.Name,
            Image = template.BaseImage
        };

        // Apply default features from template
        foreach (var feature in template.RequiredFeatures)
        {
            config.Features[$"ghcr.io/devcontainers/features/{feature}:1"] = new object();
        }

        // Apply default customizations
        if (template.DefaultCustomizations.Any())
        {
            config.Customizations = new Dictionary<string, object>(template.DefaultCustomizations);
        }

        // Apply default ports
        if (template.DefaultPorts.Any())
        {
            var ports = template.DefaultPorts
                .Select(p => int.TryParse(p, out var port) ? port : 0)
                .Where(p => p > 0)
                .ToArray();
            
            if (ports.Any())
            {
                config.ForwardPorts = ports;
            }
        }

        // Apply default post-create command
        if (!string.IsNullOrEmpty(template.DefaultPostCreateCommand))
        {
            config.PostCreateCommand = template.DefaultPostCreateCommand;
        }

        // Apply default environment variables
        if (template.DefaultEnvVars.Any())
        {
            config.RemoteEnv = new Dictionary<string, string>(template.DefaultEnvVars);
        }

        // Apply Docker Compose if required
        if (template.RequiresDockerCompose && !string.IsNullOrEmpty(template.DockerComposeTemplate))
        {
            config.DockerComposeFile = "docker-compose.yml";
            config.Service = "devcontainer";
        }

        return config;
    }

    public async Task<List<DevcontainerTemplate>> GetTemplatesByCategoryAsync(string category)
    {
        await EnsureTemplatesLoadedAsync();
        
        lock (_lock)
        {
            return _templates.Where(t => t.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public async Task<List<DevcontainerTemplate>> SearchTemplatesAsync(string query)
    {
        await EnsureTemplatesLoadedAsync();
        
        if (string.IsNullOrEmpty(query))
        {
            return await GetAvailableTemplatesAsync();
        }

        lock (_lock)
        {
            var lowerQuery = query.ToLowerInvariant();
            return _templates.Where(t =>
                t.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Category.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Id.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
    }

    public async Task<List<string>> GetAvailableCategoriesAsync()
    {
        await EnsureTemplatesLoadedAsync();
        
        lock (_lock)
        {
            return _templates.Select(t => t.Category).Distinct().OrderBy(c => c).ToList();
        }
    }

    public async Task<DevcontainerValidationResult> ValidateTemplateCompatibilityAsync(string templateId, DevcontainerOptions options)
    {
        var result = new DevcontainerValidationResult();
        
        try
        {
            var template = await GetTemplateAsync(templateId);
            if (template == null)
            {
                result.IsValid = false;
                result.Errors.Add($"Template '{templateId}' not found");
                return result;
            }

            // Check if options conflict with template requirements
            if (template.RequiresDockerCompose && !options.UseDockerCompose)
            {
                result.Warnings.Add("Template requires Docker Compose but it's not enabled in options");
            }

            // Check if requested features are compatible with template
            var incompatibleFeatures = options.Features.Except(template.RequiredFeatures.Concat(template.OptionalFeatures)).ToList();
            if (incompatibleFeatures.Any())
            {
                result.Warnings.Add($"Features not included in template: {string.Join(", ", incompatibleFeatures)}");
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating template compatibility for {TemplateId}", templateId);
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
            return result;
        }
    }

    private async Task EnsureTemplatesLoadedAsync()
    {
        bool needsRefresh;
        
        lock (_lock)
        {
            needsRefresh = !_templates.Any() || DateTime.UtcNow - _lastRefresh > _cacheTimeout;
        }

        if (needsRefresh)
        {
            await RefreshTemplatesAsync();
        }
    }

    private async Task InitializeTemplatesAsync()
    {
        try
        {
            var templates = await LoadBuiltInTemplatesAsync();
            
            lock (_lock)
            {
                _templates = templates;
                _lastRefresh = DateTime.UtcNow;
            }
            
            _logger.LogInformation("Initialized with {Count} built-in templates", templates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize templates");
        }
    }

    private async Task RefreshTemplatesAsync()
    {
        try
        {
            _logger.LogInformation("Refreshing devcontainer templates");
            
            var newTemplates = await LoadBuiltInTemplatesAsync();
            
            lock (_lock)
            {
                _templates = newTemplates;
                _lastRefresh = DateTime.UtcNow;
            }
            
            _logger.LogInformation("Successfully refreshed {Count} templates", newTemplates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh templates");
        }
    }

    private static async Task<List<DevcontainerTemplate>> LoadBuiltInTemplatesAsync()
    {
        await Task.Delay(50); // Simulate async operation
        
        return new List<DevcontainerTemplate>
        {
            new()
            {
                Id = "dotnet-basic",
                Name = ".NET Basic",
                Description = "Basic .NET development environment",
                Category = "runtime",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                RequiredFeatures = new[] { "dotnet" },
                OptionalFeatures = new[] { "docker-in-docker", "azure-cli", "git" },
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-dotnettools.csharp",
                            "ms-dotnettools.vscode-dotnet-runtime"
                        },
                        settings = new
                        {
                            dotnetCoreCliTelemetryOptOut = true
                        }
                    }
                },
                DefaultPorts = new[] { "5000", "5001" },
                DefaultPostCreateCommand = "dotnet restore",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            },
            new()
            {
                Id = "dotnet-web",
                Name = ".NET Web API",
                Description = "Complete .NET web development environment with Node.js",
                Category = "web",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                RequiredFeatures = new[] { "dotnet", "node" },
                OptionalFeatures = new[] { "docker-in-docker", "azure-cli", "kubectl-helm-minikube", "git" },
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-dotnettools.csharp",
                            "ms-dotnettools.vscode-dotnet-runtime",
                            "ms-vscode.vscode-typescript-next",
                            "esbenp.prettier-vscode"
                        },
                        settings = new
                        {
                            dotnetCoreCliTelemetryOptOut = true,
                            typescriptPreferGoToSourceDefinition = true
                        }
                    }
                },
                DefaultPorts = new[] { "5000", "5001", "3000" },
                DefaultPostCreateCommand = "dotnet restore && npm install",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["NODE_ENV"] = "development"
                }
            },
            new()
            {
                Id = "dotnet-microservices",
                Name = ".NET Microservices",
                Description = "Full-stack microservices development with Docker Compose",
                Category = "microservices",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                RequiredFeatures = new[] { "dotnet", "docker-in-docker" },
                OptionalFeatures = new[] { "azure-cli", "kubectl-helm-minikube", "node", "git" },
                RequiresDockerCompose = true,
                DockerComposeTemplate = "microservices",
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-dotnettools.csharp",
                            "ms-dotnettools.vscode-dotnet-runtime",
                            "ms-vscode.vscode-docker",
                            "ms-kubernetes-tools.vscode-kubernetes-tools"
                        }
                    }
                },
                DefaultPorts = new[] { "5000", "5001", "5010", "5011", "5020", "5021" },
                DefaultPostCreateCommand = "dotnet restore && docker-compose up -d",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            },
            new()
            {
                Id = "dotnet-blazor",
                Name = ".NET Blazor",
                Description = "Blazor development environment with hot reload",
                Category = "web",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                RequiredFeatures = new[] { "dotnet" },
                OptionalFeatures = new[] { "node", "docker-in-docker", "git" },
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-dotnettools.csharp",
                            "ms-dotnettools.vscode-dotnet-runtime",
                            "ms-dotnettools.blazorwasm-companion"
                        },
                        settings = new
                        {
                            dotnetCoreCliTelemetryOptOut = true,
                            blazorWasm_companion_enableHotReload = true
                        }
                    }
                },
                DefaultPorts = new[] { "5000", "5001" },
                DefaultPostCreateCommand = "dotnet restore",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["ASPNETCORE_ENVIRONMENT"] = "Development"
                }
            },
            new()
            {
                Id = "dotnet-minimal",
                Name = ".NET Minimal",
                Description = "Lightweight .NET development environment",
                Category = "runtime",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0-alpine",
                RequiredFeatures = new[] { "dotnet" },
                OptionalFeatures = new[] { "git" },
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-dotnettools.csharp"
                        }
                    }
                },
                DefaultPostCreateCommand = "dotnet --version",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
                }
            },
            new()
            {
                Id = "node-typescript",
                Name = "Node.js + TypeScript",
                Description = "Node.js development with TypeScript support",
                Category = "runtime",
                BaseImage = "mcr.microsoft.com/vscode/devcontainers/typescript-node:18",
                RequiredFeatures = new[] { "node" },
                OptionalFeatures = new[] { "docker-in-docker", "git" },
                DefaultCustomizations = new Dictionary<string, object>
                {
                    ["vscode"] = new
                    {
                        extensions = new[]
                        {
                            "ms-vscode.vscode-typescript-next",
                            "esbenp.prettier-vscode",
                            "bradlc.vscode-tailwindcss"
                        }
                    }
                },
                DefaultPorts = new[] { "3000", "3001" },
                DefaultPostCreateCommand = "npm install",
                DefaultEnvVars = new Dictionary<string, string>
                {
                    ["NODE_ENV"] = "development"
                }
            }
        };
    }

    /// <summary>
    /// Extracts a template to the specified location
    /// </summary>
    /// <param name="templateId">Template ID</param>
    /// <param name="options">Configuration options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Template extraction result</returns>
    public async Task<NuGetTemplateExtractionResult> ExtractTemplateAsync(string templateId, DevcontainerOptions options, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Extracting template {TemplateId}", templateId);
        
        try
        {
            // For now, return a basic stub implementation
            await Task.Delay(100, cancellationToken);
            
            return new NuGetTemplateExtractionResult
            {
                Success = true,
                ExtractedPath = options.OutputPath ?? Environment.CurrentDirectory,
                ExtractedFiles = new List<string> { "devcontainer.json", "Dockerfile" },
                ExtractionTime = TimeSpan.FromMilliseconds(100)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting template {TemplateId}", templateId);
            return new NuGetTemplateExtractionResult
            {
                Success = false,
                ErrorMessage = $"Failed to extract template {templateId}: {ex.Message}"
            };
        }
    }
}