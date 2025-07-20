using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Devcontainer;

/// <summary>
/// Command for listing available devcontainer features, templates, and extensions
/// Supports filtering, searching, and detailed information display
/// </summary>
public class DevcontainerListCommand : Command<DevcontainerListSettings>
{
    private readonly IDevcontainerFeatureRegistry _featureRegistry;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IVsCodeExtensionService _extensionService;

    public DevcontainerListCommand(
        IDevcontainerFeatureRegistry featureRegistry,
        IDevcontainerTemplateService templateService,
        IVsCodeExtensionService extensionService)
    {
        _featureRegistry = featureRegistry ?? throw new ArgumentNullException(nameof(featureRegistry));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _extensionService = extensionService ?? throw new ArgumentNullException(nameof(extensionService));
    }

    public override int Execute(CommandContext context, DevcontainerListSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, DevcontainerListSettings settings)
    {
        try
        {
            var panel = new Panel("[bold cyan]🐳 PKS Devcontainer Resources[/]")
                .BorderStyle(Style.Parse("cyan"))
                .Padding(1, 0);

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();

            // Determine what to list based on command name and options
            var commandName = context.Name?.ToLower();
            
            return commandName switch
            {
                "features" => await ListFeaturesAsync(settings),
                "templates" => await ListTemplatesAsync(settings),
                "extensions" => await ListExtensionsAsync(settings),
                _ => await ListAllResourcesAsync(settings)
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            if (settings.Verbose)
            {
                AnsiConsole.MarkupLine($"[red]Stack trace: {ex.StackTrace}[/]");
            }
            return 1;
        }
    }

    private async Task<int> ListAllResourcesAsync(DevcontainerListSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Available devcontainer resources:[/]");
        AnsiConsole.WriteLine();

        // Get counts for summary
        var featuresTask = GetFilteredFeaturesAsync(settings);
        var templatesTask = GetFilteredTemplatesAsync(settings);

        await Task.WhenAll(featuresTask, templatesTask);

        var features = await featuresTask;
        var templates = await templatesTask;

        // Display summary
        var summaryTable = new Table()
            .Title("[cyan]Resource Summary[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("Resource Type")
            .AddColumn("Count")
            .AddColumn("Description");

        summaryTable.AddRow(
            "[yellow]Features[/]",
            $"[white]{features.Count}[/]",
            "[dim]Development environment features and tools[/]"
        );

        summaryTable.AddRow(
            "[yellow]Templates[/]",
            $"[white]{templates.Count}[/]",
            "[dim]Pre-configured devcontainer templates[/]"
        );

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Display available commands
        var commandsPanel = new Panel(
            new Rows(
                new Text("pks devcontainer list features    - List available features"),
                new Text("pks devcontainer list templates   - List available templates"),
                new Text("pks devcontainer list extensions  - List recommended extensions"),
                new Text(""),
                new Text("[dim]Use --help with any command for more options[/]")
            )
        )
        .Header("[cyan]Available Commands[/]")
        .Border(BoxBorder.Rounded);

        AnsiConsole.Write(commandsPanel);

        return 0;
    }

    private async Task<int> ListFeaturesAsync(DevcontainerListSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Loading available devcontainer features...[/]");

        var features = await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching features", async _ =>
            {
                if (settings.Refresh)
                {
                    await _featureRegistry.RefreshFeaturesAsync();
                }
                return await GetFilteredFeaturesAsync(settings);
            });

        if (!features.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No features found matching the specified criteria.[/]");
            return 0;
        }

        DisplayFeatureList(features, settings);
        DisplayFeatureStatistics(features);

        return 0;
    }

    private async Task<int> ListTemplatesAsync(DevcontainerListSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Loading available devcontainer templates...[/]");

        var templates = await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching templates", async _ =>
            {
                return await GetFilteredTemplatesAsync(settings);
            });

        if (!templates.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No templates found matching the specified criteria.[/]");
            return 0;
        }

        DisplayTemplateList(templates, settings);
        DisplayTemplateStatistics(templates);

        return 0;
    }

