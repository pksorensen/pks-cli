using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Features;

/// <summary>
/// Base implementation for devcontainer features
/// </summary>
public abstract class BaseDevcontainerFeature : IDevcontainerFeature
{
    protected readonly ILogger Logger;

    protected BaseDevcontainerFeature(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Version { get; }
    public abstract string Category { get; }
    public virtual string[] Tags => Array.Empty<string>();
    public virtual bool IsDeprecated => false;
    public virtual string[] Dependencies => Array.Empty<string>();
    public virtual string[] ConflictsWith => Array.Empty<string>();
    public virtual Dictionary<string, object> DefaultOptions => new();
    public virtual Dictionary<string, DevcontainerFeatureOption> AvailableOptions => new();

    public virtual async Task<FeatureValidationResult> ValidateConfigurationAsync(object configuration)
    {
        var result = new FeatureValidationResult { IsValid = true };

        try
        {
            var configDict = ConvertToStringObjectDictionary(configuration);

            // Validate each provided option
            foreach (var option in configDict)
            {
                if (AvailableOptions.TryGetValue(option.Key, out var optionDef))
                {
                    var optionResult = ValidateOptionValue(option.Key, option.Value, optionDef);
                    if (!optionResult.IsValid)
                    {
                        result.Errors.AddRange(optionResult.Errors);
                    }
                }
                else
                {
                    result.Warnings.Add($"Unknown option '{option.Key}' for feature '{Id}'");
                }
            }

            // Check for required options
            foreach (var availableOption in AvailableOptions)
            {
                if (availableOption.Value.Required && !configDict.ContainsKey(availableOption.Key))
                {
                    result.Errors.Add($"Required option '{availableOption.Key}' is missing");
                }
            }

            result.IsValid = !result.Errors.Any();
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error validating configuration for feature {FeatureId}", Id);
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
            return result;
        }
    }

    public virtual async Task<Dictionary<string, object>> GenerateConfigurationAsync(Dictionary<string, object>? options = null)
    {
        await Task.CompletedTask; // For async compliance
        
        var config = new Dictionary<string, object>(DefaultOptions);

        if (options != null)
        {
            foreach (var option in options)
            {
                config[option.Key] = option.Value;
            }
        }

        return config;
    }

    public virtual string GetRepositoryUrl()
    {
        return $"ghcr.io/devcontainers/features/{Id}";
    }

    public virtual string GetDocumentationUrl()
    {
        return $"https://github.com/devcontainers/features/tree/main/src/{Id}";
    }

    public virtual async Task<bool> IsCompatibleWithImageAsync(string baseImage)
    {
        await Task.CompletedTask; // For async compliance
        
        // Most features are compatible with most images
        // Subclasses can override for specific compatibility checks
        return !string.IsNullOrEmpty(baseImage);
    }

    public virtual async Task<List<string>> GetRecommendedExtensionsAsync()
    {
        await Task.CompletedTask; // For async compliance
        return new List<string>();
    }

    public virtual async Task<Dictionary<string, string>> GetEnvironmentVariablesAsync()
    {
        await Task.CompletedTask; // For async compliance
        return new Dictionary<string, string>();
    }

    public virtual async Task<List<int>> GetForwardedPortsAsync()
    {
        await Task.CompletedTask; // For async compliance
        return new List<int>();
    }

    public virtual async Task<List<string>> GetPostCreateCommandsAsync()
    {
        await Task.CompletedTask; // For async compliance
        return new List<string>();
    }

    protected static Dictionary<string, object> ConvertToStringObjectDictionary(object configuration)
    {
        if (configuration is Dictionary<string, object> dict)
        {
            return dict;
        }

        // Try to serialize and deserialize to get a dictionary
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(configuration);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    protected static FeatureValidationResult ValidateOptionValue(string optionName, object value, DevcontainerFeatureOption optionDef)
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
                        if (!Regex.IsMatch(stringValue, optionDef.Pattern))
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

                case "array":
                    if (value is not System.Collections.IEnumerable)
                    {
                        result.IsValid = false;
                        result.Errors.Add($"Option '{optionName}' must be an array");
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

    protected static bool IsNumeric(object value)
    {
        return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    protected DevcontainerFeatureOption CreateStringOption(string description, object? defaultValue = null, string[]? enumValues = null, bool required = false, string? pattern = null)
    {
        return new DevcontainerFeatureOption
        {
            Type = "string",
            Description = description,
            Default = defaultValue,
            Enum = enumValues,
            Required = required,
            Pattern = pattern
        };
    }

    protected DevcontainerFeatureOption CreateBooleanOption(string description, bool defaultValue = false, bool required = false)
    {
        return new DevcontainerFeatureOption
        {
            Type = "boolean",
            Description = description,
            Default = defaultValue,
            Required = required
        };
    }

    protected DevcontainerFeatureOption CreateNumberOption(string description, object? defaultValue = null, object? minimum = null, object? maximum = null, bool required = false)
    {
        return new DevcontainerFeatureOption
        {
            Type = "number",
            Description = description,
            Default = defaultValue,
            Minimum = minimum,
            Maximum = maximum,
            Required = required
        };
    }

    protected DevcontainerFeatureOption CreateArrayOption(string description, object? defaultValue = null, bool required = false)
    {
        return new DevcontainerFeatureOption
        {
            Type = "array",
            Description = description,
            Default = defaultValue,
            Required = required
        };
    }
}