using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Core service for devcontainer operations
/// </summary>
public class DevcontainerService : IDevcontainerService
{
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerFileGenerator _fileGenerator;
    private readonly IVsCodeExtensionService _extensionService;
    private readonly ILogger<DevcontainerService> _logger;

    public DevcontainerService(
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService,
        IDevcontainerFileGenerator fileGenerator,
        IVsCodeExtensionService extensionService,
        ILogger<DevcontainerService> logger)
    {
        _featureRegistry = featureRegistry;
        _templateService = templateService;
        _fileGenerator = fileGenerator;
        _extensionService = extensionService;
        _logger = logger;
    }

    public async Task<DevcontainerResult> CreateConfigurationAsync(DevcontainerOptions options)
    {
        var startTime = DateTime.UtcNow;
        var result = new DevcontainerResult();

        try
        {
            _logger.LogInformation("Creating devcontainer configuration for {Name}", options.Name);

            // Validate input options
            var optionsValidation = await ValidateOptionsAsync(options);
            if (!optionsValidation.IsValid)
            {
                result.Success = false;
                result.Errors.AddRange(optionsValidation.Errors);
                result.Message = "Invalid configuration options";
                return result;
            }

            // Validate output path
            var pathValidation = await ValidateOutputPathAsync(options.OutputPath);
            if (!pathValidation.IsValid)
            {
                result.Success = false;
                result.Errors.AddRange(pathValidation.Errors);
                result.Message = "Invalid output path";
                return result;
            }

            // Start with base configuration
            DevcontainerConfiguration config;
            
            if (!string.IsNullOrEmpty(options.Template))
            {
                // Apply template
                config = await _templateService.ApplyTemplateAsync(options.Template, options);
                _logger.LogDebug("Applied template {Template}", options.Template);
            }
            else
            {
                // Create basic configuration
                config = CreateBasicConfiguration(options);
                _logger.LogDebug("Created basic configuration");
            }

            // Resolve and add features
            if (options.Features.Any())
            {
                var featureResolution = await ResolveFeatureDependenciesAsync(options.Features);
                if (!featureResolution.Success)
                {
                    result.Success = false;
                    result.Errors.Add(featureResolution.ErrorMessage);
                    result.Message = "Feature dependency resolution failed";
                    return result;
                }

                // Add resolved features to configuration
                foreach (var feature in featureResolution.ResolvedFeatures)
                {
                    var featureKey = $"{feature.Repository}:{feature.Version}";
                    config.Features[featureKey] = feature.DefaultOptions;
                }

                _logger.LogDebug("Added {Count} features", featureResolution.ResolvedFeatures.Count);
            }

            // Add recommended extensions based on features
            var recommendedExtensions = await GetRecommendedExtensionsAsync(options.Features);
            var allExtensions = options.Extensions.Union(recommendedExtensions.Select(e => e.Id)).ToList();

            if (allExtensions.Any())
            {
                config.Customizations["vscode"] = new
                {
                    extensions = allExtensions.ToArray()
                };
                _logger.LogDebug("Added {Count} extensions", allExtensions.Count);
            }

            // Apply custom settings
            ApplyCustomSettings(config, options);

            // Validate final configuration
            var configValidation = await ValidateConfigurationAsync(config);
            if (!configValidation.IsValid)
            {
                result.Warnings.AddRange(configValidation.Warnings);
                if (configValidation.Severity >= ValidationSeverity.Error)
                {
                    result.Success = false;
                    result.Errors.AddRange(configValidation.Errors);
                    result.Message = "Configuration validation failed";
                    return result;
                }
            }

            // Generate files
            var generationResults = await _fileGenerator.GenerateAllFilesAsync(config, options.OutputPath, options);
            var failedGenerations = generationResults.Where(r => !r.Success).ToList();

            if (failedGenerations.Any())
            {
                result.Success = false;
                result.Errors.AddRange(failedGenerations.Select(f => f.ErrorMessage));
                result.Message = "File generation failed";
                return result;
            }

            result.Success = true;
            result.Configuration = config;
            result.GeneratedFiles = generationResults.Select(r => r.FilePath).ToList();
            result.Message = "Devcontainer configuration created successfully";
            result.Duration = DateTime.UtcNow - startTime;

            _logger.LogInformation("Successfully created devcontainer configuration in {Duration}ms", 
                result.Duration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create devcontainer configuration");
            result.Success = false;
            result.Message = "An unexpected error occurred";
            result.Errors.Add(ex.Message);
            result.Duration = DateTime.UtcNow - startTime;
            return result;
        }
    }

    public async Task<DevcontainerValidationResult> ValidateConfigurationAsync(DevcontainerConfiguration configuration)
    {
        var result = new DevcontainerValidationResult();
        var errors = new List<string>();
        var warnings = new List<string>();

        try
        {
            // Validate required fields
            if (string.IsNullOrEmpty(configuration.Name))
            {
                errors.Add("Name is required");
            }
            else if (!IsValidName(configuration.Name))
            {
                errors.Add("Name contains invalid characters");
            }

            if (string.IsNullOrEmpty(configuration.Image) && configuration.Build == null)
            {
                errors.Add("Either image or build configuration is required");
            }

            if (!string.IsNullOrEmpty(configuration.Image) && !IsValidImageName(configuration.Image))
            {
                errors.Add("Image name is not valid");
            }

            // Validate features
            foreach (var feature in configuration.Features)
            {
                if (string.IsNullOrEmpty(feature.Key))
                {
                    errors.Add("Feature name cannot be empty");
                    continue;
                }

                var featureValidation = await _featureRegistry.ValidateFeatureConfiguration(feature.Key, feature.Value);
                if (!featureValidation.IsValid)
                {
                    errors.AddRange(featureValidation.Errors.Select(e => $"Feature '{feature.Key}': {e}"));
                }
            }

            // Validate ports
            foreach (var port in configuration.ForwardPorts)
            {
                if (port <= 0 || port > 65535)
                {
                    errors.Add($"Invalid port number: {port}");
                }
            }

            // Validate environment variables
            foreach (var env in configuration.RemoteEnv)
            {
                if (string.IsNullOrEmpty(env.Key))
                {
                    errors.Add("Environment variable name cannot be empty");
                }
            }

            // Validate build configuration if present
            if (configuration.Build != null)
            {
                if (string.IsNullOrEmpty(configuration.Build.Dockerfile) && string.IsNullOrEmpty(configuration.Build.Context))
                {
                    errors.Add("Build configuration requires either dockerfile or context");
                }
            }

            // Check for common issues
            if (configuration.Features.Count == 0)
            {
                warnings.Add("No features specified - consider adding relevant features for your development environment");
            }

            if (configuration.ForwardPorts.Length == 0)
            {
                warnings.Add("No ports forwarded - you may need to add port forwarding for your application");
            }

            // Determine severity
            result.Severity = errors.Any() ? ValidationSeverity.Error : 
                             warnings.Any() ? ValidationSeverity.Warning : 
                             ValidationSeverity.None;

            result.IsValid = !errors.Any();
            result.Errors = errors;
            result.Warnings = warnings;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during configuration validation");
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
            result.Severity = ValidationSeverity.Critical;
            return result;
        }
    }

    public async Task<FeatureResolutionResult> ResolveFeatureDependenciesAsync(List<string> features)
    {
        var result = new FeatureResolutionResult();

        try
        {
            var resolvedFeatures = new List<DevcontainerFeature>();
            var allFeaturesToResolve = new HashSet<string>(features);
            var conflicts = new List<FeatureConflict>();

            // Get all available features for dependency resolution
            var availableFeatures = await _featureRegistry.GetAvailableFeaturesAsync();
            var featureMap = availableFeatures.ToDictionary(f => f.Id, f => f);

            // Resolve dependencies recursively
            while (allFeaturesToResolve.Any())
            {
                var currentFeature = allFeaturesToResolve.First();
                allFeaturesToResolve.Remove(currentFeature);

                if (!featureMap.TryGetValue(currentFeature, out var feature))
                {
                    result.MissingDependencies.Add(currentFeature);
                    continue;
                }

                resolvedFeatures.Add(feature);

                // Add dependencies
                foreach (var dependency in feature.Dependencies)
                {
                    if (!resolvedFeatures.Any(f => f.Id == dependency) && !allFeaturesToResolve.Contains(dependency))
                    {
                        allFeaturesToResolve.Add(dependency);
                        result.ResolvedDependencies.Add(dependency);
                    }
                }
            }

            // Check for conflicts
            foreach (var feature in resolvedFeatures)
            {
                foreach (var conflictsWith in feature.ConflictsWith)
                {
                    var conflictingFeature = resolvedFeatures.FirstOrDefault(f => f.Id == conflictsWith);
                    if (conflictingFeature != null)
                    {
                        conflicts.Add(new FeatureConflict
                        {
                            Feature1 = feature.Id,
                            Feature2 = conflictingFeature.Id,
                            Reason = $"Feature '{feature.Id}' conflicts with '{conflictingFeature.Id}'",
                            Severity = ConflictSeverity.Error
                        });
                    }
                }
            }

            // Check for version conflicts (same feature, different versions)
            var featureGroups = resolvedFeatures.GroupBy(f => f.Id.Split(':')[0]).Where(g => g.Count() > 1);
            foreach (var group in featureGroups)
            {
                var featureList = group.ToList();
                for (int i = 0; i < featureList.Count - 1; i++)
                {
                    for (int j = i + 1; j < featureList.Count; j++)
                    {
                        conflicts.Add(new FeatureConflict
                        {
                            Feature1 = featureList[i].Id,
                            Feature2 = featureList[j].Id,
                            Reason = "Multiple versions of the same feature",
                            Severity = ConflictSeverity.Error,
                            Resolution = $"Choose either {featureList[i].Id} or {featureList[j].Id}"
                        });
                    }
                }
            }

            result.Success = !conflicts.Any(c => c.Severity >= ConflictSeverity.Error) && !result.MissingDependencies.Any();
            result.ResolvedFeatures = resolvedFeatures;
            result.ConflictingFeatures = conflicts;

            if (!result.Success)
            {
                result.ErrorMessage = conflicts.Any() ? "Feature conflicts detected" : "Missing dependencies";
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during feature dependency resolution");
            result.Success = false;
            result.ErrorMessage = $"Dependency resolution error: {ex.Message}";
            return result;
        }
    }

    public async Task<DevcontainerConfiguration> MergeConfigurationsAsync(DevcontainerConfiguration baseConfig, DevcontainerConfiguration overlayConfig)
    {
        try
        {
            var merged = new DevcontainerConfiguration
            {
                Name = !string.IsNullOrEmpty(overlayConfig.Name) ? overlayConfig.Name : baseConfig.Name,
                Image = !string.IsNullOrEmpty(overlayConfig.Image) ? overlayConfig.Image : baseConfig.Image,
                WorkspaceFolder = overlayConfig.WorkspaceFolder ?? baseConfig.WorkspaceFolder,
                PostCreateCommand = !string.IsNullOrEmpty(overlayConfig.PostCreateCommand) ? overlayConfig.PostCreateCommand : baseConfig.PostCreateCommand,
                Build = overlayConfig.Build ?? baseConfig.Build,
                DockerComposeFile = overlayConfig.DockerComposeFile ?? baseConfig.DockerComposeFile,
                Service = overlayConfig.Service ?? baseConfig.Service
            };

            // Merge features (overlay takes precedence)
            merged.Features = new Dictionary<string, object>(baseConfig.Features);
            foreach (var feature in overlayConfig.Features)
            {
                merged.Features[feature.Key] = feature.Value;
            }

            // Merge customizations
            merged.Customizations = new Dictionary<string, object>(baseConfig.Customizations);
            foreach (var customization in overlayConfig.Customizations)
            {
                merged.Customizations[customization.Key] = customization.Value;
            }

            // Merge environment variables
            merged.RemoteEnv = new Dictionary<string, string>(baseConfig.RemoteEnv);
            foreach (var env in overlayConfig.RemoteEnv)
            {
                merged.RemoteEnv[env.Key] = env.Value;
            }

            // Merge container environment
            if (baseConfig.ContainerEnv != null || overlayConfig.ContainerEnv != null)
            {
                merged.ContainerEnv = new Dictionary<string, string>(baseConfig.ContainerEnv ?? new());
                if (overlayConfig.ContainerEnv != null)
                {
                    foreach (var env in overlayConfig.ContainerEnv)
                    {
                        merged.ContainerEnv[env.Key] = env.Value;
                    }
                }
            }

            // Merge arrays (combine unique values)
            merged.ForwardPorts = baseConfig.ForwardPorts.Union(overlayConfig.ForwardPorts).Distinct().ToArray();
            merged.Mounts = baseConfig.Mounts.Union(overlayConfig.Mounts).Distinct().ToArray();
            
            if (baseConfig.RunArgs != null || overlayConfig.RunArgs != null)
            {
                merged.RunArgs = (baseConfig.RunArgs ?? Array.Empty<string>())
                    .Union(overlayConfig.RunArgs ?? Array.Empty<string>())
                    .Distinct()
                    .ToArray();
            }

            if (baseConfig.RunServices != null || overlayConfig.RunServices != null)
            {
                merged.RunServices = (baseConfig.RunServices ?? Array.Empty<string>())
                    .Union(overlayConfig.RunServices ?? Array.Empty<string>())
                    .Distinct()
                    .ToArray();
            }

            return merged;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during configuration merge");
            throw new InvalidOperationException($"Failed to merge configurations: {ex.Message}", ex);
        }
    }

    public async Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(List<string> features)
    {
        try
        {
            if (!features.Any())
            {
                return new List<VsCodeExtension>();
            }

            return await _extensionService.GetEssentialExtensionsAsync(features);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recommended extensions");
            return new List<VsCodeExtension>();
        }
    }

    public async Task<DevcontainerResult> UpdateConfigurationAsync(string configPath, DevcontainerOptions updates)
    {
        var result = new DevcontainerResult();

        try
        {
            // Read existing configuration
            if (!File.Exists(configPath))
            {
                result.Success = false;
                result.Message = "Configuration file not found";
                result.Errors.Add($"File not found: {configPath}");
                return result;
            }

            var existingContent = await File.ReadAllTextAsync(configPath);
            var existingConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(existingContent);

            if (existingConfig == null)
            {
                result.Success = false;
                result.Message = "Failed to parse existing configuration";
                result.Errors.Add("Invalid JSON in configuration file");
                return result;
            }

            // Create updated configuration
            var updatedConfig = await CreateUpdatedConfigurationAsync(existingConfig, updates);

            // Validate updated configuration
            var validation = await ValidateConfigurationAsync(updatedConfig);
            if (!validation.IsValid)
            {
                result.Success = false;
                result.Message = "Updated configuration is invalid";
                result.Errors.AddRange(validation.Errors);
                return result;
            }

            // Write updated configuration
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var updatedContent = JsonSerializer.Serialize(updatedConfig, jsonOptions);
            await File.WriteAllTextAsync(configPath, updatedContent);

            result.Success = true;
            result.Configuration = updatedConfig;
            result.Message = "Configuration updated successfully";
            result.GeneratedFiles = new List<string> { configPath };

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating configuration");
            result.Success = false;
            result.Message = "Failed to update configuration";
            result.Errors.Add(ex.Message);
            return result;
        }
    }

    public async Task<PathValidationResult> ValidateOutputPathAsync(string outputPath)
    {
        var result = new PathValidationResult();

        try
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                result.IsValid = false;
                result.Errors.Add("Output path cannot be empty");
                return result;
            }

            var fullPath = Path.GetFullPath(outputPath);
            result.ResolvedPath = fullPath;

            var directory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
            
            if (string.IsNullOrEmpty(directory))
            {
                result.IsValid = false;
                result.Errors.Add("Invalid directory path");
                return result;
            }

            result.PathExists = Directory.Exists(directory);
            result.IsDirectory = Directory.Exists(fullPath);

            if (!result.PathExists)
            {
                // Try to create the directory
                try
                {
                    Directory.CreateDirectory(directory);
                    result.PathExists = true;
                    result.CanWrite = true;
                }
                catch (Exception ex)
                {
                    result.CanWrite = false;
                    result.Errors.Add($"Cannot create directory: {ex.Message}");
                }
            }
            else
            {
                // Check write permissions
                try
                {
                    var testFile = Path.Combine(directory, $".test_{Guid.NewGuid():N}");
                    await File.WriteAllTextAsync(testFile, "test");
                    File.Delete(testFile);
                    result.CanWrite = true;
                }
                catch
                {
                    result.CanWrite = false;
                    result.Errors.Add("Directory is not writable");
                }
            }

            result.IsValid = result.PathExists && result.CanWrite;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating output path");
            result.IsValid = false;
            result.Errors.Add($"Path validation error: {ex.Message}");
            return result;
        }
    }

