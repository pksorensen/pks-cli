using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using PKS.Infrastructure.Initializers.Service;

public class InitCommand : Command<InitCommand.Settings>
{
    private readonly IInitializationService _initializationService;

    public InitCommand(IInitializationService initializationService)
    {
        _initializationService = initializationService;
    }
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT_NAME]")]
        [Description("The name of the project to initialize")]
        public string? ProjectName { get; set; }

        [CommandOption("-t|--template <TEMPLATE>")]
        [Description("Project template to use (api, web, console, agent)")]
        [DefaultValue("console")]
        public string Template { get; set; } = "console";

        [CommandOption("-a|--agentic")]
        [Description("Enable agentic features and AI automation")]
        public bool EnableAgentic { get; set; }

        [CommandOption("-m|--mcp")]
        [Description("Enable Model Context Protocol (MCP) integration")]
        public bool EnableMcp { get; set; }

        [CommandOption("-f|--force")]
        [Description("Force overwrite existing files")]
        public bool Force { get; set; }

        [CommandOption("-d|--description <DESCRIPTION>")]
        [Description("Optional project description")]
        public string? Description { get; set; }

        [CommandOption("--devcontainer")]
        [Description("Enable devcontainer configuration for isolated development")]
        public bool EnableDevcontainer { get; set; }

        [CommandOption("--devcontainer-features <FEATURES>")]
        [Description("Comma-separated list of devcontainer features (e.g., dotnet,docker-in-docker,python)")]
        public string? DevcontainerFeatures { get; set; }
    }

    public override int Execute(CommandContext context, Settings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Interactive project name collection if not provided
        if (string.IsNullOrEmpty(settings.ProjectName))
        {
            settings.ProjectName = AnsiConsole.Ask<string>("What's the [green]name[/] of your project?");
        }

        // Interactive project description collection if not provided
        if (string.IsNullOrEmpty(settings.Description))
        {
            settings.Description = AnsiConsole.Ask<string>(
                "What's the [cyan]description/objective[/] of your project?",
                defaultValue: $"A .NET project initialized with PKS CLI using {settings.Template} template");
        }

        // Prepare options for initializers
        var options = new Dictionary<string, object?>
        {
            { "agentic", settings.EnableAgentic },
            { "mcp", settings.EnableMcp },
            { "template", settings.Template },
            { "description", settings.Description },
            { "devcontainer", settings.EnableDevcontainer }
        };

        // Parse devcontainer features if provided
        if (!string.IsNullOrEmpty(settings.DevcontainerFeatures))
        {
            var features = settings.DevcontainerFeatures
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToArray();
            options["devcontainer-features"] = features;
        }

        // Create target directory path
        var targetDirectory = Path.Combine(Environment.CurrentDirectory, settings.ProjectName);

        // Validate target directory first
        var validation = await _initializationService.ValidateTargetDirectoryAsync(targetDirectory, settings.Force);
        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine($"[red]❌ {validation.ErrorMessage}[/]");
            return 1;
        }

        // Create initialization context
        var initContext = _initializationService.CreateContext(
            settings.ProjectName,
            settings.Template,
            targetDirectory,
            settings.Force,
            options);

        try
        {
            // Run initialization with status display
            InitializationSummary summary = null!;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync($"Initializing project '{settings.ProjectName}'...", async ctx =>
                {
                    ctx.Status($"Setting up {settings.Template} template...");
                    await Task.Delay(200);

                    ctx.Status("Creating project structure...");
                    await Task.Delay(300);

                    ctx.Status("Running initializers...");
                    summary = await _initializationService.InitializeProjectAsync(initContext);

                    ctx.Status("Finalizing project setup...");
                    await Task.Delay(200);
                });

            // Display success/failure message
            if (summary.Success)
            {
                var agenticInfo = settings.EnableAgentic ? "\n• [cyan]Agentic features:[/] [green]Enabled[/]" : "";
                var devcontainerInfo = settings.EnableDevcontainer ? "\n• [cyan]Devcontainer:[/] [green]Configured[/]" : "";

                var panel = new Panel($"""
                🎉 [bold green]Project '{settings.ProjectName}' initialized successfully![/]
                
                [cyan1]Template:[/] {settings.Template}
                [cyan1]Description:[/] {settings.Description}
                [cyan1]Files created:[/] {summary.FilesCreated}
                [cyan1]Duration:[/] {summary.Duration.TotalSeconds:F1}s{agenticInfo}{devcontainerInfo}
                
                Next steps:
                • [cyan]cd {settings.ProjectName}[/] - Navigate to your project{(settings.EnableDevcontainer ? "\n• [cyan]code .[/] - Open in VS Code and 'Reopen in Container'" : "")}
                • [cyan]pks agent create[/] - Add AI development agents  
                • [cyan]pks deploy --watch[/] - Deploy with intelligent monitoring
                
                [dim]Ready to revolutionize your .NET development experience! 🚀[/]
                """)
                .Border(BoxBorder.Double)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]🚀 PKS Project Ready[/] ");

                AnsiConsole.Write(panel);

                // Show warnings if any
                if (summary.WarningsCount > 0)
                {
                    AnsiConsole.MarkupLine($"\n[yellow]⚠️ {summary.WarningsCount} warning(s) occurred during initialization[/]");
                }

                return 0;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]❌ Initialization failed: {summary.ErrorMessage}[/]");

                // Show detailed error information if available
                if (summary.ErrorsCount > 0)
                {
                    AnsiConsole.MarkupLine($"[red]{summary.ErrorsCount} error(s) encountered[/]");
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}