    private async Task<int> ListExtensionsAsync(DevcontainerListSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]Loading VS Code extensions...[/]");

        var extensions = await AnsiConsole.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching extensions", async _ =>
            {
                return await GetRecommendedExtensionsAsync(settings);
            });

        if (!extensions.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No extensions found.[/]");
            return 0;
        }

        DisplayExtensionList(extensions, settings);

        return 0;
    }

    private async Task<List<DevcontainerFeature>> GetFilteredFeaturesAsync(DevcontainerListSettings settings)
    {
        List<DevcontainerFeature> features;

        if (!string.IsNullOrEmpty(settings.SearchQuery))
        {
            features = await _featureRegistry.SearchFeaturesAsync(settings.SearchQuery);
        }
        else if (!string.IsNullOrEmpty(settings.Category))
        {
            features = await _featureRegistry.GetFeaturesByCategory(settings.Category);
        }
        else
        {
            features = await _featureRegistry.GetAvailableFeaturesAsync();
        }

        // Apply additional filters
        if (!settings.ShowDeprecated)
        {
            features = features.Where(f => !f.IsDeprecated).ToList();
        }

        return features.OrderBy(f => f.Category).ThenBy(f => f.Name).ToList();
    }

    private async Task<List<DevcontainerTemplate>> GetFilteredTemplatesAsync(DevcontainerListSettings settings)
    {
        var templates = await _templateService.GetAvailableTemplatesAsync();

        // Apply filters
        if (!string.IsNullOrEmpty(settings.SearchQuery))
        {
            templates = templates.Where(t => 
                t.Name.Contains(settings.SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(settings.SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Category.Contains(settings.SearchQuery, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (!string.IsNullOrEmpty(settings.Category))
        {
            templates = templates.Where(t => 
                t.Category.Equals(settings.Category, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        return templates.OrderBy(t => t.Category).ThenBy(t => t.Name).ToList();
    }

    private async Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(DevcontainerListSettings settings)
    {
        List<VsCodeExtension> extensions;

        if (!string.IsNullOrEmpty(settings.SearchQuery))
        {
            extensions = await _extensionService.SearchExtensionsAsync(settings.SearchQuery);
        }
        else if (!string.IsNullOrEmpty(settings.Category))
        {
            extensions = await _extensionService.GetExtensionsByCategoryAsync(settings.Category);
        }
        else
        {
            // Get all available categories and then get extensions from each
            var categories = await _extensionService.GetAvailableCategoriesAsync();
            extensions = new List<VsCodeExtension>();
            
            foreach (var category in categories)
            {
                var categoryExtensions = await _extensionService.GetExtensionsByCategoryAsync(category);
                extensions.AddRange(categoryExtensions);
            }
        }

        return extensions.OrderBy(e => e.Category).ThenBy(e => e.Name).ToList();
    }

    private void DisplayFeatureList(List<DevcontainerFeature> features, DevcontainerListSettings settings)
    {
        if (settings.ShowDetails)
        {
            DisplayDetailedFeatureList(features);
        }
        else
        {
            DisplayCompactFeatureList(features, settings);
        }
    }

    private void DisplayCompactFeatureList(List<DevcontainerFeature> features, DevcontainerListSettings settings)
    {
        var table = new Table()
            .Title($"[cyan]Devcontainer Features ({features.Count} found)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Category")
            .AddColumn("Description");

        foreach (var feature in features.Take(settings.Limit ?? features.Count))
        {
            var description = feature.Description.Length > 60 
                ? feature.Description[..57] + "..."
                : feature.Description;

            var nameColor = feature.IsDeprecated ? "strikethrough dim" : "white";
            var deprecatedIcon = feature.IsDeprecated ? " [red](deprecated)[/]" : "";

            table.AddRow(
                $"[yellow]{feature.Id}[/]",
                $"[{nameColor}]{feature.Name}[/{nameColor}]{deprecatedIcon}",
                $"[cyan]{feature.Category}[/]",
                $"[dim]{description}[/]"
            );
        }

        AnsiConsole.Write(table);

        if (settings.Limit.HasValue && features.Count > settings.Limit.Value)
        {
            AnsiConsole.MarkupLine($"[dim]Showing {settings.Limit.Value} of {features.Count} features. Use --limit to show more.[/]");
        }
    }

    private void DisplayDetailedFeatureList(List<DevcontainerFeature> features)
    {
        foreach (var feature in features)
        {
            var panel = new Panel(
                new Rows(
                    new Text($"ID: {feature.Id}"),
                    new Text($"Version: {feature.Version}"),
                    new Text($"Repository: {feature.Repository}"),
                    new Text($"Description: {feature.Description}"),
                    new Text($"Tags: {string.Join(", ", feature.Tags)}"),
                    feature.Dependencies.Any() 
                        ? new Text($"Dependencies: {string.Join(", ", feature.Dependencies)}")
                        : new Text("Dependencies: None"),
                    feature.ConflictsWith.Any()
                        ? new Text($"Conflicts: {string.Join(", ", feature.ConflictsWith)}")
                        : new Text("Conflicts: None")
                )
            )
            .Header($"[yellow]{feature.Name}[/] ([cyan]{feature.Category}[/])")
            .Border(feature.IsDeprecated ? BoxBorder.Double : BoxBorder.Rounded)
            .BorderStyle(feature.IsDeprecated ? Style.Parse("red") : Style.Parse("cyan"));

            if (feature.IsDeprecated && !string.IsNullOrEmpty(feature.DeprecationMessage))
            {
                panel.Header($"[strikethrough red]{feature.Name}[/] [red](DEPRECATED)[/]");
            }

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }
    }

    private void DisplayTemplateList(List<DevcontainerTemplate> templates, DevcontainerListSettings settings)
    {
        if (settings.ShowDetails)
        {
            DisplayDetailedTemplateList(templates);
        }
        else
        {
            DisplayCompactTemplateList(templates, settings);
        }
    }

    private void DisplayCompactTemplateList(List<DevcontainerTemplate> templates, DevcontainerListSettings settings)
    {
        var table = new Table()
            .Title($"[cyan]Devcontainer Templates ({templates.Count} found)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Category")
            .AddColumn("Base Image")
            .AddColumn("Features");

        foreach (var template in templates.Take(settings.Limit ?? templates.Count))
        {
            var baseImage = template.BaseImage.Length > 30 
                ? template.BaseImage[..27] + "..."
                : template.BaseImage;

            var featureCount = template.RequiredFeatures.Length + template.OptionalFeatures.Length;

            table.AddRow(
                $"[yellow]{template.Id}[/]",
                $"[white]{template.Name}[/]",
                $"[cyan]{template.Category}[/]",
                $"[dim]{baseImage}[/]",
                $"[green]{featureCount}[/]"
            );
        }

        AnsiConsole.Write(table);

        if (settings.Limit.HasValue && templates.Count > settings.Limit.Value)
        {
            AnsiConsole.MarkupLine($"[dim]Showing {settings.Limit.Value} of {templates.Count} templates. Use --limit to show more.[/]");
        }
    }

    private void DisplayDetailedTemplateList(List<DevcontainerTemplate> templates)
    {
        foreach (var template in templates)
        {
            var content = new List<Spectre.Console.Rendering.IRenderable>
            {
                new Text($"ID: {template.Id}"),
                new Text($"Base Image: {template.BaseImage}"),
                new Text($"Description: {template.Description}")
            };

            if (template.RequiredFeatures.Any())
            {
                content.Add(new Text($"Required Features: {string.Join(", ", template.RequiredFeatures)}"));
            }

            if (template.OptionalFeatures.Any())
            {
                content.Add(new Text($"Optional Features: {string.Join(", ", template.OptionalFeatures)}"));
            }

            if (template.DefaultPorts.Any())
            {
                content.Add(new Text($"Default Ports: {string.Join(", ", template.DefaultPorts)}"));
            }

            if (!string.IsNullOrEmpty(template.DefaultPostCreateCommand))
            {
                content.Add(new Text($"Post-Create Command: {template.DefaultPostCreateCommand}"));
            }

            if (template.RequiresDockerCompose)
            {
                content.Add(new Text("Requires Docker Compose: Yes"));
            }

            var panel = new Panel(new Rows(content))
                .Header($"[yellow]{template.Name}[/] ([cyan]{template.Category}[/])")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("cyan"));

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }
    }

    private void DisplayExtensionList(List<VsCodeExtension> extensions, DevcontainerListSettings settings)
    {
        var table = new Table()
            .Title($"[cyan]VS Code Extensions ({extensions.Count} found)[/]")
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Name")
            .AddColumn("Publisher")
            .AddColumn("Category")
            .AddColumn("Essential");

        foreach (var extension in extensions.Take(settings.Limit ?? extensions.Count))
        {
            var essentialIcon = extension.IsEssential ? "[green]✓[/]" : "[dim]-[/]";

            table.AddRow(
                $"[yellow]{extension.Id}[/]",
                $"[white]{extension.Name}[/]",
                $"[green]{extension.Publisher}[/]",
                $"[cyan]{extension.Category}[/]",
                essentialIcon
            );
        }

        AnsiConsole.Write(table);

        if (settings.Limit.HasValue && extensions.Count > settings.Limit.Value)
        {
            AnsiConsole.MarkupLine($"[dim]Showing {settings.Limit.Value} of {extensions.Count} extensions. Use --limit to show more.[/]");
        }
    }

    private void DisplayFeatureStatistics(List<DevcontainerFeature> features)
    {
        var categories = features.GroupBy(f => f.Category).OrderByDescending(g => g.Count()).ToList();
        var deprecated = features.Count(f => f.IsDeprecated);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total features: {features.Count} | Deprecated: {deprecated} | Categories: {categories.Count}[/]");

        if (categories.Any())
        {
            AnsiConsole.MarkupLine("[dim]Top categories:[/]");
            foreach (var category in categories.Take(5))
            {
                AnsiConsole.MarkupLine($"[dim]  • {category.Key}: {category.Count()}[/]");
            }
        }
    }

    private void DisplayTemplateStatistics(List<DevcontainerTemplate> templates)
    {
        var categories = templates.GroupBy(t => t.Category).OrderByDescending(g => g.Count()).ToList();
        var dockerComposeRequired = templates.Count(t => t.RequiresDockerCompose);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[dim]Total templates: {templates.Count} | Docker Compose: {dockerComposeRequired} | Categories: {categories.Count}[/]");

        if (categories.Any())
        {
            AnsiConsole.MarkupLine("[dim]Categories:[/]");
            foreach (var category in categories)
            {
                AnsiConsole.MarkupLine($"[dim]  • {category.Key}: {category.Count()}[/]");
            }
        }
    }

}

/// <summary>
/// Settings for the devcontainer list command
/// </summary>
public class DevcontainerListSettings : DevcontainerSettings
{
    [CommandOption("-c|--category <CATEGORY>")]
    [Description("Filter by category")]
    public string? Category { get; set; }

    [CommandOption("-s|--search <QUERY>")]
    [Description("Search by name or description")]
    public string? SearchQuery { get; set; }

    [CommandOption("--show-details")]
    [Description("Show detailed information")]
    public bool ShowDetails { get; set; }

    [CommandOption("--show-deprecated")]
    [Description("Include deprecated features")]
    public bool ShowDeprecated { get; set; }

    [CommandOption("--refresh")]
    [Description("Refresh from remote sources")]
    public bool Refresh { get; set; }

    [CommandOption("--limit <COUNT>")]
    [Description("Limit number of results")]
    public int? Limit { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; }
}