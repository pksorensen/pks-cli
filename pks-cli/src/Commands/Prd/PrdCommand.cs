using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using System.Text.Json;

namespace PKS.Commands.Prd;

/// <summary>
/// Settings for the main PRD command
/// </summary>
public class PrdMainSettings : CommandSettings
{
}

/// <summary>
/// Main PRD command with subcommands for PRD management
/// </summary>
[Description("Manage Product Requirements Documents (PRDs) with AI-powered generation")]
public class PrdCommand : Command<PrdMainSettings>
{
    public override int Execute(CommandContext context, PrdMainSettings settings)
    {
        // Display help when no subcommand is provided
        AnsiConsole.MarkupLine("[cyan]PKS PRD Management[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Available commands:");
        AnsiConsole.MarkupLine("  [green]pks prd generate[/] <idea> - Generate PRD from idea description");
        AnsiConsole.MarkupLine("  [green]pks prd load[/] <file>     - Load and parse existing PRD");
        AnsiConsole.MarkupLine("  [green]pks prd requirements[/]    - List requirements from PRD");
        AnsiConsole.MarkupLine("  [green]pks prd status[/]          - Show PRD status and progress");
        AnsiConsole.MarkupLine("  [green]pks prd validate[/]        - Validate PRD completeness");
        AnsiConsole.MarkupLine("  [green]pks prd template[/] <name> - Generate PRD template");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Use [yellow]pks prd <command> --help[/] for more information about a command.");
        
        return 0;
    }
}

/// <summary>
/// Generate PRD from idea description
/// </summary>
[Description("Generate a comprehensive PRD from an idea description")]
public class PrdGenerateCommand : Command<PrdGenerateSettings>
{
    private readonly IPrdService _prdService;

