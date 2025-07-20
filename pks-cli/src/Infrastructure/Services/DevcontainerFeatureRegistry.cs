using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Registry for managing devcontainer features
/// </summary>
public class DevcontainerFeatureRegistry : IDevcontainerFeatureRegistry
{
    private readonly ILogger<DevcontainerFeatureRegistry> _logger;
    private List<DevcontainerFeature> _features = new();
    private readonly object _lock = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromHours(24);

    public DevcontainerFeatureRegistry(ILogger<DevcontainerFeatureRegistry> logger)
    {
        _logger = logger;
        _ = Task.Run(InitializeFeaturesAsync); // Initialize asynchronously
    }

    public async Task<List<DevcontainerFeature>> GetAvailableFeaturesAsync()
    {
        await EnsureFeaturesLoadedAsync();
        
        lock (_lock)
        {
            return new List<DevcontainerFeature>(_features);
        }
    }

    public async Task<DevcontainerFeature?> GetFeatureAsync(string id)
    {
        await EnsureFeaturesLoadedAsync();
        
        lock (_lock)
        {
            return _features.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<List<DevcontainerFeature>> SearchFeaturesAsync(string query)
    {
        await EnsureFeaturesLoadedAsync();
        
        if (string.IsNullOrEmpty(query))
        {
            return await GetAvailableFeaturesAsync();
        }

        lock (_lock)
        {
            var lowerQuery = query.ToLowerInvariant();
            return _features.Where(f =>
                f.Name.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                f.Description.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                f.Tags.Any(t => t.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)) ||
                f.Category.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase) ||
                f.Id.Contains(lowerQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }
    }

    public async Task<List<DevcontainerFeature>> GetFeaturesByCategory(string category)
    {
        await EnsureFeaturesLoadedAsync();
        
        lock (_lock)
        {
            return _features.Where(f => f.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public async Task<FeatureValidationResult> ValidateFeatureConfiguration(string featureId, object configuration)
    {
        var result = new FeatureValidationResult();

        try
        {
            var feature = await GetFeatureAsync(featureId);
            if (feature == null)
            {
                result.IsValid = false;
                result.Errors.Add($"Feature '{featureId}' not found");
                return result;
            }

            if (feature.IsDeprecated)
            {
                result.Warnings.Add($"Feature '{featureId}' is deprecated" + 
                    (string.IsNullOrEmpty(feature.DeprecationMessage) ? "" : $": {feature.DeprecationMessage}"));
            }

            // Validate configuration against feature options
            var configDict = ConvertToStringObjectDictionary(configuration);
            var validatedOptions = new Dictionary<string, object>();

            foreach (var option in feature.AvailableOptions)
            {
                var optionName = option.Key;
                var optionDef = option.Value;

                if (configDict.TryGetValue(optionName, out var value))
                {
                    // Validate the provided value
                    var validationResult = ValidateOptionValue(optionName, value, optionDef);
                    if (!validationResult.IsValid)
                    {
                        result.Errors.AddRange(validationResult.Errors);
                    }
                    else
                    {
                        validatedOptions[optionName] = value;
                    }
                }
                else if (optionDef.Required)
                {
                    result.Errors.Add($"Required option '{optionName}' is missing");
                }
                else if (optionDef.Default != null)
                {
                    validatedOptions[optionName] = optionDef.Default;
                }
            }

            // Check for unknown options
            foreach (var providedOption in configDict.Keys)
            {
                if (!feature.AvailableOptions.ContainsKey(providedOption))
                {
                    result.Warnings.Add($"Unknown option '{providedOption}' for feature '{featureId}'");
                }
            }

            result.IsValid = !result.Errors.Any();
            result.ValidatedOptions = validatedOptions;

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating feature configuration for {FeatureId}", featureId);
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
            return result;
        }
    }

    public async Task<List<string>> GetFeatureDependenciesAsync(string featureId)
    {
        var feature = await GetFeatureAsync(featureId);
        return feature?.Dependencies.ToList() ?? new List<string>();
    }

    public async Task<List<string>> GetFeatureConflictsAsync(string featureId)
    {
        var feature = await GetFeatureAsync(featureId);
        return feature?.ConflictsWith.ToList() ?? new List<string>();
    }

    public async Task<List<string>> GetAvailableCategoriesAsync()
    {
        await EnsureFeaturesLoadedAsync();
        
        lock (_lock)
        {
            return _features.Select(f => f.Category).Distinct().OrderBy(c => c).ToList();
        }
    }

    public async Task<bool> RefreshFeaturesAsync()
    {
        try
        {
            _logger.LogInformation("Refreshing devcontainer features from remote sources");
            
            // In a real implementation, this would fetch from:
            // - GitHub's devcontainer features repository
            // - Custom feature registries
            // - Local feature definitions
            
            var newFeatures = await LoadBuiltInFeaturesAsync();
            
            lock (_lock)
            {
                _features = newFeatures;
                _lastRefresh = DateTime.UtcNow;
            }
            
            _logger.LogInformation("Successfully refreshed {Count} features", newFeatures.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh features");
            return false;
        }
    }

    public async Task<List<DevcontainerFeature>> GetCompatibleFeaturesAsync(string baseImage)
    {
        await EnsureFeaturesLoadedAsync();
        
        // For now, return all features as most devcontainer features are cross-platform
        // In a real implementation, this would check feature compatibility with the base image
        lock (_lock)
        {
            return new List<DevcontainerFeature>(_features);
        }
    }

    private async Task EnsureFeaturesLoadedAsync()
    {
        bool needsRefresh;
        
        lock (_lock)
        {
            needsRefresh = !_features.Any() || DateTime.UtcNow - _lastRefresh > _cacheTimeout;
        }

        if (needsRefresh)
        {
            await RefreshFeaturesAsync();
        }
    }

    private async Task InitializeFeaturesAsync()
    {
        try
        {
            var features = await LoadBuiltInFeaturesAsync();
            
            lock (_lock)
            {
                _features = features;
                _lastRefresh = DateTime.UtcNow;
            }
            
            _logger.LogInformation("Initialized with {Count} built-in features", features.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize features");
        }
    }

    private static async Task<List<DevcontainerFeature>> LoadBuiltInFeaturesAsync()
    {
        // This would normally load from external sources, but for now we'll use built-in definitions
        await Task.Delay(100); // Simulate async operation
        
        return new List<DevcontainerFeature>
        {
            new()
            {
                Id = "dotnet",
                Name = ".NET",
                Description = "Installs .NET SDK and runtime",
                Version = "2",
                Repository = "ghcr.io/devcontainers/features/dotnet",
                Category = "runtime",
                Tags = new[] { "dotnet", "csharp", "runtime" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/dotnet",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "8.0"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = ".NET version to install",
                        Default = "8.0",
                        Enum = new[] { "6.0", "7.0", "8.0", "latest" }
                    },
                    ["installUsingApt"] = new()
                    {
                        Type = "boolean",
                        Description = "Install using apt-get instead of tar.gz",
                        Default = true
                    },
                    ["dotnetRuntimeOnly"] = new()
                    {
                        Type = "boolean",
                        Description = "Install runtime only (not SDK)",
                        Default = false
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = Array.Empty<string>()
            },
            new()
            {
                Id = "docker-in-docker",
                Name = "Docker in Docker",
                Description = "Enables Docker inside the container",
                Version = "2",
                Repository = "ghcr.io/devcontainers/features/docker-in-docker",
                Category = "tool",
                Tags = new[] { "docker", "container" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/docker-in-docker",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest",
                    ["moby"] = true
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Docker version",
                        Default = "latest"
                    },
                    ["moby"] = new()
                    {
                        Type = "boolean",
                        Description = "Install Moby CLI instead of Docker CLI",
                        Default = true
                    },
                    ["dockerDashComposeVersion"] = new()
                    {
                        Type = "string",
                        Description = "Docker Compose version",
                        Default = "v2"
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = new[] { "docker-outside-of-docker" }
            },
            new()
            {
                Id = "azure-cli",
                Name = "Azure CLI",
                Description = "Installs Azure CLI",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/azure-cli",
                Category = "cloud",
                Tags = new[] { "azure", "cli", "cloud" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/azure-cli",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Azure CLI version",
                        Default = "latest"
                    },
                    ["installBicep"] = new()
                    {
                        Type = "boolean",
                        Description = "Install Azure Bicep CLI",
                        Default = true
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = Array.Empty<string>()
            },
            new()
            {
                Id = "kubectl-helm-minikube",
                Name = "Kubernetes Tools",
                Description = "Installs kubectl, Helm, and Minikube",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/kubectl-helm-minikube",
                Category = "kubernetes",
                Tags = new[] { "kubernetes", "kubectl", "helm", "minikube" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/kubectl-helm-minikube",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest",
                    ["helm"] = "latest",
                    ["minikube"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "kubectl version",
                        Default = "latest"
                    },
                    ["helm"] = new()
                    {
                        Type = "string",
                        Description = "Helm version",
                        Default = "latest"
                    },
                    ["minikube"] = new()
                    {
                        Type = "string",
                        Description = "Minikube version",
                        Default = "latest"
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = Array.Empty<string>()
            },
            new()
            {
                Id = "node",
                Name = "Node.js",
                Description = "Installs Node.js and npm",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/node",
                Category = "runtime",
                Tags = new[] { "node", "npm", "javascript", "typescript" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/node",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "lts",
                    ["nodeGypDependencies"] = true
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Node.js version",
                        Default = "lts",
                        Enum = new[] { "lts", "18", "19", "20", "latest" }
                    },
                    ["nodeGypDependencies"] = new()
                    {
                        Type = "boolean",
                        Description = "Install dependencies for node-gyp",
                        Default = true
                    },
                    ["nvmInstallPath"] = new()
                    {
                        Type = "string",
                        Description = "NVM install path",
                        Default = "/usr/local/share/nvm"
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = Array.Empty<string>()
            },
            new()
            {
                Id = "git",
                Name = "Git",
                Description = "Installs Git SCM",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/git",
                Category = "tool",
                Tags = new[] { "git", "scm", "version-control" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/git",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Git version",
                        Default = "latest"
                    },
                    ["ppa"] = new()
                    {
                        Type = "boolean",
                        Description = "Install from PPA instead of default package",
                        Default = true
                    }
                },
                Dependencies = Array.Empty<string>(),
                ConflictsWith = Array.Empty<string>()
            },
            new()
            {
                Id = "github-cli",
                Name = "GitHub CLI",
                Description = "Installs GitHub CLI (gh)",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/github-cli",
                Category = "tool",
                Tags = new[] { "github", "cli", "gh" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/github-cli",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "GitHub CLI version",
                        Default = "latest"
                    }
                },
                Dependencies = new[] { "git" },
                ConflictsWith = Array.Empty<string>()
            }
        };
    }

    private static Dictionary<string, object> ConvertToStringObjectDictionary(object configuration)
    {
        if (configuration is Dictionary<string, object> dict)
        {
            return dict;
        }

        if (configuration is JsonElement jsonElement)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText()) ?? new();
        }

        // Try to serialize and deserialize to get a dictionary
        try
        {
            var json = JsonSerializer.Serialize(configuration);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    private static FeatureValidationResult ValidateOptionValue(string optionName, object value, DevcontainerFeatureOption optionDef)
    {
        var result = new FeatureValidationResult { IsValid = true };

        try
        {
            switch (optionDef.Type.ToLower())
            {
                case "string":
                    if (value is not string stringValue)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be a string");
                        break;
                    }

                    if (optionDef.Enum != null && !optionDef.Enum.Contains(stringValue))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be one of: {string.Join(", ", optionDef.Enum)}");
                    }

                    if (!string.IsNullOrEmpty(optionDef.Pattern))
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(stringValue, optionDef.Pattern))
                        {
                            result.IsValid = false;
                            result.Errors.Add($"Option '{optionName}' does not match required pattern");
                        }
                    }
                    break;

                case "boolean":
                    if (value is not bool)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be a boolean");
                    }
                    break;

                case "number":
                case "integer":
                    if (!IsNumeric(value))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be a number");
                        break;
                    }

                    var numericValue = Convert.ToDouble(value);
                    
                    if (optionDef.Minimum != null && numericValue < Convert.ToDouble(optionDef.Minimum))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be at least {optionDef.Minimum}");
                    }

                    if (optionDef.Maximum != null && numericValue > Convert.ToDouble(optionDef.Maximum))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be at most {optionDef.Maximum}");
                    }
                    break;

                default:
                    result.Warnings.Add($"Unknown option type '{optionDef.Type}' for '{optionName}'");
                    break;
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Error validating option '{optionName}': {ex.Message}");
        }

        return result;
    }

    private static bool IsNumeric(object value)
    {
        return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }
}