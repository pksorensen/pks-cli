using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Interactive wizard command for comprehensive devcontainer setup
/// Provides step-by-step guidance with multi-selection prompts and configuration preview
/// </summary>
public class DevcontainerWizardCommand : DevcontainerCommand<DevcontainerWizardSettings>
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IVsCodeExtensionService _extensionService;

    public DevcontainerWizardCommand(
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService,
        IVsCodeExtensionService extensionService)
    {
        _devcontainerService = devcontainerService ?? throw new ArgumentNullException(nameof(devcontainerService));
        _featureRegistry = featureRegistry ?? throw new ArgumentNullException(nameof(featureRegistry));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _extensionService = extensionService ?? throw new ArgumentNullException(nameof(extensionService));
    }

    public override int Execute(CommandContext context, DevcontainerWizardSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, DevcontainerWizardSettings settings)
    {
        try
        {
            DisplayBanner("Interactive Wizard");

            AnsiConsole.MarkupLine("[cyan]Welcome to the PKS Devcontainer Wizard![/]");
            AnsiConsole.MarkupLine("[dim]This wizard will guide you through creating a comprehensive devcontainer configuration.[/]");
            AnsiConsole.WriteLine();

            // Validate output path first
            var outputPath = ValidateAndResolvePath(settings.OutputPath);
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

            // Check for existing configuration
            var existingConfigPath = Path.Combine(outputPath, ".devcontainer", "devcontainer.json");
            if (File.Exists(existingConfigPath) && !settings.Force)
            {
                var overwrite = PromptConfirmation(
                    $"A devcontainer configuration already exists at {existingConfigPath}. Do you want to overwrite it?",
                    false);
                
                if (!overwrite)
                {
                    DisplayInfo("Operation cancelled by user");
                    return 0;
                }
            }

            var options = new DevcontainerOptions
            {
                OutputPath = outputPath,
                Interactive = true
            };

            // Step 1: Basic Configuration
            await ConfigureBasicSettingsAsync(options, settings);

            // Step 2: Template Selection (if not skipped)
            if (!settings.SkipTemplates)
            {
                await SelectTemplateAsync(options, settings);
            }

            // Step 3: Feature Selection (if not skipped)
            if (!settings.SkipFeatures)
            {
                await SelectFeaturesAsync(options, settings);
            }

            // Step 4: Extension Selection (if not skipped)
            if (!settings.SkipExtensions)
            {
                await SelectExtensionsAsync(options, settings);
            }

            // Step 5: Advanced Configuration (expert mode only)
            if (settings.ExpertMode)
            {
                await ConfigureAdvancedSettingsAsync(options);
            }

            // Step 6: Configuration Review and Confirmation
            if (!await ReviewConfigurationAsync(options, settings))
            {
                DisplayInfo("Configuration cancelled by user");
                return 0;
            }

            // Step 7: Generate Configuration
            if (settings.DryRun)
            {
                await DisplayConfigurationPreviewAsync(options);
                return 0;
            }

            var result = await WithSpinnerAsync("Creating devcontainer configuration", async () =>
            {
                return await _devcontainerService.CreateConfigurationAsync(options);
            });

            if (result.Success)
            {
                DisplaySuccess("Devcontainer wizard completed successfully!");
                AnsiConsole.WriteLine();

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
                DisplayNextSteps();

                if (result.Warnings.Any())
                {
                    AnsiConsole.WriteLine();
                    DisplayValidationResults(Enumerable.Empty<string>(), result.Warnings);
                }

                return 0;
            }
            else
            {
                DisplayError($"Failed to create devcontainer configuration: {result.Message}");
                DisplayValidationResults(result.Errors, result.Warnings);
                return 1;
            }
        }
        catch (Exception ex)
        {
            DisplayError($"An error occurred during the wizard: {ex.Message}");
            if (settings.Verbose)
            {
                DisplayError($"Stack trace: {ex.StackTrace}");
            }
            return 1;
        }
    }

    private async Task ConfigureBasicSettingsAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 1: Basic Configuration[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        // Project name
        options.Name = PromptText(
            "What would you like to name your devcontainer?",
            Path.GetFileName(options.OutputPath));

        // Base image selection
        if (!settings.QuickSetup)
        {
            var useTemplate = PromptConfirmation(
                "Would you like to start with a predefined template? (Recommended for beginners)",
                true);

            if (!useTemplate)
            {
                var imageOptions = new[]
                {
                    "mcr.microsoft.com/vscode/devcontainers/base:ubuntu",
                    "mcr.microsoft.com/vscode/devcontainers/dotnet:latest",
                    "mcr.microsoft.com/vscode/devcontainers/javascript-node:latest",
                    "mcr.microsoft.com/vscode/devcontainers/python:latest",
                    "mcr.microsoft.com/vscode/devcontainers/java:latest",
                    "Custom image..."
                };

                var selectedImage = PromptSelection("Select a base image:", imageOptions);

                if (selectedImage == "Custom image...")
                {
                    options.BaseImage = PromptText("Enter the custom base image name:");
                }
                else
                {
                    options.BaseImage = selectedImage;
                }
            }
        }

        // Docker Compose option
        if (settings.ExpertMode)
        {
            options.UseDockerCompose = PromptConfirmation(
                "Do you want to use Docker Compose for multi-container development?",
                false);
        }

        AnsiConsole.WriteLine();
    }

    private async Task SelectTemplateAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 2: Template Selection[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var templates = await WithSpinnerAsync("Loading available templates", async () =>
        {
            return await _templateService.GetAvailableTemplatesAsync();
        });

        if (!templates.Any())
        {
            DisplayWarning("No templates available. Skipping template selection.");
            return;
        }

        // Group templates by category for better display
        var groupedTemplates = templates.GroupBy(t => t.Category).ToList();

        DisplayInfo($"Found {templates.Count} available templates across {groupedTemplates.Count} categories");
        AnsiConsole.WriteLine();

        // Show template categories
        var categoryChoices = groupedTemplates.Select(g => g.Key).Concat(new[] { "None - Skip template" }).ToArray();
        var selectedCategory = PromptSelection("Select a template category:", categoryChoices);

        if (selectedCategory == "None - Skip template")
        {
            DisplayInfo("Template selection skipped");
            return;
        }

        // Show templates in selected category
        var categoryTemplates = groupedTemplates.First(g => g.Key == selectedCategory).ToList();
        var templateChoices = categoryTemplates.Select(t => $"{t.Name} - {t.Description}").ToArray();
        
        if (settings.Verbose)
        {
            var templateTable = new Table()
                .Title($"[cyan]Templates in {selectedCategory}[/]")
                .Border(TableBorder.Rounded)
                .AddColumn("Name")
                .AddColumn("Description")
                .AddColumn("Features");

            foreach (var template in categoryTemplates)
            {
                templateTable.AddRow(
                    $"[yellow]{template.Name}[/]",
                    $"[white]{template.Description}[/]",
                    $"[dim]{template.RequiredFeatures.Length + template.OptionalFeatures.Length}[/]"
                );
            }

            AnsiConsole.Write(templateTable);
            AnsiConsole.WriteLine();
        }

        var selectedTemplateDisplay = PromptSelection("Select a template:", templateChoices);
        var selectedTemplate = categoryTemplates.First(t => selectedTemplateDisplay.StartsWith(t.Name));

        options.Template = selectedTemplate.Id;
        options.BaseImage = selectedTemplate.BaseImage;

        DisplaySuccess($"Selected template: {selectedTemplate.Name}");
        
        if (selectedTemplate.RequiredFeatures.Any())
        {
            DisplayInfo($"This template includes {selectedTemplate.RequiredFeatures.Length} required features");
            options.Features.AddRange(selectedTemplate.RequiredFeatures);
        }

        AnsiConsole.WriteLine();
    }

    private async Task SelectFeaturesAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 3: Feature Selection[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var allFeatures = await WithSpinnerAsync("Loading available features", async () =>
        {
            return await _featureRegistry.GetAvailableFeaturesAsync();
        });

        if (!allFeatures.Any())
        {
            DisplayWarning("No features available. Skipping feature selection.");
            return;
        }

        // Filter out deprecated features unless explicitly requested
        var availableFeatures = allFeatures.Where(f => !f.IsDeprecated).ToList();
        
        DisplayInfo($"Found {availableFeatures.Count} available features");

        if (settings.QuickSetup)
        {
            // Quick setup: show only popular/essential features
            var popularFeatures = availableFeatures
                .Where(f => f.Tags.Contains("popular") || f.Tags.Contains("essential"))
                .Take(10)
                .ToList();

            if (popularFeatures.Any())
            {
                var selectedFeatures = PromptMultiSelection(
                    "Select popular features to include (use space to select, enter to confirm):",
                    popularFeatures.Select(f => $"{f.Name} - {f.Description}")
                );

                foreach (var selected in selectedFeatures)
                {
                    var feature = popularFeatures.First(f => selected.StartsWith(f.Name));
                    if (!options.Features.Contains(feature.Id))
                    {
                        options.Features.Add(feature.Id);
                    }
                }
            }
        }
        else
        {
            // Full feature selection by category
            var categories = await _featureRegistry.GetAvailableCategoriesAsync();
            var selectedCategories = PromptMultiSelection(
                "Select feature categories to explore:",
                categories.Concat(new[] { "All categories" })
            );

            List<DevcontainerFeature> featuresForSelection;
            if (selectedCategories.Contains("All categories"))
            {
                featuresForSelection = availableFeatures;
            }
            else
            {
                featuresForSelection = new List<DevcontainerFeature>();
                foreach (var category in selectedCategories)
                {
                    var categoryFeatures = await _featureRegistry.GetFeaturesByCategory(category);
                    featuresForSelection.AddRange(categoryFeatures.Where(f => !f.IsDeprecated));
                }
            }

            if (featuresForSelection.Any())
            {
                // Display features in a table for better overview
                if (settings.Verbose)
                {
                    DisplayFeatureTable(featuresForSelection.Select(f => (f.Id, f.Name, f.Description, f.Category)));
                    AnsiConsole.WriteLine();
                }

                var featureChoices = featuresForSelection.Select(f => $"{f.Name} - {f.Description}").ToList();
                var selectedFeatures = PromptMultiSelection(
                    "Select features to include (use space to select, enter to confirm):",
                    featureChoices
                );

                foreach (var selected in selectedFeatures)
                {
                    var feature = featuresForSelection.First(f => selected.StartsWith(f.Name));
                    if (!options.Features.Contains(feature.Id))
                    {
                        options.Features.Add(feature.Id);
                    }
                }
            }
        }

        if (options.Features.Any())
        {
            DisplaySuccess($"Selected {options.Features.Count} features");
            
            // Resolve dependencies
            var resolution = await _devcontainerService.ResolveFeatureDependenciesAsync(options.Features);
            if (resolution.Success)
            {
                if (resolution.MissingDependencies.Any())
                {
                    var addDependencies = PromptConfirmation(
                        $"Some features have dependencies that will be automatically added. Continue?",
                        true);
                    
                    if (addDependencies)
                    {
                        options.Features = resolution.ResolvedFeatures.Select(f => f.Id).ToList();
                        DisplayInfo($"Added {resolution.MissingDependencies.Count} dependency features");
                    }
                }
            }
            else
            {
                DisplayWarning($"Feature dependency resolution failed: {resolution.ErrorMessage}");
            }
        }
        else
        {
            DisplayInfo("No features selected");
        }

        AnsiConsole.WriteLine();
    }

    private async Task SelectExtensionsAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 4: VS Code Extension Selection[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        // Get recommended extensions based on selected features
        var recommendedExtensions = await WithSpinnerAsync("Finding recommended extensions", async () =>
        {
            return await _devcontainerService.GetRecommendedExtensionsAsync(options.Features);
        });

        if (recommendedExtensions.Any())
        {
            DisplayInfo($"Found {recommendedExtensions.Count} recommended extensions based on your selected features");
            
            if (settings.Verbose)
            {
                DisplayExtensionTable(recommendedExtensions.Select(e => (e.Id, e.Name, e.Publisher, e.Description)));
                AnsiConsole.WriteLine();
            }

            var includeRecommended = PromptConfirmation(
                "Include all recommended extensions?",
                true);

            if (includeRecommended)
            {
                options.Extensions.AddRange(recommendedExtensions.Select(e => e.Id));
                DisplaySuccess($"Added {recommendedExtensions.Count} recommended extensions");
            }
            else
            {
                // Allow individual selection
                var extensionChoices = recommendedExtensions.Select(e => $"{e.Name} ({e.Publisher}) - {e.Description}").ToList();
                var selectedExtensions = PromptMultiSelection(
                    "Select specific extensions to include:",
                    extensionChoices
                );

                foreach (var selected in selectedExtensions)
                {
                    var extension = recommendedExtensions.First(e => selected.Contains(e.Name) && selected.Contains(e.Publisher));
                    options.Extensions.Add(extension.Id);
                }

                DisplaySuccess($"Added {selectedExtensions.Count} extensions");
            }
        }

        // Allow manual extension addition
        if (!settings.QuickSetup)
        {
            var addCustomExtensions = PromptConfirmation(
                "Would you like to add custom extensions?",
                false);

            if (addCustomExtensions)
            {
                var continueAdding = true;
                while (continueAdding)
                {
                    var extensionId = PromptText("Enter extension ID (e.g., ms-dotnettools.csharp):");
                    if (!string.IsNullOrWhiteSpace(extensionId))
                    {
                        options.Extensions.Add(extensionId.Trim());
                        DisplaySuccess($"Added extension: {extensionId}");
                    }

                    continueAdding = PromptConfirmation("Add another extension?", false);
                }
            }
        }

        AnsiConsole.WriteLine();
    }

    private async Task ConfigureAdvancedSettingsAsync(DevcontainerOptions options)
    {
        var rule = new Rule("[cyan]Step 5: Advanced Configuration[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        DisplayInfo("Expert mode: Configure advanced settings");

        // Port forwarding
        var configurePorts = PromptConfirmation("Configure port forwarding?", false);
        if (configurePorts)
        {
            var continueAdding = true;
            while (continueAdding)
            {
                var portStr = PromptText("Enter port number to forward:");
                if (int.TryParse(portStr, out var port) && port > 0 && port <= 65535)
                {
                    options.ForwardPorts.Add(port);
                    DisplaySuccess($"Added port: {port}");
                }
                else
                {
                    DisplayWarning("Invalid port number");
                }

                continueAdding = PromptConfirmation("Add another port?", false);
            }
        }

        // Environment variables
        var configureEnv = PromptConfirmation("Configure environment variables?", false);
        if (configureEnv)
        {
            var continueAdding = true;
            while (continueAdding)
            {
                var envName = PromptText("Environment variable name:");
                var envValue = PromptText("Environment variable value:");
                
                if (!string.IsNullOrWhiteSpace(envName))
                {
                    options.EnvironmentVariables[envName.Trim()] = envValue ?? string.Empty;
                    DisplaySuccess($"Added environment variable: {envName}");
                }

                continueAdding = PromptConfirmation("Add another environment variable?", false);
            }
        }

        // Post-create command
        var configureCommand = PromptConfirmation("Configure post-create command?", false);
        if (configureCommand)
        {
            options.PostCreateCommand = PromptText("Enter command to run after container creation:");
        }

        AnsiConsole.WriteLine();
    }

    private async Task<bool> ReviewConfigurationAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 6: Configuration Review[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        DisplayConfigurationSummary(
            options.Name,
            options.BaseImage,
            options.Features,
            options.Extensions,
            options.ForwardPorts
        );

        if (settings.Verbose && options.EnvironmentVariables.Any())
        {
            AnsiConsole.WriteLine();
            DisplayInfo("Environment Variables:");
            foreach (var kvp in options.EnvironmentVariables)
            {
                AnsiConsole.MarkupLine($"[dim]  • {kvp.Key} = {kvp.Value}[/]");
            }
        }

        AnsiConsole.WriteLine();
        return PromptConfirmation("Create devcontainer with this configuration?", true);
    }

    private async Task DisplayConfigurationPreviewAsync(DevcontainerOptions options)
    {
        DisplayInfo("DRY RUN - Configuration preview");
        AnsiConsole.WriteLine();

        // Create a mock configuration for preview
        var mockResult = await _devcontainerService.CreateConfigurationAsync(options);
        
        var expectedFiles = new[]
        {
            Path.Combine(options.OutputPath, ".devcontainer", "devcontainer.json"),
            Path.Combine(options.OutputPath, ".devcontainer", "README.md")
        };

        DisplayInfo("Files that would be generated:");
        foreach (var file in expectedFiles)
        {
            var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
            AnsiConsole.MarkupLine($"[dim]  • {relativePath}[/]");
        }
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

    private void DisplayNextSteps()
    {
        var nextSteps = new Panel(
            new Rows(
                new Text("1. Open this project in VS Code"),
                new Text("2. Install the Dev Containers extension (ms-vscode-remote.remote-containers)"),
                new Text("3. Press F1 and run 'Dev Containers: Reopen in Container'"),
                new Text("4. Wait for the container to build (this may take several minutes)"),
                new Text("5. Your development environment is ready!"),
                new Text(""),
                new Text("[dim]Tip: Use 'pks devcontainer validate' to check your configuration[/]")
            )
        )
        .Header("[green]🎉 What's Next?[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(nextSteps);
    }
}