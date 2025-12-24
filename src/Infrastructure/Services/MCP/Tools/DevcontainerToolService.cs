using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS devcontainer management operations
/// This service provides MCP tools for dev container creation, configuration, and management
/// </summary>
public class DevcontainerToolService
{
    private readonly ILogger<DevcontainerToolService> _logger;
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IVsCodeExtensionService _extensionService;

    public DevcontainerToolService(
        ILogger<DevcontainerToolService> logger,
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService,
        IVsCodeExtensionService extensionService)
    {
        _logger = logger;
        _devcontainerService = devcontainerService;
        _featureRegistry = featureRegistry;
        _templateService = templateService;
        _extensionService = extensionService;
    }

    /// <summary>
    /// Initialize a development container configuration
    /// This tool connects to the real PKS devcontainer command functionality
    /// </summary>
    [McpServerTool]
    [Description("Initialize a development container configuration")]
    public async Task<object> InitializeDevcontainerAsync(
        string baseImage = "mcr.microsoft.com/devcontainers/universal:2-linux",
        string[]? features = null,
        string[]? extensions = null,
        string? containerName = null,
        int[]? forwardPorts = null,
        string? postCreateCommand = null)
    {
        _logger.LogInformation("MCP Tool: Initializing devcontainer with base image '{BaseImage}'", baseImage);

        try
        {
            // Validate base image
            if (string.IsNullOrWhiteSpace(baseImage))
            {
                return new
                {
                    success = false,
                    error = "Base image cannot be empty",
                    message = "Please provide a valid base image for the devcontainer"
                };
            }

            // Get available features and extensions
            var availableFeatures = await _featureRegistry.GetAvailableFeaturesAsync();
            var availableExtensions = await _extensionService.GetRecommendedExtensionsAsync(new[] { "general" });

            // Validate requested features
            if (features != null)
            {
                var invalidFeatures = features.Where(f => !availableFeatures.Any(af => af.Id == f)).ToArray();
                if (invalidFeatures.Length > 0)
                {
                    return new
                    {
                        success = false,
                        error = "Invalid features requested",
                        invalidFeatures,
                        availableFeatures = availableFeatures.Select(f => f.Id).ToArray(),
                        message = $"Unknown features: {string.Join(", ", invalidFeatures)}"
                    };
                }
            }

            // Create devcontainer configuration
            var config = new DevcontainerConfiguration
            {
                Name = containerName ?? Path.GetFileName(Environment.CurrentDirectory),
                BaseImage = baseImage,
                Extensions = extensions?.ToList() ?? new List<string>(),
                ForwardPortsList = forwardPorts?.ToList() ?? new List<int>(),
                PostCreateCommand = postCreateCommand
            };

            // Set features (convert from string[] to Dictionary for JSON serialization)
            if (features != null)
            {
                foreach (var feature in features)
                {
                    config.Features[feature] = true; // Default to enabled
                }
            }

            // Initialize devcontainer
            var result = await _devcontainerService.InitializeAsync(config);

            if (result.Success)
            {
                // Get additional information about the created configuration
                var configDetails = await _devcontainerService.GetConfigurationAsync();

                return new
                {
                    success = true,
                    containerName = config.Name,
                    baseImage = config.BaseImage,
                    features = config.Features.Keys.ToArray(),
                    extensions = config.Extensions.ToArray(),
                    forwardPorts = config.ForwardPortsList.ToArray(),
                    postCreateCommand = config.PostCreateCommand,
                    configurationFile = ".devcontainer/devcontainer.json",
                    dockerFile = result.DockerfileCreated ? ".devcontainer/Dockerfile" : null,
                    configDetails,
                    estimatedSize = CalculateEstimatedContainerSize(config),
                    createdAt = DateTime.UtcNow,
                    message = result.Message ?? "Devcontainer initialized successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    containerName = config.Name,
                    baseImage = config.BaseImage,
                    error = result.Message,
                    message = $"Devcontainer initialization failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize devcontainer");
            return new
            {
                success = false,
                baseImage,
                error = ex.Message,
                message = $"Devcontainer initialization failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// List available devcontainer features
    /// </summary>
    [McpServerTool]
    [Description("List available devcontainer features")]
    public async Task<object> ListFeaturesAsync(
        string? category = null,
        bool detailed = false)
    {
        _logger.LogInformation("MCP Tool: Listing devcontainer features, category: '{Category}', detailed: {Detailed}",
            category, detailed);

        try
        {
            var features = await _featureRegistry.GetAvailableFeaturesAsync();

            // Filter by category if specified
            if (!string.IsNullOrWhiteSpace(category))
            {
                features = features.Where(f => f.Category.Contains(category, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var featureList = features.ToArray();

            if (detailed)
            {
                return new
                {
                    success = true,
                    totalFeatures = featureList.Length,
                    category = category ?? "all",
                    features = featureList.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        description = f.Description,
                        version = f.Version,
                        category = f.Category,
                        options = f.Options,
                        documentationUrl = f.DocumentationUrl,
                        maintainer = f.Maintainer,
                        dependencies = f.Dependencies
                    }).ToArray(),
                    message = $"Retrieved {featureList.Length} features"
                };
            }
            else
            {
                return new
                {
                    success = true,
                    totalFeatures = featureList.Length,
                    category = category ?? "all",
                    features = featureList.Select(f => new
                    {
                        id = f.Id,
                        name = f.Name,
                        description = f.Description,
                        category = f.Category
                    }).ToArray(),
                    popularFeatures = featureList
                        .Where(f => f.Category.Contains("popular", StringComparison.OrdinalIgnoreCase))
                        .Select(f => f.Id)
                        .ToArray(),
                    message = $"Retrieved {featureList.Length} features"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list devcontainer features");
            return new
            {
                success = false,
                category,
                error = ex.Message,
                message = $"Failed to retrieve features: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Add features to existing devcontainer configuration
    /// </summary>
    [McpServerTool]
    [Description("Add features to existing devcontainer configuration")]
    public async Task<object> AddFeaturesAsync(
        string[] features,
        bool rebuild = false)
    {
        _logger.LogInformation("MCP Tool: Adding features {Features} to devcontainer, rebuild: {Rebuild}",
            string.Join(", ", features), rebuild);

        try
        {
            // Check if devcontainer exists
            var hasDevcontainer = await _devcontainerService.HasDevcontainerAsync();
            if (!hasDevcontainer)
            {
                return new
                {
                    success = false,
                    error = "No devcontainer configuration found",
                    message = "Please initialize a devcontainer first using pks_devcontainer_init"
                };
            }

            // Validate features
            var availableFeatures = await _featureRegistry.GetAvailableFeaturesAsync();
            var invalidFeatures = features.Where(f => !availableFeatures.Any(af => af.Id == f)).ToArray();

            if (invalidFeatures.Length > 0)
            {
                return new
                {
                    success = false,
                    error = "Invalid features",
                    invalidFeatures,
                    availableFeatures = availableFeatures.Select(f => f.Id).ToArray(),
                    message = $"Unknown features: {string.Join(", ", invalidFeatures)}"
                };
            }

            // Add features
            var addResult = await _devcontainerService.AddFeaturesAsync(features.ToList());

            if (addResult.Success)
            {
                var result = new
                {
                    success = true,
                    addedFeatures = features,
                    featuresCount = features.Length,
                    configurationUpdated = true,
                    rebuild = rebuild,
                    updatedAt = DateTime.UtcNow,
                    message = addResult.Message ?? $"Successfully added {features.Length} features to devcontainer"
                };

                // If rebuild requested, attempt to rebuild
                if (rebuild)
                {
                    var rebuildResult = await _devcontainerService.RebuildAsync();
                    return new
                    {
                        success = result.success,
                        addedFeatures = result.addedFeatures,
                        featuresCount = result.featuresCount,
                        configurationUpdated = result.configurationUpdated,
                        rebuild = result.rebuild,
                        updatedAt = result.updatedAt,
                        rebuildStatus = rebuildResult.Success ? "completed" : "failed",
                        rebuildMessage = rebuildResult.Message,
                        message = rebuildResult.Success
                            ? "Features added and container rebuilt successfully"
                            : "Features added but container rebuild failed"
                    };
                }

                return result;
            }
            else
            {
                return new
                {
                    success = false,
                    features,
                    error = addResult.Message,
                    message = $"Failed to add features: {addResult.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add features to devcontainer");
            return new
            {
                success = false,
                features,
                error = ex.Message,
                message = $"Failed to add features: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get devcontainer status and configuration
    /// </summary>
    [McpServerTool]
    [Description("Get devcontainer status and configuration")]
    public async Task<object> GetStatusAsync(
        bool includeRuntime = false)
    {
        _logger.LogInformation("MCP Tool: Getting devcontainer status, includeRuntime: {IncludeRuntime}", includeRuntime);

        try
        {
            var hasDevcontainer = await _devcontainerService.HasDevcontainerAsync();

            if (!hasDevcontainer)
            {
                return new
                {
                    success = true,
                    hasDevcontainer = false,
                    status = "not-configured",
                    message = "No devcontainer configuration found in current directory"
                };
            }

            var config = await _devcontainerService.GetConfigurationAsync();
            var isRunning = await _devcontainerService.IsRunningAsync();

            var baseResult = new
            {
                success = true,
                hasDevcontainer = true,
                status = isRunning ? "running" : "stopped",
                configuration = new
                {
                    name = config.Name,
                    image = config.Image,
                    features = config.Features.Keys.ToArray(),
                    extensions = config.Extensions.ToArray(),
                    forwardPorts = config.ForwardPortsList.ToArray(),
                    customizations = config.Customizations
                },
                configurationFile = ".devcontainer/devcontainer.json",
                lastModified = File.GetLastWriteTime(".devcontainer/devcontainer.json"),
                isRunning,
                message = isRunning
                    ? "Devcontainer is configured and running"
                    : "Devcontainer is configured but not running"
            };

            if (includeRuntime && isRunning)
            {
                var runtimeInfo = await _devcontainerService.GetRuntimeInfoAsync();
                return new
                {
                    success = baseResult.success,
                    hasDevcontainer = baseResult.hasDevcontainer,
                    status = baseResult.status,
                    configuration = baseResult.configuration,
                    configurationFile = baseResult.configurationFile,
                    lastModified = baseResult.lastModified,
                    isRunning = baseResult.isRunning,
                    message = baseResult.message,
                    runtimeInfo = new
                    {
                        containerId = runtimeInfo.ContainerId,
                        containerName = runtimeInfo.ContainerName,
                        startedAt = runtimeInfo.StartedAt,
                        uptime = runtimeInfo.Uptime,
                        memoryUsage = runtimeInfo.MemoryUsage,
                        cpuUsage = runtimeInfo.CpuUsage,
                        networkPorts = runtimeInfo.NetworkPorts
                    }
                };
            }

            return baseResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get devcontainer status");
            return new
            {
                success = false,
                error = ex.Message,
                message = $"Failed to get devcontainer status: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Rebuild the devcontainer
    /// </summary>
    [McpServerTool]
    [Description("Rebuild the devcontainer")]
    public async Task<object> RebuildAsync(
        bool force = false,
        bool clearCache = false)
    {
        _logger.LogInformation("MCP Tool: Rebuilding devcontainer, force: {Force}, clearCache: {ClearCache}",
            force, clearCache);

        try
        {
            var hasDevcontainer = await _devcontainerService.HasDevcontainerAsync();
            if (!hasDevcontainer)
            {
                return new
                {
                    success = false,
                    error = "No devcontainer configuration found",
                    message = "Please initialize a devcontainer first"
                };
            }

            // Clear cache if requested
            if (clearCache)
            {
                await _devcontainerService.ClearCacheAsync();
            }

            // Perform rebuild
            var rebuildResult = await _devcontainerService.RebuildAsync(force);

            if (rebuildResult.Success)
            {
                var runtimeInfo = await _devcontainerService.GetRuntimeInfoAsync();

                return new
                {
                    success = true,
                    force,
                    clearCache,
                    rebuildDuration = rebuildResult.Duration,
                    containerId = runtimeInfo.ContainerId,
                    containerName = runtimeInfo.ContainerName,
                    startedAt = runtimeInfo.StartedAt,
                    rebuiltAt = DateTime.UtcNow,
                    message = rebuildResult.Message ?? "Devcontainer rebuilt successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    force,
                    clearCache,
                    error = rebuildResult.Message,
                    message = $"Devcontainer rebuild failed: {rebuildResult.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild devcontainer");
            return new
            {
                success = false,
                force,
                clearCache,
                error = ex.Message,
                message = $"Devcontainer rebuild failed: {ex.Message}"
            };
        }
    }

    private string CalculateEstimatedContainerSize(DevcontainerConfiguration config)
    {
        // Simple estimation based on base image and features
        var baseSize = config.BaseImage.ToLower() switch
        {
            var img when img.Contains("alpine") => "50MB",
            var img when img.Contains("ubuntu") => "200MB",
            var img when img.Contains("universal") => "1.2GB",
            var img when img.Contains("dotnet") => "500MB",
            var img when img.Contains("node") => "300MB",
            var img when img.Contains("python") => "400MB",
            _ => "500MB"
        };

        var featureOverhead = config.Features.Count * 50; // Estimate 50MB per feature
        var extensionOverhead = config.Extensions.Count * 10; // Estimate 10MB per extension

        return $"{baseSize} + ~{featureOverhead + extensionOverhead}MB";
    }
}