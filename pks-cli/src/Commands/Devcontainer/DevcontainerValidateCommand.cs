using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Text.Json;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command for validating devcontainer configurations
/// Provides comprehensive validation with detailed reports and suggestions
/// </summary>
public class DevcontainerValidateCommand : DevcontainerCommand<DevcontainerValidateSettings>
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IVsCodeExtensionService _extensionService;

    public DevcontainerValidateCommand(
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry,
        IVsCodeExtensionService extensionService)
    {
        _devcontainerService = devcontainerService ?? throw new ArgumentNullException(nameof(devcontainerService));
        _featureRegistry = featureRegistry ?? throw new ArgumentNullException(nameof(featureRegistry));
        _extensionService = extensionService ?? throw new ArgumentNullException(nameof(extensionService));
    }

    public override int Execute(CommandContext context, DevcontainerValidateSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, DevcontainerValidateSettings settings)
    {
        try
        {
            DisplayBanner("Validation");

            // Determine configuration path
            var configPath = DetermineConfigPath(settings);
            
            if (!File.Exists(configPath))
            {
                DisplayError($"Devcontainer configuration not found at: {configPath}");
                DisplayInfo("Use 'pks devcontainer init' to create a new configuration");
                return 1;
            }

            DisplayInfo($"Validating devcontainer configuration: {configPath}");
            AnsiConsole.WriteLine();

            // Load and parse configuration
            DevcontainerConfiguration configuration;
            try
            {
                var configContent = await File.ReadAllTextAsync(configPath);
                configuration = JsonSerializer.Deserialize<DevcontainerConfiguration>(configContent) 
                    ?? throw new InvalidOperationException("Failed to deserialize configuration");
            }
            catch (Exception ex)
            {
                DisplayError($"Failed to parse devcontainer configuration: {ex.Message}");
                return 1;
            }

            var validationTasks = new List<Task<ValidationResult>>();

            // Core validation
            validationTasks.Add(ValidateConfigurationStructureAsync(configuration, settings));

            // Feature validation
            if (settings.CheckFeatures)
            {
                validationTasks.Add(ValidateFeaturesAsync(configuration, settings));
            }

            // Extension validation
            if (settings.CheckExtensions)
            {
                validationTasks.Add(ValidateExtensionsAsync(configuration, settings));
            }

            // Additional validations
            validationTasks.Add(ValidatePortsAsync(configuration));
            validationTasks.Add(ValidateImageAsync(configuration));
            validationTasks.Add(ValidatePathsAsync(configuration, Path.GetDirectoryName(configPath) ?? string.Empty));

            // Execute all validations
            var results = await WithSpinnerAsync("Running validation checks", async () =>
            {
                return await Task.WhenAll(validationTasks);
            });

            // Aggregate results
            var aggregatedResult = AggregateValidationResults(results);
            
            // Display results
            DisplayValidationSummary(aggregatedResult, settings);

            // Return appropriate exit code
            return DetermineExitCode(aggregatedResult, settings);
        }
        catch (Exception ex)
        {
            DisplayError($"Validation failed with error: {ex.Message}");
            if (settings.Verbose)
            {
                DisplayError($"Stack trace: {ex.StackTrace}");
            }
            return 1;
        }
    }

    private string DetermineConfigPath(DevcontainerValidateSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ConfigPath))
        {
            return Path.IsPathFullyQualified(settings.ConfigPath) 
                ? settings.ConfigPath 
                : Path.GetFullPath(settings.ConfigPath);
        }

        // Look for devcontainer.json in standard locations
        var candidates = new[]
        {
            Path.Combine(settings.OutputPath, ".devcontainer", "devcontainer.json"),
            Path.Combine(settings.OutputPath, ".devcontainer.json"),
            Path.Combine(settings.OutputPath, "devcontainer.json")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Default to the most common location
        return Path.Combine(settings.OutputPath, ".devcontainer", "devcontainer.json");
    }

    private async Task<ValidationResult> ValidateConfigurationStructureAsync(
        DevcontainerConfiguration configuration, 
        DevcontainerValidateSettings settings)
    {
        var result = new ValidationResult("Configuration Structure");

        // Basic structure validation
        if (string.IsNullOrEmpty(configuration.Name))
        {
            result.AddError("Configuration must have a name");
        }

        if (string.IsNullOrEmpty(configuration.Image) && configuration.Build == null)
        {
            result.AddError("Configuration must specify either an image or build configuration");
        }

        if (!string.IsNullOrEmpty(configuration.Image) && configuration.Build != null)
        {
            result.AddWarning("Configuration specifies both image and build - build will take precedence");
        }

        // Validate build configuration if present
        if (configuration.Build != null)
        {
            if (string.IsNullOrEmpty(configuration.Build.Dockerfile))
            {
                result.AddError("Build configuration must specify a Dockerfile");
            }
        }

        // Validate Docker Compose configuration
        if (!string.IsNullOrEmpty(configuration.DockerComposeFile))
        {
            if (string.IsNullOrEmpty(configuration.Service))
            {
                result.AddError("Docker Compose configuration must specify a service name");
            }
        }

        // Use the devcontainer service for additional validation
        var serviceValidation = await _devcontainerService.ValidateConfigurationAsync(configuration);
        
        result.AddErrors(serviceValidation.Errors);
        result.AddWarnings(serviceValidation.Warnings);
        result.AddSuggestions(serviceValidation.Suggestions);

        return result;
    }

    private async Task<ValidationResult> ValidateFeaturesAsync(
        DevcontainerConfiguration configuration, 
        DevcontainerValidateSettings settings)
    {
        var result = new ValidationResult("Features");

        if (!configuration.Features.Any())
        {
            result.AddInfo("No features configured");
            return result;
        }

        // Get available features for validation
        var availableFeatures = await _featureRegistry.GetAvailableFeaturesAsync();
        var availableFeatureIds = availableFeatures.Select(f => f.Id).ToHashSet();

        foreach (var featureEntry in configuration.Features)
        {
            var featureId = featureEntry.Key;
            var featureConfig = featureEntry.Value;

            // Check if feature exists
            if (!availableFeatureIds.Contains(featureId))
            {
                result.AddError($"Unknown feature: {featureId}");
                continue;
            }

            // Get feature details
            var feature = await _featureRegistry.GetFeatureAsync(featureId);
            if (feature == null)
            {
                result.AddError($"Could not load feature details for: {featureId}");
                continue;
            }

            // Check for deprecated features
            if (feature.IsDeprecated)
            {
                var message = $"Feature '{featureId}' is deprecated";
                if (!string.IsNullOrEmpty(feature.DeprecationMessage))
                {
                    message += $": {feature.DeprecationMessage}";
                }
                result.AddWarning(message);
            }

            // Validate feature configuration
            var featureValidation = await _featureRegistry.ValidateFeatureConfiguration(featureId, featureConfig);
            if (!featureValidation.IsValid)
            {
                result.AddErrors(featureValidation.Errors.Select(e => $"Feature '{featureId}': {e}"));
                result.AddWarnings(featureValidation.Warnings.Select(w => $"Feature '{featureId}': {w}"));
            }
        }

        // Check for feature conflicts and dependencies
        var featureIds = configuration.Features.Keys.ToList();
        var resolutionResult = await _devcontainerService.ResolveFeatureDependenciesAsync(featureIds);
        
        if (!resolutionResult.Success)
        {
            result.AddError($"Feature dependency resolution failed: {resolutionResult.ErrorMessage}");
        }

        foreach (var conflict in resolutionResult.ConflictingFeatures)
        {
            var severity = conflict.Severity == ConflictSeverity.Warning ? "Warning" : "Error";
            var message = $"{severity}: {conflict.Feature1} conflicts with {conflict.Feature2} - {conflict.Reason}";
            
            if (conflict.Severity == ConflictSeverity.Warning)
            {
                result.AddWarning(message);
            }
            else
            {
                result.AddError(message);
            }

            if (!string.IsNullOrEmpty(conflict.Resolution))
            {
                result.AddSuggestion($"Resolution for {conflict.Feature1}/{conflict.Feature2}: {conflict.Resolution}");
            }
        }

        if (resolutionResult.MissingDependencies.Any())
        {
            result.AddWarning($"Missing feature dependencies: {string.Join(", ", resolutionResult.MissingDependencies)}");
            result.AddSuggestion("Run 'pks devcontainer init --features' to resolve dependencies automatically");
        }

        return result;
    }

    private async Task<ValidationResult> ValidateExtensionsAsync(
        DevcontainerConfiguration configuration, 
        DevcontainerValidateSettings settings)
    {
        var result = new ValidationResult("Extensions");

        var extensions = GetExtensionsFromConfiguration(configuration);
        
        if (!extensions.Any())
        {
            result.AddInfo("No VS Code extensions configured");
            return result;
        }

        foreach (var extensionId in extensions)
        {
            try
            {
                var validation = await _extensionService.ValidateExtensionAsync(extensionId);
                
                if (!validation.IsValid)
                {
                    result.AddError($"Extension '{extensionId}': {validation.ErrorMessage}");
                }
                else if (!validation.Exists)
                {
                    result.AddWarning($"Extension '{extensionId}' may not exist in the marketplace");
                }
                else if (!validation.IsCompatible)
                {
                    result.AddWarning($"Extension '{extensionId}' may not be compatible with devcontainers");
                }

                if (validation.Dependencies.Any())
                {
                    result.AddInfo($"Extension '{extensionId}' has dependencies: {string.Join(", ", validation.Dependencies)}");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Could not validate extension '{extensionId}': {ex.Message}");
            }
        }

        return result;
    }

    private Task<ValidationResult> ValidatePortsAsync(DevcontainerConfiguration configuration)
    {
        var result = new ValidationResult("Port Configuration");

        if (!configuration.ForwardPorts.Any())
        {
            result.AddInfo("No port forwarding configured");
            return Task.FromResult(result);
        }

        var seenPorts = new HashSet<int>();
        
        foreach (var port in configuration.ForwardPorts)
        {
            if (port < 1 || port > 65535)
            {
                result.AddError($"Invalid port number: {port}. Must be between 1 and 65535");
            }
            else if (seenPorts.Contains(port))
            {
                result.AddError($"Duplicate port configuration: {port}");
            }
            else
            {
                seenPorts.Add(port);
            }

            // Check for commonly used system ports
            if (port < 1024)
            {
                result.AddWarning($"Port {port} is in the system reserved range (1-1023)");
            }
        }

        return Task.FromResult(result);
    }

    private Task<ValidationResult> ValidateImageAsync(DevcontainerConfiguration configuration)
    {
        var result = new ValidationResult("Base Image");

        if (string.IsNullOrEmpty(configuration.Image))
        {
            result.AddInfo("No base image specified (using build configuration)");
            return Task.FromResult(result);
        }

        // Basic image name validation
        if (!IsValidImageName(configuration.Image))
        {
            result.AddError($"Invalid image name format: {configuration.Image}");
        }

        // Check for latest tag usage
        if (configuration.Image.EndsWith(":latest"))
        {
            result.AddWarning("Using 'latest' tag is not recommended for reproducible builds");
            result.AddSuggestion("Consider pinning to a specific version tag");
        }

        // Check for common base images and provide suggestions
        if (configuration.Image.Contains("ubuntu") && !configuration.Image.Contains("devcontainer"))
        {
            result.AddSuggestion("Consider using mcr.microsoft.com/vscode/devcontainers/base:ubuntu for better devcontainer support");
        }

        return Task.FromResult(result);
    }

    private Task<ValidationResult> ValidatePathsAsync(DevcontainerConfiguration configuration, string configDir)
    {
        var result = new ValidationResult("File Paths");

        // Validate workspace folder
        if (!string.IsNullOrEmpty(configuration.WorkspaceFolder))
        {
            if (!Path.IsPathFullyQualified(configuration.WorkspaceFolder) && 
                !configuration.WorkspaceFolder.StartsWith("/"))
            {
                result.AddWarning($"Workspace folder should be an absolute path: {configuration.WorkspaceFolder}");
            }
        }

        // Validate Dockerfile path if using build
        if (configuration.Build?.Dockerfile != null)
        {
            var dockerfilePath = Path.IsPathFullyQualified(configuration.Build.Dockerfile)
                ? configuration.Build.Dockerfile
                : Path.Combine(configDir, configuration.Build.Dockerfile);

            if (!File.Exists(dockerfilePath))
            {
                result.AddError($"Dockerfile not found: {configuration.Build.Dockerfile}");
            }
        }

        // Validate Docker Compose file if specified
        if (!string.IsNullOrEmpty(configuration.DockerComposeFile))
        {
            var composePath = Path.IsPathFullyQualified(configuration.DockerComposeFile)
                ? configuration.DockerComposeFile
                : Path.Combine(configDir, configuration.DockerComposeFile);

            if (!File.Exists(composePath))
            {
                result.AddError($"Docker Compose file not found: {configuration.DockerComposeFile}");
            }
        }

        return Task.FromResult(result);
    }

    private static IEnumerable<string> GetExtensionsFromConfiguration(DevcontainerConfiguration configuration)
    {
        if (configuration.Customizations.TryGetValue("vscode", out var vscodeConfig) &&
            vscodeConfig is JsonElement vscodeElement &&
            vscodeElement.TryGetProperty("extensions", out var extensionsElement) &&
            extensionsElement.ValueKind == JsonValueKind.Array)
        {
            return extensionsElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrEmpty(s));
        }

        return Enumerable.Empty<string>();
    }

    private static bool IsValidImageName(string imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
            return false;

        // Basic validation - more comprehensive validation would require additional logic
        return !imageName.Contains(" ") && 
               !imageName.StartsWith("-") && 
               !imageName.EndsWith("-") &&
               imageName.All(c => char.IsLetterOrDigit(c) || ".-/:_".Contains(c));
    }

    private static AggregatedValidationResult AggregateValidationResults(ValidationResult[] results)
    {
        var aggregated = new AggregatedValidationResult();

        foreach (var result in results)
        {
            aggregated.Results.Add(result);
            aggregated.TotalErrors += result.Errors.Count;
            aggregated.TotalWarnings += result.Warnings.Count;
            aggregated.TotalSuggestions += result.Suggestions.Count;
        }

        aggregated.IsValid = aggregated.TotalErrors == 0;
        aggregated.OverallSeverity = aggregated.TotalErrors > 0 
            ? ValidationSeverity.Error 
            : aggregated.TotalWarnings > 0 
                ? ValidationSeverity.Warning 
                : ValidationSeverity.None;

        return aggregated;
    }

    private void DisplayValidationSummary(AggregatedValidationResult result, DevcontainerValidateSettings settings)
    {
        // Display overall status
        var statusIcon = result.IsValid ? "✓" : "✗";
        var statusColor = result.IsValid ? "green" : "red";
        var statusText = result.IsValid ? "VALID" : "INVALID";

        AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon} Configuration is {statusText}[/]");
        AnsiConsole.WriteLine();

        // Display summary statistics
        var summaryTable = new Table()
            .Title("[cyan]Validation Summary[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Category")
            .AddColumn("Errors")
            .AddColumn("Warnings")
            .AddColumn("Suggestions");

        foreach (var validationResult in result.Results)
        {
            var errorColor = validationResult.Errors.Any() ? "red" : "green";
            var warningColor = validationResult.Warnings.Any() ? "yellow" : "dim";

            summaryTable.AddRow(
                validationResult.Category,
                $"[{errorColor}]{validationResult.Errors.Count}[/]",
                $"[{warningColor}]{validationResult.Warnings.Count}[/]",
                $"[cyan]{validationResult.Suggestions.Count}[/]"
            );
        }

        AnsiConsole.Write(summaryTable);

        // Display detailed results if there are issues or verbose mode
        if (!result.IsValid || result.TotalWarnings > 0 || settings.Verbose)
        {
            AnsiConsole.WriteLine();
            DisplayDetailedResults(result.Results, settings);
        }

        // Display totals
        AnsiConsole.WriteLine();
        var totalsPanel = new Panel(
            $"[red]Errors: {result.TotalErrors}[/] | " +
            $"[yellow]Warnings: {result.TotalWarnings}[/] | " +
            $"[cyan]Suggestions: {result.TotalSuggestions}[/]"
        )
        .Header("[cyan]Totals[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(totalsPanel);
    }

    private void DisplayDetailedResults(List<ValidationResult> results, DevcontainerValidateSettings settings)
    {
        foreach (var result in results.Where(r => r.HasIssues || settings.Verbose))
        {
            var rule = new Rule($"[cyan]{result.Category}[/]").RuleStyle("cyan");
            AnsiConsole.Write(rule);

            if (result.Errors.Any())
            {
                AnsiConsole.MarkupLine("[red]Errors:[/]");
                foreach (var error in result.Errors)
                {
                    AnsiConsole.MarkupLine($"[red]  • {error}[/]");
                }
                AnsiConsole.WriteLine();
            }

            if (result.Warnings.Any())
            {
                AnsiConsole.MarkupLine("[yellow]Warnings:[/]");
                foreach (var warning in result.Warnings)
                {
                    AnsiConsole.MarkupLine($"[yellow]  • {warning}[/]");
                }
                AnsiConsole.WriteLine();
            }

            if (result.Suggestions.Any())
            {
                AnsiConsole.MarkupLine("[cyan]Suggestions:[/]");
                foreach (var suggestion in result.Suggestions)
                {
                    AnsiConsole.MarkupLine($"[cyan]  • {suggestion}[/]");
                }
                AnsiConsole.WriteLine();
            }

            if (result.InfoMessages.Any() && settings.Verbose)
            {
                AnsiConsole.MarkupLine("[dim]Information:[/]");
                foreach (var info in result.InfoMessages)
                {
                    AnsiConsole.MarkupLine($"[dim]  • {info}[/]");
                }
                AnsiConsole.WriteLine();
            }
        }
    }

    private static int DetermineExitCode(AggregatedValidationResult result, DevcontainerValidateSettings settings)
    {
        if (result.TotalErrors > 0)
        {
            return 1; // Errors found
        }

        if (settings.Strict && result.TotalWarnings > 0)
        {
            return 1; // Strict mode treats warnings as errors
        }

        return 0; // Success
    }

}