    private async Task<DevcontainerValidationResult> ValidateOptionsAsync(DevcontainerOptions options)
    {
        var result = new DevcontainerValidationResult();
        var errors = new List<string>();

        if (string.IsNullOrEmpty(options.Name))
        {
            errors.Add("Name is required");
        }
        else if (!IsValidName(options.Name))
        {
            errors.Add("Name contains invalid characters");
        }

        if (string.IsNullOrEmpty(options.OutputPath))
        {
            errors.Add("Output path is required");
        }

        result.IsValid = !errors.Any();
        result.Errors = errors;
        return result;
    }

    private DevcontainerConfiguration CreateBasicConfiguration(DevcontainerOptions options)
    {
        var config = new DevcontainerConfiguration
        {
            Name = options.Name,
            Image = options.BaseImage ?? "mcr.microsoft.com/dotnet/sdk:8.0",
            WorkspaceFolder = "/workspaces"
        };

        if (options.ForwardPorts.Any())
        {
            config.ForwardPorts = options.ForwardPorts.ToArray();
        }

        if (!string.IsNullOrEmpty(options.PostCreateCommand))
        {
            config.PostCreateCommand = options.PostCreateCommand;
        }

        if (options.EnvironmentVariables.Any())
        {
            config.RemoteEnv = new Dictionary<string, string>(options.EnvironmentVariables);
        }

        return config;
    }

