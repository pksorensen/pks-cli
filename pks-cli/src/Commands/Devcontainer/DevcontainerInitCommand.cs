using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command for initializing devcontainer configurations
/// Non-interactive command for quick devcontainer setup with command-line options
/// </summary>
public class DevcontainerInitCommand : DevcontainerCommand<DevcontainerInitSettings>
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerTemplateService _templateService;

    public DevcontainerInitCommand(
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService)
    {
        _devcontainerService = devcontainerService ?? throw new ArgumentNullException(nameof(devcontainerService));
        _featureRegistry = featureRegistry ?? throw new ArgumentNullException(nameof(featureRegistry));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
    }

    public override int Execute(CommandContext context, DevcontainerInitSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, DevcontainerInitSettings settings)
    {
        try
        {
            DisplayBanner("Initialization");

            // If interactive mode is requested, delegate to wizard
            if (settings.Interactive)
            {
                DisplayInfo("Interactive mode requested - launching devcontainer wizard...");
                // Note: In a real implementation, we'd redirect to the wizard command
                DisplayWarning("Interactive mode is available via 'pks devcontainer wizard' command");
                return 0;
            }

            // Validate and resolve paths
            var outputPath = ValidateAndResolvePath(settings.OutputPath);
            
            // Check if devcontainer already exists
            var existingConfigPath = Path.Combine(outputPath, ".devcontainer", "devcontainer.json");
            if (File.Exists(existingConfigPath) && !settings.Force)
            {
                DisplayError($"Devcontainer configuration already exists at {existingConfigPath}");
                DisplayInfo("Use --force to overwrite existing configuration");
                return 1;
            }

            // Validate output path
            var pathValidation = await _devcontainerService.ValidateOutputPathAsync(outputPath);
            if (!pathValidation.IsValid)
            {
                DisplayError("Invalid output path:");
                foreach (var error in pathValidation.Errors)
                {
                    DisplayError($"  • {error}");
                }
                return 1;
            }

            // Build devcontainer options
            var options = await BuildDevcontainerOptionsAsync(settings, outputPath);
            
            // Validate feature dependencies
            if (options.Features.Any())
            {
                var resolutionResult = await _devcontainerService.ResolveFeatureDependenciesAsync(options.Features);
                if (!resolutionResult.Success)
                {
                    DisplayError("Feature dependency resolution failed:");
                    DisplayError($"  • {resolutionResult.ErrorMessage}");
                    
                    if (resolutionResult.ConflictingFeatures.Any())
                    {
                        DisplayError("Feature conflicts detected:");
                        foreach (var conflict in resolutionResult.ConflictingFeatures)
                        {
                            DisplayError($"  • {conflict.Feature1} conflicts with {conflict.Feature2}: {conflict.Reason}");
                        }
                    }
                    return 1;
                }
                
                // Update features with resolved dependencies
                options.Features = resolutionResult.ResolvedFeatures.Select(f => f.Id).ToList();
                
                if (resolutionResult.MissingDependencies.Any())
                {
                    DisplayWarning("Missing dependencies detected and will be added:");
                    foreach (var dep in resolutionResult.MissingDependencies)
                    {
                        DisplayWarning($"  • {dep}");
                    }
                }
            }

            // Display dry run information
            if (settings.DryRun)
            {
                await DisplayDryRunInfoAsync(options);
                return 0;
            }

            // Create devcontainer configuration
            var result = await WithSpinnerAsync("Creating devcontainer configuration", async () =>
            {
                return await _devcontainerService.CreateConfigurationAsync(options);
            });

            // Process result
            if (result.Success)
            {
                DisplaySuccess("Devcontainer configuration created successfully");
                
                if (result.Configuration != null)
                {
                    DisplayConfigurationSummary(
                        result.Configuration.Name,
                        result.Configuration.Image,
                        result.Configuration.Features.Keys,
                        GetExtensionsFromCustomizations(result.Configuration.Customizations),
                        result.Configuration.ForwardPorts
                    );
                }

                DisplayGeneratedFiles(result.GeneratedFiles);

                if (result.Warnings.Any())
                {
                    AnsiConsole.WriteLine();
                    DisplayWarning("Warnings:");
                    foreach (var warning in result.Warnings)
                    {
                        DisplayWarning($"  • {warning}");
                    }
                }

                DisplayNextSteps(outputPath);
                return 0;
            }
            else
            {
                DisplayError($"Failed to create devcontainer configuration: {result.Message}");
                
                if (result.Errors.Any())
                {
                    foreach (var error in result.Errors)
                    {
                        DisplayError($"  • {error}");
                    }
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            DisplayError($"An error occurred: {ex.Message}");
            if (settings.Verbose)
            {
                DisplayError($"Stack trace: {ex.StackTrace}");
            }
            return 1;
        }
    }

    private Task<DevcontainerOptions> BuildDevcontainerOptionsAsync(DevcontainerInitSettings settings, string outputPath)
    {
        var options = new DevcontainerOptions
        {
            Name = settings.Name ?? Path.GetFileName(outputPath),
            OutputPath = outputPath,
            Template = settings.Template,
            BaseImage = settings.BaseImage,
            UseDockerCompose = settings.UseDockerCompose,
            Interactive = false,
            IncludeDevPackages = settings.IncludeDevPackages,
            EnableGitCredentials = settings.EnableGitCredentials,
            PostCreateCommand = settings.PostCreateCommand,
            WorkspaceFolder = settings.WorkspaceFolder
        };

        // Parse features
        if (settings.Features != null && settings.Features.Any())
        {
            foreach (var featureSpec in settings.Features)
            {
                // Handle features in format "feature-id" or "feature-id@version"
                var featureId = featureSpec.Split('@')[0];
                options.Features.Add(featureId);
            }
        }

        // Parse extensions
        if (settings.Extensions != null && settings.Extensions.Any())
        {
            options.Extensions.AddRange(settings.Extensions);
        }

        // Parse ports
        if (settings.Ports != null && settings.Ports.Any())
        {
            foreach (var portSpec in settings.Ports)
            {
                if (int.TryParse(portSpec, out var port))
                {
                    options.ForwardPorts.Add(port);
                }
                else
                {
                    DisplayWarning($"Invalid port specification: {portSpec}");
                }
            }
        }

        // Parse environment variables
        if (settings.EnvironmentVariables != null && settings.EnvironmentVariables.Any())
        {
            foreach (var envVar in settings.EnvironmentVariables)
            {
                var parts = envVar.Split('=', 2);
                if (parts.Length == 2)
                {
                    options.EnvironmentVariables[parts[0]] = parts[1];
                }
                else
                {
                    DisplayWarning($"Invalid environment variable format: {envVar}. Expected KEY=VALUE");
                }
            }
        }

        return Task.FromResult(options);
    }

    private Task DisplayDryRunInfoAsync(DevcontainerOptions options)
    {
        DisplayInfo("DRY RUN - No files will be created");
        AnsiConsole.WriteLine();

        var panel = new Panel(
            new Rows(
                new Text($"Name: {options.Name}"),
                new Text($"Output Path: {options.OutputPath}"),
                new Text($"Template: {options.Template ?? "None"}"),
                new Text($"Base Image: {options.BaseImage ?? "Default"}"),
                new Text($"Features ({options.Features.Count}): {string.Join(", ", options.Features)}"),
                new Text($"Extensions ({options.Extensions.Count}): {string.Join(", ", options.Extensions)}"),
                new Text($"Forwarded Ports: {string.Join(", ", options.ForwardPorts)}"),
                new Text($"Docker Compose: {(options.UseDockerCompose ? "Yes" : "No")}"),
                new Text($"Post-Create Command: {options.PostCreateCommand ?? "None"}"),
                new Text($"Environment Variables: {options.EnvironmentVariables.Count}")
            )
        )
        .Header("[cyan]Configuration Preview[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        if (options.Features.Any())
        {
            AnsiConsole.WriteLine();
            DisplayInfo("Selected features will be validated for dependencies and conflicts");
        }

        // Simulate file generation
        var expectedFiles = new[]
        {
            Path.Combine(options.OutputPath, ".devcontainer", "devcontainer.json"),
            Path.Combine(options.OutputPath, ".devcontainer", "README.md")
        };

        if (options.UseDockerCompose)
        {
            expectedFiles = expectedFiles.Concat(new[]
            {
                Path.Combine(options.OutputPath, ".devcontainer", "docker-compose.yml")
            }).ToArray();
        }

        AnsiConsole.WriteLine();
        DisplayInfo("Files that would be generated:");
        foreach (var file in expectedFiles)
        {
            var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
            DisplayProgress($"• {relativePath}");
        }
        return Task.CompletedTask;
    }

    private static IEnumerable<string> GetExtensionsFromCustomizations(Dictionary<string, object> customizations)
    {
        if (customizations.TryGetValue("vscode", out var vscodeConfig) &&
            vscodeConfig is Dictionary<string, object> vscodeDict &&
            vscodeDict.TryGetValue("extensions", out var extensions) &&
            extensions is IEnumerable<object> extensionList)
        {
            return extensionList.Select(e => e.ToString() ?? string.Empty).Where(s => !string.IsNullOrEmpty(s));
        }
        
        return Enumerable.Empty<string>();
    }

    private void DisplayNextSteps(string outputPath)
    {
        AnsiConsole.WriteLine();
        
        var nextSteps = new Panel(
            new Rows(
                new Text("1. Open the project in VS Code"),
                new Text("2. Install the Dev Containers extension if not already installed"),
                new Text("3. Press F1 and run 'Dev Containers: Reopen in Container'"),
                new Text("4. Wait for the container to build and start"),
                new Text("5. Start developing in your isolated environment!")
            )
        )
        .Header("[green]Next Steps[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(nextSteps);

        AnsiConsole.WriteLine();
        DisplayInfo($"Devcontainer configuration created in: {Path.Combine(outputPath, ".devcontainer")}");
        DisplayInfo("Use 'pks devcontainer validate' to verify the configuration");
    }


    private static bool IsValidImageName(string imageName)
    {
        // Basic validation for container image names
        // Full validation would be more complex, this is a simplified version
        return !string.IsNullOrWhiteSpace(imageName) &&
               !imageName.StartsWith('-') &&
               !imageName.EndsWith('-') &&
               imageName.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '/' || c == ':' || c == '-' || c == '_');
    }
}