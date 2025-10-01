using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;

namespace PKS.Commands;

/// <summary>
/// Command to initialize a new agentic development project with devcontainer support
/// </summary>
public class InitCommand : Command<InitCommand.Settings>
{
    private readonly INuGetTemplateDiscoveryService _templateDiscovery;
    private readonly IAnsiConsole _console;
    private readonly string? _workingDirectory;

    public InitCommand(
        INuGetTemplateDiscoveryService templateDiscovery,
        IAnsiConsole? console = null,
        string? workingDirectory = null)
    {
        _templateDiscovery = templateDiscovery;
        _console = console ?? AnsiConsole.Console;
        _workingDirectory = workingDirectory;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT_NAME]")]
        [Description("The name of the project to initialize")]
        public string? ProjectName { get; set; }

        [CommandOption("-t|--template <TEMPLATE>")]
        [Description("Template short name to use (e.g., pks-claude-dotnet9)")]
        public string? Template { get; set; }

        [CommandOption("-d|--description <DESCRIPTION>")]
        [Description("Optional project description")]
        public string? Description { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force overwrite existing files")]
        public bool Force { get; set; }

        [CommandOption("--nuget-source <SOURCE>")]
        [Description("Custom NuGet source/feed to search for templates")]
        public string? NuGetSource { get; set; }

        [CommandOption("--tag <TAG>")]
        [Description("NuGet tag to filter templates (default: pks-templates)")]
        [DefaultValue("pks-templates")]
        public string Tag { get; set; } = "pks-templates";

        [CommandOption("--agentic")]
        [Description("Enable agentic features and AI automation")]
        public bool EnableAgentic { get; set; }

        [CommandOption("--mcp")]
        [Description("Enable Model Context Protocol (MCP) integration")]
        public bool EnableMcp { get; set; }
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Display PKS CLI banner
        DisplayBanner();

        // Interactive project name collection if not provided
        if (string.IsNullOrEmpty(settings.ProjectName))
        {
            settings.ProjectName = _console.Ask<string>("\n[cyan]What's the[/] [green]name[/] [cyan]of your project?[/]");
        }

        // Validate project name
        if (string.IsNullOrWhiteSpace(settings.ProjectName) ||
            settings.ProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            _console.MarkupLine("[red]❌ Invalid project name. Please use valid file name characters.[/]");
            return 1;
        }

        try
        {
            // Discover available templates from NuGet
            List<NuGetDevcontainerTemplate> templates = new();

            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("[cyan]Discovering available templates...[/]", async ctx =>
                {
                    var sources = string.IsNullOrEmpty(settings.NuGetSource)
                        ? null
                        : new[] { settings.NuGetSource };

                    templates = await _templateDiscovery.DiscoverTemplatesAsync(
                        tag: settings.Tag,
                        sources: sources,
                        cancellationToken: CancellationToken.None);
                });

            if (!templates.Any())
            {
                _console.MarkupLine($"[yellow]⚠️  No templates found with tag '{settings.Tag}'.[/]");
                _console.MarkupLine("[dim]Try running: pks template list --all[/]");
                return 1;
            }

            // Interactive template selection if not provided
            NuGetDevcontainerTemplate selectedTemplate;

            if (string.IsNullOrEmpty(settings.Template))
            {
                selectedTemplate = _console.Prompt(
                    new SelectionPrompt<NuGetDevcontainerTemplate>()
                        .Title("\n[cyan]Which template would you like to use?[/]")
                        .PageSize(10)
                        .MoreChoicesText("[grey](Move up and down to reveal more templates)[/]")
                        .AddChoices(templates)
                        .UseConverter(template =>
                            $"{GetTemplateIcon(template)} {template.Title} [dim]({template.PackageId})[/]"));
            }
            else
            {
                selectedTemplate = templates.FirstOrDefault(t =>
                    t.ShortNames.Any(sn => sn.Equals(settings.Template, StringComparison.OrdinalIgnoreCase)) ||
                    t.PackageId.Equals(settings.Template, StringComparison.OrdinalIgnoreCase))!;

                if (selectedTemplate == null)
                {
                    _console.MarkupLine($"[red]❌ Template '{settings.Template}' not found.[/]");
                    _console.MarkupLine($"\n[cyan]Available templates:[/]");

                    var table = new Table();
                    table.AddColumn("Short Name");
                    table.AddColumn("Package ID");
                    table.AddColumn("Description");

                    foreach (var t in templates)
                    {
                        table.AddRow(
                            t.ShortNames.Length > 0 ? string.Join(", ", t.ShortNames) : "[dim]N/A[/]",
                            t.PackageId,
                            t.Description ?? "[dim]No description[/]");
                    }

                    _console.Write(table);
                    return 1;
                }
            }

            // Get project description
            if (string.IsNullOrEmpty(settings.Description))
            {
                var defaultDesc = selectedTemplate.Description ?? "An agentic development project";
                settings.Description = _console.Prompt(
                    new TextPrompt<string>("[cyan]What's the[/] [green]description/objective[/] [cyan]of your project?[/]")
                        .DefaultValue(defaultDesc));
            }

            // Create target directory
            var workingDir = _workingDirectory ?? Environment.CurrentDirectory;
            var targetDirectory = Path.Combine(workingDir, settings.ProjectName);

            if (Directory.Exists(targetDirectory) && !settings.Force)
            {
                _console.MarkupLine($"[red]❌ Directory '{settings.ProjectName}' already exists. Use --force to overwrite.[/]");
                return 1;
            }

            // Install/extract template
            _console.MarkupLine($"\n[cyan]📦 Installing template:[/] {selectedTemplate.Title} v{selectedTemplate.Version}");

            NuGetTemplateExtractionResult extractionResult;

            await _console.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync($"Extracting template to '{settings.ProjectName}'...", async ctx =>
                {
                    extractionResult = await _templateDiscovery.ExtractTemplateAsync(
                        selectedTemplate.PackageId,
                        selectedTemplate.Version,
                        targetDirectory,
                        cancellationToken: CancellationToken.None);
                });

            // Display success message
            var panel = new Panel($"""
                🎉 [bold green]Project '{settings.ProjectName}' initialized successfully![/]

                [cyan1]Template:[/] {selectedTemplate.Title}
                [cyan1]Package:[/] {selectedTemplate.PackageId} v{selectedTemplate.Version}
                [cyan1]Description:[/] {settings.Description}
                [cyan1]Location:[/] {targetDirectory}

                [bold cyan]Next steps:[/]
                • [cyan]cd {settings.ProjectName}[/] - Navigate to your project
                • [cyan]code .[/] - Open in VS Code
                • [cyan]Select "Reopen in Container"[/] to start development
                {(settings.EnableAgentic ? "\n• [cyan]pks agent create[/] - Add AI development agents" : "")}
                {(settings.EnableMcp ? "\n• [cyan]pks mcp init[/] - Configure MCP integration" : "")}

                [dim]Ready for agentic development! 🚀[/]
                """)
                .Border(BoxBorder.Double)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]🚀 PKS Project Ready[/] ");

            _console.Write(panel);

            return 0;
        }
        catch (Exception ex)
        {
            _console.WriteException(ex);
            return 1;
        }
    }

    private void DisplayBanner()
    {
        _console.Write(new FigletText("PKS CLI")
            .LeftJustified()
            .Color(Color.Cyan1));

        _console.MarkupLine("[cyan]🤖 Agentic Development Environment Setup[/]");
        _console.MarkupLine("[dim]Discover and install devcontainer templates from NuGet[/]\n");
    }

    private string GetTemplateIcon(NuGetDevcontainerTemplate template)
    {
        // Try to determine icon from package ID or tags
        var id = template.PackageId.ToLowerInvariant();
        var tags = template.Tags.Select(t => t.ToLowerInvariant()).ToList();

        if (id.Contains("dotnet") || id.Contains("csharp") || tags.Contains("dotnet"))
            return "🔷";
        if (id.Contains("python") || tags.Contains("python"))
            return "🐍";
        if (id.Contains("node") || id.Contains("javascript") || tags.Contains("nodejs"))
            return "📗";
        if (id.Contains("go") || tags.Contains("go") || tags.Contains("golang"))
            return "🐹";
        if (id.Contains("rust") || tags.Contains("rust"))
            return "🦀";
        if (id.Contains("java") || tags.Contains("java"))
            return "☕";
        if (id.Contains("claude") || id.Contains("ai") || tags.Contains("claude"))
            return "🤖";
        if (id.Contains("aspire") || tags.Contains("aspire"))
            return "✨";

        return "📦";
    }
}