    public PrdGenerateCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdGenerateSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdGenerateSettings settings)
    {
        try
        {
            // Interactive mode for detailed input
            if (settings.Interactive)
            {
                await CollectInteractiveInputAsync(settings);
            }

            // Validate required inputs
            if (string.IsNullOrEmpty(settings.IdeaDescription))
            {
                AnsiConsole.MarkupLine("[red]Error: Idea description is required[/]");
                return 1;
            }

            // Set default project name if not provided
            if (string.IsNullOrEmpty(settings.ProjectName))
            {
                settings.ProjectName = Path.GetFileName(Environment.CurrentDirectory);
            }

            // Set default output path if not provided
            if (string.IsNullOrEmpty(settings.OutputPath))
            {
                var docsDir = Path.Combine(Environment.CurrentDirectory, "docs");
                if (!Directory.Exists(docsDir))
                {
                    Directory.CreateDirectory(docsDir);
                }
                settings.OutputPath = Path.Combine(docsDir, "PRD.md");
            }

            // Check if file exists and force flag
            if (File.Exists(settings.OutputPath) && !settings.Force)
            {
                var overwrite = AnsiConsole.Confirm($"PRD file already exists at [yellow]{settings.OutputPath}[/]. Overwrite?");
                if (!overwrite)
                {
                    AnsiConsole.MarkupLine("[yellow]Operation cancelled[/]");
                    return 0;
                }
            }

            // Parse template type
            if (!Enum.TryParse<PrdTemplateType>(settings.Template, true, out var templateType))
            {
                AnsiConsole.MarkupLine($"[red]Invalid template type: {settings.Template}[/]");
                AnsiConsole.MarkupLine("Valid types: standard, technical, mobile, web, api, minimal, enterprise");
                return 1;
            }

            // Create generation request
            var request = new PrdGenerationRequest
            {
                IdeaDescription = settings.IdeaDescription,
                ProjectName = settings.ProjectName,
                TargetAudience = settings.TargetAudience ?? "",
                Stakeholders = ParseCommaSeparatedList(settings.Stakeholders),
                BusinessContext = settings.BusinessContext ?? "",
                TechnicalConstraints = ParseCommaSeparatedList(settings.TechnicalConstraints)
            };

            // Generate PRD with progress indicator
            PrdDocument document = null!;
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync("Generating PRD...", async ctx =>
                {
                    ctx.Status("Analyzing idea and requirements...");
                    await Task.Delay(500);
                    
                    ctx.Status("Generating sections and requirements...");
                    document = await _prdService.GeneratePrdAsync(request, settings.OutputPath);
                    
                    ctx.Status("Formatting and saving PRD...");
                    await Task.Delay(300);
                });

            // Display success message
            var panel = new Panel($"""
                🎉 [bold green]PRD generated successfully![/]
                
                [cyan1]Project:[/] {settings.ProjectName}
                [cyan1]Output:[/] {settings.OutputPath}
                [cyan1]Requirements:[/] {document.Requirements.Count}
                [cyan1]User Stories:[/] {document.UserStories.Count}
                [cyan1]Sections:[/] {document.Sections.Count}
                
                Next steps:
                • [cyan]pks prd validate[/] - Validate PRD completeness
                • [cyan]pks prd requirements[/] - Review requirements
                • [cyan]pks prd status[/] - Track progress
                
                [dim]Your AI-powered PRD is ready for review! 📋[/]
                """)
                .Border(BoxBorder.Double)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]🚀 PRD Generation Complete[/] ");

            AnsiConsole.Write(panel);

            // Show validation summary if verbose
            if (settings.Verbose)
            {
                var validation = await _prdService.ValidatePrdAsync(document);
                DisplayValidationSummary(validation);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private async Task CollectInteractiveInputAsync(PrdGenerateSettings settings)
    {
        AnsiConsole.MarkupLine("[cyan]🤖 Interactive PRD Generation[/]");
        AnsiConsole.WriteLine();

        if (string.IsNullOrEmpty(settings.IdeaDescription))
        {
            settings.IdeaDescription = AnsiConsole.Ask<string>("What's your [green]idea or project description[/]?");
        }

        if (string.IsNullOrEmpty(settings.ProjectName))
        {
            settings.ProjectName = AnsiConsole.Ask<string>("What's the [cyan]project name[/]?", 
                Path.GetFileName(Environment.CurrentDirectory));
        }

        if (string.IsNullOrEmpty(settings.TargetAudience))
        {
            settings.TargetAudience = AnsiConsole.Ask<string>("Who is the [yellow]target audience[/]?", "End users");
        }

        var templateOptions = new[] { "standard", "technical", "mobile", "web", "api", "minimal", "enterprise" };
        settings.Template = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select PRD [blue]template type[/]:")
                .AddChoices(templateOptions)
                .HighlightStyle("cyan"));

        var addMoreDetails = AnsiConsole.Confirm("Would you like to add [yellow]business context[/] and [yellow]stakeholders[/]?");
        if (addMoreDetails)
        {
            settings.BusinessContext = AnsiConsole.Ask<string>("Business context:", "");
            settings.Stakeholders = AnsiConsole.Ask<string>("Stakeholders (comma-separated):", "");
        }

        await Task.CompletedTask;
    }

    private List<string> ParseCommaSeparatedList(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return new List<string>();

        return input.Split(',', StringSplitOptions.RemoveEmptyEntries)
                   .Select(s => s.Trim())
                   .ToList();
    }

    private void DisplayValidationSummary(PrdValidationResult validation)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title("[bold]PRD Validation Summary[/]")
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Completeness Score", $"{validation.CompletenessScore:F1}%");
        table.AddRow("Validation Status", validation.IsValid ? "[green]Valid[/]" : "[red]Issues Found[/]");
        table.AddRow("Errors", validation.Errors.Count.ToString());
        table.AddRow("Warnings", validation.Warnings.Count.ToString());
        table.AddRow("Suggestions", validation.Suggestions.Count.ToString());

        AnsiConsole.Write(table);
    }
}

/// <summary>
/// Load and parse existing PRD
/// </summary>
[Description("Load and parse an existing PRD file")]
public class PrdLoadCommand : Command<PrdLoadSettings>
{
    private readonly IPrdService _prdService;

