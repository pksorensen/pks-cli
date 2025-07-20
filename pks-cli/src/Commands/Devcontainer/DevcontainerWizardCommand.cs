using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using Microsoft.Extensions.Logging;

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
    private readonly INuGetTemplateDiscoveryService _nugetTemplateService;
    private readonly ILogger<DevcontainerWizardCommand>? _logger;

    public DevcontainerWizardCommand(
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService,
        IVsCodeExtensionService extensionService,
        INuGetTemplateDiscoveryService nugetTemplateService,
        ILogger<DevcontainerWizardCommand>? logger = null)
    {
        _devcontainerService = devcontainerService ?? throw new ArgumentNullException(nameof(devcontainerService));
        _featureRegistry = featureRegistry ?? throw new ArgumentNullException(nameof(featureRegistry));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _extensionService = extensionService ?? throw new ArgumentNullException(nameof(extensionService));
        _nugetTemplateService = nugetTemplateService ?? throw new ArgumentNullException(nameof(nugetTemplateService));
        _logger = logger;
    }

    public override int Execute(CommandContext context, DevcontainerWizardSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, DevcontainerWizardSettings settings)
    {
        try
        {
            DisplayBanner("Interactive Wizard");
            
            if (settings.Debug)
            {
                DisplayInfo("DEBUG: Debug mode enabled - will show detailed diagnostic information");
                DisplayInfo($"DEBUG: Using output path: {settings.OutputPath}");
                DisplayInfo($"DEBUG: FromTemplates: {settings.FromTemplates}");
                DisplayInfo($"DEBUG: Sources count: {settings.Sources?.Length ?? 0}");
                DisplayInfo($"DEBUG: AddSources count: {settings.AddSources?.Length ?? 0}");
            }

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

            // Step 3.5: Environment Variables (if template requires them)
            await ConfigureEnvironmentVariablesAsync(options, settings);

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

    private Task ConfigureBasicSettingsAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
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
        return Task.CompletedTask;
    }

    private async Task SelectTemplateAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        var rule = new Rule("[cyan]Step 2: Template Selection[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        var templates = new List<DevcontainerTemplate>();

        if (settings.FromTemplates)
        {
            // Discover templates from NuGet
            var nugetTemplates = await WithSpinnerAsync("Discovering NuGet templates", async () =>
            {
                try
                {
                    var sources = GetNuGetSources(settings);
                    DisplayNuGetSourcesInfo(sources, settings);
                    
                    if (settings.Debug)
                    {
                        DisplayInfo($"DEBUG: Starting template discovery with {sources.Count()} sources");
                        foreach (var source in sources)
                        {
                            DisplayInfo($"DEBUG: Source: {source}");
                        }
                    }
                    
                    // Try multiple tags for better compatibility
                    var discoveredTemplates = new List<NuGetDevcontainerTemplate>();
                    
                    if (settings.Debug)
                    {
                        DisplayInfo("DEBUG: Trying 'pks-devcontainers' tag first");
                    }
                    
                    // First try the specific pks-devcontainers tag
                    var pksTemplates = await _nugetTemplateService.DiscoverTemplatesAsync(
                        tag: "pks-devcontainers",
                        sources: sources);
                    discoveredTemplates.AddRange(pksTemplates);
                    
                    if (settings.Debug)
                    {
                        DisplayInfo($"DEBUG: Found {pksTemplates.Count} templates with 'pks-devcontainers' tag");
                        foreach (var template in pksTemplates)
                        {
                            DisplayInfo($"DEBUG: - {template.PackageId} v{template.Version}");
                        }
                    }
                    
                    // Also try the general pks-cli tag for backward compatibility
                    if (!discoveredTemplates.Any())
                    {
                        if (settings.Debug)
                        {
                            DisplayInfo("DEBUG: No templates found with 'pks-devcontainers', trying 'pks-cli' tag");
                        }
                        
                        var pksCliTemplates = await _nugetTemplateService.DiscoverTemplatesAsync(
                            tag: "pks-cli",
                            sources: sources);
                            
                        if (settings.Debug)
                        {
                            DisplayInfo($"DEBUG: Found {pksCliTemplates.Count} templates with 'pks-cli' tag");
                            foreach (var template in pksCliTemplates)
                            {
                                DisplayInfo($"DEBUG: - {template.PackageId} v{template.Version} (Tags: {string.Join(", ", template.Tags)})");
                            }
                        }
                        
                        // Filter for devcontainer-related templates
                        var devcontainerTemplates = pksCliTemplates.Where(t => 
                            t.Tags.Any(tag => tag.Contains("devcontainer", StringComparison.OrdinalIgnoreCase)) ||
                            t.Title.Contains("devcontainer", StringComparison.OrdinalIgnoreCase) ||
                            t.Description.Contains("devcontainer", StringComparison.OrdinalIgnoreCase)).ToList();
                            
                        if (settings.Debug)
                        {
                            DisplayInfo($"DEBUG: Filtered to {devcontainerTemplates.Count} devcontainer-related templates");
                            foreach (var template in devcontainerTemplates)
                            {
                                DisplayInfo($"DEBUG: Filtered - {template.PackageId} v{template.Version}");
                            }
                        }
                        
                        discoveredTemplates.AddRange(devcontainerTemplates);
                    }

                    return discoveredTemplates.Select(nt => nt.ToDevcontainerTemplate()).ToList();
                }
                catch (Exception ex)
                {
                    DisplayWarning($"Error discovering NuGet templates: {ex.Message}");
                    if (settings.Debug)
                    {
                        DisplayInfo($"DEBUG: Exception type: {ex.GetType().Name}");
                        DisplayInfo($"DEBUG: Exception details: {ex}");
                    }
                    DisplayInfo("Falling back to built-in templates...");
                    return new List<DevcontainerTemplate>();
                }
            });

            if (nugetTemplates.Any())
            {
                templates.AddRange(nugetTemplates);
                DisplaySuccess($"Found {nugetTemplates.Count} templates from NuGet packages");
            }
            else
            {
                DisplayWarning("No NuGet templates found with compatible tags.");
                DisplayInfo("Using built-in templates...");
            }
        }

        // Always load built-in templates as fallback or supplement
        var builtInTemplates = await WithSpinnerAsync("Loading built-in templates", async () =>
        {
            return await _templateService.GetAvailableTemplatesAsync();
        });

        if (builtInTemplates.Any())
        {
            templates.AddRange(builtInTemplates);
            if (settings.FromTemplates && !templates.Any())
            {
                DisplayInfo($"Using {builtInTemplates.Count} built-in templates");
            }
        }

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
        options.SelectedTemplate = selectedTemplate;
        options.BaseImage = selectedTemplate.BaseImage;
        options.TemplateVersion = selectedTemplate.Version;
        
        // Store NuGet sources used for template discovery
        if (settings.FromTemplates && settings.Sources?.Any() == true)
        {
            options.NuGetSources.AddRange(settings.Sources);
        }

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

        // Check if a template was selected and has specific feature requirements
        if (options.SelectedTemplate != null)
        {
            if (settings.Debug)
            {
                DisplayInfo($"DEBUG: Template '{options.SelectedTemplate.Name}' selected");
                DisplayInfo($"DEBUG: Required features: {string.Join(", ", options.SelectedTemplate.RequiredFeatures)}");
                DisplayInfo($"DEBUG: Optional features: {string.Join(", ", options.SelectedTemplate.OptionalFeatures)}");
            }

            // If template has no optional features, skip feature selection
            if (!options.SelectedTemplate.OptionalFeatures.Any())
            {
                DisplayInfo($"Template '{options.SelectedTemplate.Name}' has all required features included. Skipping additional feature selection.");
                return;
            }

            // Only show features that are relevant to this template
            var allFeatures = await WithSpinnerAsync("Loading template-relevant features", async () =>
            {
                var features = await _featureRegistry.GetAvailableFeaturesAsync();
                return features.Where(f => options.SelectedTemplate.OptionalFeatures.Contains(f.Id)).ToList();
            });

            if (!allFeatures.Any())
            {
                DisplayInfo($"Template '{options.SelectedTemplate.Name}' has no additional optional features. Skipping feature selection.");
                return;
            }

            DisplayInfo($"Found {allFeatures.Count} optional features for template '{options.SelectedTemplate.Name}'");
            
            // Show template-specific optional features
            var templateFeatures = allFeatures.Where(f => !f.IsDeprecated).ToList();
            var selectedFeatures = PromptMultiSelection(
                "Select optional features to include with this template:",
                templateFeatures.Select(f => $"{f.Name} - {f.Description}")
            );

            foreach (var selected in selectedFeatures)
            {
                var feature = templateFeatures.First(f => selected.StartsWith(f.Name));
                if (!options.Features.Contains(feature.Id))
                {
                    options.Features.Add(feature.Id);
                }
            }

            if (selectedFeatures.Any())
            {
                DisplaySuccess($"Added {selectedFeatures.Count} optional features");
            }
            
            return;
        }

        // Fallback to original behavior if no template was selected
        var allAvailableFeatures = await WithSpinnerAsync("Loading available features", async () =>
        {
            return await _featureRegistry.GetAvailableFeaturesAsync();
        });

        if (!allAvailableFeatures.Any())
        {
            DisplayWarning("No features available. Skipping feature selection.");
            return;
        }

        // Filter out deprecated features unless explicitly requested
        var availableFeatures = allAvailableFeatures.Where(f => !f.IsDeprecated).ToList();
        
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

    private Task ConfigureAdvancedSettingsAsync(DevcontainerOptions options)
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
        return Task.CompletedTask;
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

    private IEnumerable<string> GetNuGetSources(DevcontainerWizardSettings settings)
    {
        var sources = new List<string>();

        // Add custom sources if specified
        if (settings.Sources?.Any() == true)
        {
            sources.AddRange(settings.Sources);
        }
        else
        {
            // Default NuGet source
            sources.Add("https://api.nuget.org/v3/index.json");
        }

        // Add additional sources if specified
        if (settings.AddSources?.Any() == true)
        {
            sources.AddRange(settings.AddSources);
        }

        return sources;
    }

    private void DisplayNuGetSourcesInfo(IEnumerable<string> sources, DevcontainerWizardSettings settings)
    {
        if (settings.Verbose)
        {
            AnsiConsole.MarkupLine("[dim]NuGet Sources:[/]");
            foreach (var source in sources)
            {
                AnsiConsole.MarkupLine($"[dim]  • {source}[/]");
            }
            AnsiConsole.WriteLine();
        }
    }

    private async Task<string?> PromptTemplateWithAutoCompletionAsync(List<DevcontainerTemplate> templates, DevcontainerWizardSettings settings)
    {
        if (!settings.FromTemplates)
        {
            // Use regular selection for built-in templates
            var templateChoices = templates.Select(t => $"{t.Name} - {t.Description}").ToArray();
            var selectedTemplateDisplay = PromptSelection("Select a template:", templateChoices);
            return templates.First(t => selectedTemplateDisplay.StartsWith(t.Name)).Id;
        }

        // Enhanced template selection with search and auto-completion for NuGet templates
        AnsiConsole.MarkupLine("[cyan]Template Search (type to filter, TAB for suggestions):[/]");
        
        var query = string.Empty;
        var filteredTemplates = templates;

        while (true)
        {
            AnsiConsole.Write($"Template: {query}");
            
            var key = Console.ReadKey(true);
            
            if (key.Key == ConsoleKey.Enter && filteredTemplates.Any())
            {
                AnsiConsole.WriteLine();
                break;
            }
            else if (key.Key == ConsoleKey.Tab && query.Length > 0)
            {
                // Show auto-completion suggestions
                var suggestions = await GetAutoCompletionSuggestionsAsync(query, settings);
                if (suggestions.Any())
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[dim]Auto-completion suggestions:[/]");
                    foreach (var suggestion in suggestions.Take(5))
                    {
                        AnsiConsole.MarkupLine($"[dim]  • {suggestion.PackageId} - {suggestion.Description}[/]");
                    }
                    AnsiConsole.WriteLine();
                }
            }
            else if (key.Key == ConsoleKey.Backspace && query.Length > 0)
            {
                query = query.Substring(0, query.Length - 1);
                filteredTemplates = FilterTemplates(templates, query);
                Console.Clear();
                DisplayFilteredTemplates(filteredTemplates);
            }
            else if (char.IsLetterOrDigit(key.KeyChar) || key.KeyChar == '-' || key.KeyChar == '_')
            {
                query += key.KeyChar;
                filteredTemplates = FilterTemplates(templates, query);
                Console.Clear();
                DisplayFilteredTemplates(filteredTemplates);
            }
        }

        if (filteredTemplates.Any())
        {
            var templateChoices = filteredTemplates.Select(t => $"{t.Name} - {t.Description}").ToArray();
            var selectedTemplateDisplay = PromptSelection("Select from filtered templates:", templateChoices);
            return filteredTemplates.First(t => selectedTemplateDisplay.StartsWith(t.Name)).Id;
        }

        return null;
    }

    private async Task<List<NuGetTemplateSearchResult>> GetAutoCompletionSuggestionsAsync(string query, DevcontainerWizardSettings settings)
    {
        try
        {
            var sources = GetNuGetSources(settings);
            return await _nugetTemplateService.SearchTemplatesAsync(
                query, 
                tag: "pks-devcontainers", 
                sources: sources, 
                maxResults: 10);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get auto-completion suggestions");
            return new List<NuGetTemplateSearchResult>();
        }
    }

    private static List<DevcontainerTemplate> FilterTemplates(List<DevcontainerTemplate> templates, string query)
    {
        if (string.IsNullOrEmpty(query))
            return templates;

        var lowerQuery = query.ToLowerInvariant();
        return templates.Where(t =>
            t.Name.ToLowerInvariant().Contains(lowerQuery) ||
            t.Description.ToLowerInvariant().Contains(lowerQuery) ||
            t.Id.ToLowerInvariant().Contains(lowerQuery)
        ).ToList();
    }

    private static void DisplayFilteredTemplates(List<DevcontainerTemplate> templates)
    {
        AnsiConsole.MarkupLine($"[dim]Found {templates.Count} matching templates:[/]");
        foreach (var template in templates.Take(5))
        {
            AnsiConsole.MarkupLine($"[dim]  • {template.Name} - {template.Description}[/]");
        }
        if (templates.Count > 5)
        {
            AnsiConsole.MarkupLine($"[dim]  ... and {templates.Count - 5} more[/]");
        }
        AnsiConsole.WriteLine();
    }

    private async Task ConfigureEnvironmentVariablesAsync(DevcontainerOptions options, DevcontainerWizardSettings settings)
    {
        // Only show this step if a template was selected and has required environment variables
        if (options.SelectedTemplate?.RequiredEnvVars?.Any() != true)
        {
            if (settings.Debug)
            {
                DisplayInfo("DEBUG: No required environment variables defined for template, skipping env var configuration");
            }
            return;
        }

        var rule = new Rule("[cyan]Step 3.5: Environment Variable Configuration[/]").RuleStyle("cyan");
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();

        if (settings.Debug)
        {
            DisplayInfo($"DEBUG: Template '{options.SelectedTemplate.Name}' requires {options.SelectedTemplate.RequiredEnvVars.Count} environment variables");
            foreach (var envVar in options.SelectedTemplate.RequiredEnvVars)
            {
                DisplayInfo($"DEBUG: - {envVar.Key}: {envVar.Value}");
            }
        }

        DisplayInfo($"Template '{options.SelectedTemplate.Name}' requires the following environment variables:");
        AnsiConsole.WriteLine();

        foreach (var envVar in options.SelectedTemplate.RequiredEnvVars)
        {
            var envName = envVar.Key;
            var envDescription = envVar.Value;

            // Check if environment variable is already set in the system
            var existingValue = Environment.GetEnvironmentVariable(envName);
            string? providedValue = null;

            if (!string.IsNullOrEmpty(existingValue))
            {
                var useExisting = PromptConfirmation(
                    $"Environment variable '{envName}' is already set. Use existing value?",
                    true);

                if (useExisting)
                {
                    providedValue = existingValue;
                    DisplaySuccess($"Using existing value for {envName}");
                }
            }

            if (string.IsNullOrEmpty(providedValue))
            {
                // Prompt for the environment variable value
                var isSecret = envName.ToLowerInvariant().Contains("token") || 
                              envName.ToLowerInvariant().Contains("secret") || 
                              envName.ToLowerInvariant().Contains("key") ||
                              envName.ToLowerInvariant().Contains("password");

                if (isSecret)
                {
                    DisplayInfo($"🔐 {envDescription}");
                    providedValue = PromptSecret($"Enter value for {envName}:");
                }
                else
                {
                    DisplayInfo($"📝 {envDescription}");
                    providedValue = PromptText($"Enter value for {envName}:");
                }
            }

            if (!string.IsNullOrEmpty(providedValue))
            {
                options.EnvironmentVariables[envName] = providedValue;
                DisplaySuccess($"✓ Configured {envName}");
            }
            else
            {
                DisplayWarning($"⚠️ {envName} was not configured - template may not work correctly");
            }

            AnsiConsole.WriteLine();
        }

        if (options.EnvironmentVariables.Any())
        {
            DisplaySuccess($"Configured {options.EnvironmentVariables.Count} environment variables");
            
            if (settings.Verbose)
            {
                var envTable = new Table()
                    .Title("[cyan]Configured Environment Variables[/]")
                    .Border(TableBorder.Rounded)
                    .AddColumn("Variable")
                    .AddColumn("Value");

                foreach (var envVar in options.EnvironmentVariables)
                {
                    var displayValue = envVar.Key.ToLowerInvariant().Contains("token") || 
                                     envVar.Key.ToLowerInvariant().Contains("secret") || 
                                     envVar.Key.ToLowerInvariant().Contains("key") ||
                                     envVar.Key.ToLowerInvariant().Contains("password")
                        ? "***********" // Hide sensitive values
                        : envVar.Value;

                    envTable.AddRow(
                        $"[yellow]{envVar.Key}[/]",
                        $"[dim]{displayValue}[/]"
                    );
                }

                AnsiConsole.Write(envTable);
            }
        }

        AnsiConsole.WriteLine();
    }

    private string PromptSecret(string prompt)
    {
        AnsiConsole.Write($"{prompt} ");
        var password = string.Empty;
        ConsoleKeyInfo keyInfo;
        
        do
        {
            keyInfo = Console.ReadKey(true);
            
            if (keyInfo.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password.Substring(0, password.Length - 1);
                Console.Write("\b \b");
            }
            else if (keyInfo.Key != ConsoleKey.Enter && keyInfo.Key != ConsoleKey.Backspace)
            {
                password += keyInfo.KeyChar;
                Console.Write("*");
            }
        } while (keyInfo.Key != ConsoleKey.Enter);
        
        Console.WriteLine();
        return password;
    }
}