using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;

namespace PKS.Commands.Prd;

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

    public override int Execute(CommandContext context, PrdLoadSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdLoadSettings settings)
    {
        try
        {
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