    public PrdLoadCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdLoadSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdLoadSettings settings)
    {
        try
        {
            if (!File.Exists(settings.FilePath))
            {
                AnsiConsole.MarkupLine($"[red]Error: PRD file not found: {settings.FilePath}[/]");
                return 1;
            }

            // Load PRD with progress indicator
            PrdParsingResult result = null!;
            
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync($"Loading PRD from {settings.FilePath}...", async ctx =>
                {
                    ctx.Status("Parsing PRD file...");
                    result = await _prdService.LoadPrdAsync(settings.FilePath);
                    
                    ctx.Status("Processing content...");
                    await Task.Delay(200);
                });

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD: {result.ErrorMessage}[/]");
                return 1;
            }

            var document = result.Document!;

            // Display basic information
            var panel = new Panel($"""
                📋 [bold green]PRD loaded successfully![/]
                
                [cyan1]Project:[/] {document.Configuration.ProjectName}
                [cyan1]Version:[/] {document.Configuration.Version}
                [cyan1]Author:[/] {document.Configuration.Author}
                [cyan1]Created:[/] {document.Configuration.CreatedAt:yyyy-MM-dd}
                [cyan1]Updated:[/] {document.Configuration.UpdatedAt:yyyy-MM-dd}
                
                [cyan1]Requirements:[/] {document.Requirements.Count}
                [cyan1]User Stories:[/] {document.UserStories.Count}
                [cyan1]Sections:[/] {document.Sections.Count}
                """)
                .Border(BoxBorder.Rounded)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]📋 PRD Summary[/] ");

            AnsiConsole.Write(panel);

            // Show warnings if any
            if (result.Warnings.Any())
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]⚠️ Warnings:[/]");
                foreach (var warning in result.Warnings)
                {
                    AnsiConsole.MarkupLine($"  • [yellow]{warning}[/]");
                }
            }

            // Show metadata if requested
            if (settings.ShowMetadata)
            {
                DisplayMetadata(document);
            }

            // Validate if requested
            if (settings.Validate)
            {
                var validation = await _prdService.ValidatePrdAsync(document);
                DisplayValidationResults(validation);
            }

            // Export if requested
            if (!string.IsNullOrEmpty(settings.ExportPath))
            {
                await ExportPrdAsync(document, settings.ExportPath, settings.OutputFormat);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private void DisplayMetadata(PrdDocument document)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title("[bold]PRD Metadata[/]")
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Target Audience", document.Configuration.TargetAudience);
        table.AddRow("Stakeholders", string.Join(", ", document.Configuration.Stakeholders));
        
        if (document.Configuration.Metadata.Any())
        {
            foreach (var metadata in document.Configuration.Metadata)
            {
                table.AddRow(metadata.Key, metadata.Value?.ToString() ?? "");
            }
        }

        AnsiConsole.Write(table);
    }

    private void DisplayValidationResults(PrdValidationResult validation)
    {
        AnsiConsole.WriteLine();
        
        var statusColor = validation.IsValid ? "green" : "red";
        var statusText = validation.IsValid ? "✅ Valid" : "❌ Issues Found";
        
        AnsiConsole.MarkupLine($"[bold]Validation Status:[/] [{statusColor}]{statusText}[/]");
        AnsiConsole.MarkupLine($"[bold]Completeness Score:[/] {validation.CompletenessScore:F1}%");
        
        if (validation.Errors.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]Errors:[/]");
            foreach (var error in validation.Errors)
            {
                AnsiConsole.MarkupLine($"  • [red]{error}[/]");
            }
        }

        if (validation.Warnings.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Warnings:[/]");
            foreach (var warning in validation.Warnings)
            {
                AnsiConsole.MarkupLine($"  • [yellow]{warning}[/]");
            }
        }

        if (validation.Suggestions.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Suggestions:[/]");
            foreach (var suggestion in validation.Suggestions)
            {
                AnsiConsole.MarkupLine($"  • [cyan]{suggestion}[/]");
            }
        }
    }

    private async Task ExportPrdAsync(PrdDocument document, string exportPath, string format)
    {
        try
        {
            var directory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var success = await _prdService.SavePrdAsync(document, exportPath);
            
            if (success)
            {
                AnsiConsole.MarkupLine($"[green]✅ PRD exported to: {exportPath}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]❌ Failed to export PRD to: {exportPath}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export error: {ex.Message}[/]");
        }
    }
}