/// <summary>
/// Individual validation result for a specific category
/// </summary>
public class ValidationResult
{
    public string Category { get; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Suggestions { get; } = new();
    public List<string> InfoMessages { get; } = new();

    public bool HasIssues => Errors.Any() || Warnings.Any();

    public ValidationResult(string category)
    {
        Category = category;
    }

    public void AddError(string error) => Errors.Add(error);
    public void AddWarning(string warning) => Warnings.Add(warning);
    public void AddSuggestion(string suggestion) => Suggestions.Add(suggestion);
    public void AddInfo(string info) => InfoMessages.Add(info);

    public void AddErrors(IEnumerable<string> errors) => Errors.AddRange(errors);
    public void AddWarnings(IEnumerable<string> warnings) => Warnings.AddRange(warnings);
    public void AddSuggestions(IEnumerable<string> suggestions) => Suggestions.AddRange(suggestions);
}

/// <summary>
/// Aggregated validation result across all categories
/// </summary>
public class AggregatedValidationResult
{
    public bool IsValid { get; set; }
    public ValidationSeverity OverallSeverity { get; set; }
    public int TotalErrors { get; set; }
    public int TotalWarnings { get; set; }
    public int TotalSuggestions { get; set; }
    public List<ValidationResult> Results { get; } = new();
}