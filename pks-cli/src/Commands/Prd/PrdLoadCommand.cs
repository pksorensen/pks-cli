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
            PrdLoadResult loadResult = null!;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync($"Loading PRD from {settings.FilePath}...", async ctx =>
                {
                    ctx.Status("Parsing PRD file...");
                    loadResult = await _prdService.LoadPrdAsync(settings.FilePath);

                    ctx.Status("Processing content...");
                    await Task.Delay(200);
                });

            if (loadResult == null || !loadResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD from {settings.FilePath}: {loadResult?.Message}[/]");
                return 1;
            }

            // Display basic information
            var panel = new Panel($"""
                📋 [bold green]PRD loaded successfully![/]
                
                [cyan1]Project:[/] {loadResult.ProductName}
                [cyan1]Template:[/] {loadResult.Template ?? "Unknown"}
                [cyan1]Status:[/] {(loadResult.Success ? "Loaded" : "Failed")}
                
                [cyan1]Sections:[/] {loadResult.Sections?.Count ?? 0}
                [cyan1]Message:[/] {loadResult.Message ?? "No message"}
                """)
                .Border(BoxBorder.Rounded)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]📋 PRD Summary[/] ");

            AnsiConsole.Write(panel);

            // Show any content insights
            if (loadResult.Sections?.Any() == true)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[green]✓ Found {loadResult.Sections.Count} sections in the PRD[/]");
            }

            // Show metadata if requested
            if (settings.ShowMetadata)
            {
                DisplayMetadata(loadResult);
            }

            // Validate if requested
            if (settings.Validate)
            {
                var validationOptions = new PrdValidationOptions
                {
                    FilePath = settings.FilePath,
                    Strictness = "standard"
                };
                var validation = await _prdService.ValidatePrdAsync(validationOptions);
                DisplayValidationResults(validation);
            }

            // Export if requested
            if (!string.IsNullOrEmpty(settings.ExportPath))
            {
                await ExportPrdAsync(loadResult, settings.ExportPath, settings.OutputFormat);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private void DisplayMetadata(PrdLoadResult loadResult)
    {
        AnsiConsole.WriteLine();
        var table = new Table()
            .Title("[bold]PRD Metadata[/]")
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Product Name", loadResult.ProductName ?? "Unknown");
        table.AddRow("Template", loadResult.Template ?? "Unknown");
        table.AddRow("Sections", loadResult.Sections?.Count.ToString() ?? "0");
        table.AddRow("Status", loadResult.Success ? "Loaded" : "Failed");

        // Note: Metadata not available in PrdLoadResult
        table.AddRow("Message", loadResult.Message ?? "No message");

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

    private async Task ExportPrdAsync(PrdLoadResult loadResult, string exportPath, string format)
    {
        try
        {
            var directory = Path.GetDirectoryName(exportPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // For export, we would need the original document, but since we only have LoadResult,
            // we'll create a simple export of the available data
            var exportData = new
            {
                ProductName = loadResult.ProductName,
                Template = loadResult.Template,
                Sections = loadResult.Sections,
                Message = loadResult.Message,
                Success = loadResult.Success,
                ExportedAt = DateTime.UtcNow
            };

            var json = System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(exportPath, json);

            AnsiConsole.MarkupLine($"[green]✅ PRD data exported to: {exportPath}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export error: {ex.Message}[/]");
        }
    }
}