using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using System.Text.Json;

namespace PKS.Commands.Prd;

/// <summary>
/// Validate PRD completeness and consistency
/// </summary>
[Description("Validate PRD for completeness, consistency, and quality")]
public class PrdValidateCommand : Command<PrdValidateSettings>
{
    private readonly IPrdService _prdService;

    public PrdValidateCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdValidateSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdValidateSettings settings)
    {
        try
        {
            // Set default file path if not provided
            var filePath = settings.FilePath ?? Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");

            // Load PRD
            var loadResult = await _prdService.LoadPrdAsync(filePath);
            if (loadResult == null || !loadResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD from {filePath}: {loadResult?.Message}[/]");
                return 1;
            }

            // Validate PRD
            PrdValidationResult validation = null!;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Star2)
                .SpinnerStyle(Style.Parse("green bold"))
                .StartAsync("Validating PRD...", async ctx =>
                {
                    ctx.Status("Analyzing completeness...");
                    await Task.Delay(300);

                    ctx.Status("Checking consistency...");
                    var validationOptions = new PrdValidationOptions
                    {
                        FilePath = filePath,
                        Strictness = settings.Strict ? "strict" : "standard"
                    };
                    validation = await _prdService.ValidatePrdAsync(validationOptions);

                    ctx.Status("Generating report...");
                    await Task.Delay(200);
                });

            // Display validation results
            DisplayValidationResults(validation, settings.Strict);

            // Generate report if requested
            if (!string.IsNullOrEmpty(settings.ReportPath))
            {
                await GenerateValidationReportAsync(validation, settings.ReportPath);
            }

            // Return appropriate exit code
            return validation.IsValid ? 0 : 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private void DisplayValidationResults(PrdValidationResult validation, bool strictMode)
    {
        var statusIcon = validation.IsValid ? "✅" : "❌";
        var statusColor = validation.IsValid ? "green" : "red";
        var statusText = validation.IsValid ? "Valid" : "Issues Found";

        var panel = new Panel($"""
            {statusIcon} [bold {statusColor}]{statusText}[/]
            
            [cyan1]Completeness Score:[/] {validation.CompletenessScore:F1}%
            [cyan1]Errors:[/] {validation.Errors?.Count() ?? 0}
            [cyan1]Warnings:[/] {validation.Warnings?.Count() ?? 0}
            [cyan1]Suggestions:[/] {validation.Suggestions?.Count() ?? 0}
            """)
            .Border(BoxBorder.Double)
            .BorderStyle(statusColor)
            .Header(" [bold cyan]🔍 Validation Results[/] ");

        AnsiConsole.Write(panel);

        // Show detailed issues
        if (validation.Errors.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red bold]Errors (must fix):[/]");
            foreach (var error in validation.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]❌ {error}[/]");
            }
        }

        if (validation.Warnings.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow bold]Warnings (should fix):[/]");
            foreach (var warning in validation.Warnings)
            {
                AnsiConsole.MarkupLine($"  [yellow]⚠️ {warning}[/]");
            }
        }

        if (validation.Suggestions.Any())
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan bold]Suggestions (could improve):[/]");
            foreach (var suggestion in validation.Suggestions)
            {
                AnsiConsole.MarkupLine($"  [cyan]💡 {suggestion}[/]");
            }
        }

        // Show completion progress
        AnsiConsole.WriteLine();
        var progressValue = (int)validation.CompletenessScore;

        AnsiConsole.Write(new Rule($"[green]Completeness: {validation.CompletenessScore:F1}%[/]")
            .RuleStyle("green"));

        // Alternative progress visualization using markup
        var progressBar = new string('█', progressValue / 5);
        var remainingBar = new string('░', 20 - (progressValue / 5));
        AnsiConsole.MarkupLine($"[green]{progressBar}[/][dim]{remainingBar}[/] {validation.CompletenessScore:F1}%");
    }

    private async Task GenerateValidationReportAsync(PrdValidationResult validation, string reportPath)
    {
        try
        {
            var report = new
            {
                Timestamp = DateTime.UtcNow,
                IsValid = validation.IsValid,
                CompletenessScore = validation.CompletenessScore,
                Errors = validation.Errors,
                Warnings = validation.Warnings,
                Suggestions = validation.Suggestions,
                Summary = new
                {
                    TotalIssues = validation.Errors.Count() + validation.Warnings.Count(),
                    CriticalIssues = validation.Errors.Count(),
                    RecommendedActions = validation.Errors.Any() ?
                        "Fix all errors before proceeding" :
                        validation.Warnings.Any() ?
                            "Address warnings for better quality" :
                            "PRD is in good shape"
                }
            };

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await File.WriteAllTextAsync(reportPath, json);
            AnsiConsole.MarkupLine($"[green]✅ Validation report saved to: {reportPath}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to generate report: {ex.Message}[/]");
        }
    }
}