    private void ApplyCustomSettings(DevcontainerConfiguration config, DevcontainerOptions options)
    {
        foreach (var setting in options.CustomSettings)
        {
            switch (setting.Key.ToLower())
            {
                case "workspacefolder":
                    config.WorkspaceFolder = setting.Value?.ToString();
                    break;
                case "postcreateccommand":
                    config.PostCreateCommand = setting.Value?.ToString() ?? "";
                    break;
                default:
                    // Add to customizations
                    config.Customizations[setting.Key] = setting.Value;
                    break;
            }
        }
    }

    private async Task<DevcontainerConfiguration> CreateUpdatedConfigurationAsync(DevcontainerConfiguration existing, DevcontainerOptions updates)
    {
        var updated = JsonSerializer.Deserialize<DevcontainerConfiguration>(JsonSerializer.Serialize(existing));
        
        if (updated == null)
        {
            throw new InvalidOperationException("Failed to clone existing configuration");
        }

        // Apply updates
        if (!string.IsNullOrEmpty(updates.Name))
        {
            updated.Name = updates.Name;
        }

        if (!string.IsNullOrEmpty(updates.BaseImage))
        {
            updated.Image = updates.BaseImage;
        }

        if (!string.IsNullOrEmpty(updates.PostCreateCommand))
        {
            updated.PostCreateCommand = updates.PostCreateCommand;
        }

        // Add new features
        if (updates.Features.Any())
        {
            var availableFeatures = await _featureRegistry.GetAvailableFeaturesAsync();
            var featureMap = availableFeatures.ToDictionary(f => f.Id, f => f);

            foreach (var featureId in updates.Features)
            {
                if (featureMap.TryGetValue(featureId, out var feature))
                {
                    var featureKey = $"{feature.Repository}:{feature.Version}";
                    updated.Features[featureKey] = feature.DefaultOptions;
                }
            }
        }

        // Add new extensions
        if (updates.Extensions.Any())
        {
            if (updated.Customizations.TryGetValue("vscode", out var vsCodeCustomization))
            {
                // This is a simplified approach - in a real implementation, you'd need to properly handle the JSON structure
                var existingExtensions = new List<string>();
                updated.Customizations["vscode"] = new
                {
                    extensions = existingExtensions.Union(updates.Extensions).Distinct().ToArray()
                };
            }
            else
            {
                updated.Customizations["vscode"] = new
                {
                    extensions = updates.Extensions.ToArray()
                };
            }
        }

        // Merge environment variables
        foreach (var env in updates.EnvironmentVariables)
        {
            updated.RemoteEnv[env.Key] = env.Value;
        }

        // Add new ports
        if (updates.ForwardPorts.Any())
        {
            updated.ForwardPorts = updated.ForwardPorts.Union(updates.ForwardPorts).Distinct().ToArray();
        }

        return updated;
    }

    private static bool IsValidName(string name)
    {
        // Name should contain only alphanumeric characters, hyphens, and underscores
        return Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$");
    }

    private static bool IsValidImageName(string imageName)
    {
        // Basic image name validation
        return !string.IsNullOrEmpty(imageName) && 
               !imageName.Contains(' ') && 
               !imageName.StartsWith('-') && 
               !imageName.EndsWith('-');
    }
}