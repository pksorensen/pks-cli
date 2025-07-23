using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;

namespace PKS.Commands.Prd;

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

    public override int Execute(CommandContext context, PrdGenerateSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
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
            PrdGenerationResult generateResult = null!;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync("Generating PRD...", async ctx =>
                {
                    ctx.Status("Analyzing idea and requirements...");
                    await Task.Delay(500);

                    ctx.Status("Generating sections and requirements...");
                    var generateResult = await _prdService.GeneratePrdAsync(request);
                    // Note: generateResult contains the generated PRD information

                    ctx.Status("Formatting and saving PRD...");
                    await Task.Delay(300);
                });

            // Display success message
            var panel = new Panel($"""
                🎉 [bold green]PRD generated successfully![/]
                
                [cyan1]Project:[/] {settings.ProjectName}
                [cyan1]Output:[/] {settings.OutputPath}
                [cyan1]Output File:[/] {generateResult.OutputFile ?? "N/A"}
                [cyan1]Word Count:[/] {generateResult.WordCount}
                [cyan1]Sections:[/] {generateResult.Sections?.Count ?? 0}
                
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
                var validationOptions = new PrdValidationOptions
                {
                    FilePath = settings.OutputPath,
                    Strictness = "standard"
                };
                var validation = await _prdService.ValidatePrdAsync(validationOptions);
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
        table.AddRow("Errors", validation.Errors?.Count().ToString() ?? "0");
        table.AddRow("Warnings", validation.Warnings?.Count().ToString() ?? "0");
        table.AddRow("Suggestions", validation.Suggestions?.Count().ToString() ?? "0");

        AnsiConsole.Write(table);
    }
}