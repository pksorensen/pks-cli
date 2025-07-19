using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using System.Text.Json;

namespace PKS.Commands.Prd;

/// <summary>
/// List and manage requirements from PRD
/// </summary>
[Description("List and filter requirements from a PRD document")]
public class PrdRequirementsCommand : Command<PrdRequirementsSettings>
{
    private readonly IPrdService _prdService;

    public PrdRequirementsCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdRequirementsSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdRequirementsSettings settings)
    {
        try
        {
            // Set default file path if not provided
            var filePath = settings.FilePath ?? Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");
            
            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[red]PRD file not found: {filePath}[/]");
                AnsiConsole.MarkupLine("Use [cyan]pks prd generate[/] to create a new PRD");
                return 1;
            }

            // Load PRD
            var result = await _prdService.LoadPrdAsync(filePath);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD: {result.ErrorMessage}[/]");
                return 1;
            }

            var document = result.Document!;

            // Parse filters
            RequirementStatus? statusFilter = null;
            if (!string.IsNullOrEmpty(settings.Status))
            {
                if (!Enum.TryParse<RequirementStatus>(settings.Status, true, out var status))
                {
                    AnsiConsole.MarkupLine($"[red]Invalid status: {settings.Status}[/]");
                    return 1;
                }
                statusFilter = status;
            }

            RequirementPriority? priorityFilter = null;
            if (!string.IsNullOrEmpty(settings.Priority))
            {
                if (!Enum.TryParse<RequirementPriority>(settings.Priority, true, out var priority))
                {
                    AnsiConsole.MarkupLine($"[red]Invalid priority: {settings.Priority}[/]");
                    return 1;
                }
                priorityFilter = priority;
            }

            // Get filtered requirements
            var requirements = await _prdService.GetRequirementsAsync(document, statusFilter, priorityFilter);

            // Apply additional filters
            if (!string.IsNullOrEmpty(settings.Type))
            {
                if (Enum.TryParse<RequirementType>(settings.Type, true, out var typeFilter))
                {
                    requirements = requirements.Where(r => r.Type == typeFilter).ToList();
                }
            }

            if (!string.IsNullOrEmpty(settings.Assignee))
            {
                requirements = requirements.Where(r => 
                    r.Assignee.Contains(settings.Assignee, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Display requirements
            if (!requirements.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No requirements found matching the specified criteria[/]");
                return 0;
            }

            if (settings.ShowDetails)
            {
                DisplayDetailedRequirements(requirements);
            }
            else
            {
                DisplayRequirementsTable(requirements);
            }

            // Export if requested
            if (!string.IsNullOrEmpty(settings.ExportPath))
            {
                await ExportRequirementsAsync(requirements, settings.ExportPath);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private void DisplayRequirementsTable(List<PrdRequirement> requirements)
    {
        var table = new Table()
            .Title($"[bold]Requirements ({requirements.Count})[/]")
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("ID");
        table.AddColumn("Title");
        table.AddColumn("Type");
        table.AddColumn("Priority");
        table.AddColumn("Status");
        table.AddColumn("Assignee");

        foreach (var req in requirements.OrderBy(r => r.Id))
        {
            var statusColor = req.Status switch
            {
                RequirementStatus.Completed => "green",
                RequirementStatus.InProgress => "yellow",
                RequirementStatus.Blocked => "red",
                RequirementStatus.Cancelled => "dim",
                _ => "white"
            };

            var priorityColor = req.Priority switch
            {
                RequirementPriority.Critical => "red",
                RequirementPriority.High => "orange1",
                RequirementPriority.Medium => "yellow",
                RequirementPriority.Low => "cyan",
                RequirementPriority.Nice => "dim"
            };

            table.AddRow(
                req.Id,
                req.Title.Length > 40 ? req.Title.Substring(0, 37) + "..." : req.Title,
                req.Type.ToString(),
                $"[{priorityColor}]{req.Priority}[/]",
                $"[{statusColor}]{req.Status}[/]",
                req.Assignee
            );
        }

        AnsiConsole.Write(table);
    }

    private void DisplayDetailedRequirements(List<PrdRequirement> requirements)
    {
        foreach (var req in requirements.OrderBy(r => r.Id))
        {
            var panel = new Panel($"""
                [bold cyan]{req.Title}[/]
                
                {req.Description}
                
                [dim]Type:[/] {req.Type}
                [dim]Priority:[/] {req.Priority}
                [dim]Status:[/] {req.Status}
                [dim]Assignee:[/] {req.Assignee}
                [dim]Effort:[/] {req.EstimatedEffort} points
                
                [dim]Acceptance Criteria:[/]
                {string.Join("\n", req.AcceptanceCriteria.Select(c => $"• {c}"))}
                """)
                .Border(BoxBorder.Rounded)
                .BorderStyle("cyan")
                .Header($" [bold]{req.Id}[/] ");

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }
    }

    private async Task ExportRequirementsAsync(List<PrdRequirement> requirements, string exportPath)
    {
        try
        {
            var extension = Path.GetExtension(exportPath).ToLowerInvariant();
            
            if (extension == ".json")
            {
                var json = JsonSerializer.Serialize(requirements, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(exportPath, json);
            }
            else if (extension == ".csv")
            {
                var csv = GenerateCsv(requirements);
                await File.WriteAllTextAsync(exportPath, csv);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Unsupported export format: {extension}[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[green]✅ Requirements exported to: {exportPath}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export error: {ex.Message}[/]");
        }
    }

    private string GenerateCsv(List<PrdRequirement> requirements)
    {
        var lines = new List<string>
        {
            "ID,Title,Description,Type,Priority,Status,Assignee,EstimatedEffort,AcceptanceCriteria"
        };

        foreach (var req in requirements)
        {
            var acceptanceCriteria = string.Join("; ", req.AcceptanceCriteria);
            lines.Add($"\"{req.Id}\",\"{req.Title}\",\"{req.Description}\",\"{req.Type}\",\"{req.Priority}\",\"{req.Status}\",\"{req.Assignee}\",{req.EstimatedEffort},\"{acceptanceCriteria}\"");
        }

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Show PRD status and progress
/// </summary>
[Description("Display PRD status, progress, and statistics")]
public class PrdStatusCommand : Command<PrdStatusSettings>
{
    private readonly IPrdService _prdService;

    public PrdStatusCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdStatusSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdStatusSettings settings)
    {
        try
        {
            if (settings.CheckAll)
            {
                await CheckAllPrdsAsync();
                return 0;
            }

            // Set default file path if not provided
            var filePath = settings.FilePath ?? Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");
            
            if (settings.Watch)
            {
                await WatchPrdAsync(filePath);
                return 0;
            }

            await DisplayPrdStatusAsync(filePath, settings);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private async Task DisplayPrdStatusAsync(string filePath, PrdStatusSettings settings)
    {
        var status = await _prdService.GetPrdStatusAsync(filePath);
        
        if (!status.Exists)
        {
            AnsiConsole.MarkupLine($"[red]PRD file not found: {filePath}[/]");
            AnsiConsole.MarkupLine("Use [cyan]pks prd generate[/] to create a new PRD");
            return;
        }

        // Display status panel
        var completionColor = status.CompletionPercentage switch
        {
            >= 80 => "green",
            >= 60 => "yellow",
            >= 40 => "orange1",
            _ => "red"
        };

        var panel = new Panel($"""
            📊 [bold]PRD Status Report[/]
            
            [cyan1]File:[/] {filePath}
            [cyan1]Last Modified:[/] {status.LastModified:yyyy-MM-dd HH:mm:ss}
            
            [cyan1]Total Requirements:[/] {status.TotalRequirements}
            [cyan1]Completed:[/] [green]{status.CompletedRequirements}[/]
            [cyan1]In Progress:[/] [yellow]{status.InProgressRequirements}[/]
            [cyan1]Pending:[/] [dim]{status.PendingRequirements}[/]
            
            [cyan1]User Stories:[/] {status.TotalUserStories}
            
            [cyan1]Completion:[/] [{completionColor}]{status.CompletionPercentage:F1}%[/]
            """)
            .Border(BoxBorder.Double)
            .BorderStyle("cyan1")
            .Header(" [bold cyan]📋 PRD Status[/] ");

        AnsiConsole.Write(panel);

        // Show progress bar
        var progressBar = new BreakdownChart()
            .Width(60)
            .ShowPercentage()
            .AddItem("Completed", status.CompletedRequirements, Color.Green)
            .AddItem("In Progress", status.InProgressRequirements, Color.Yellow)
            .AddItem("Pending", status.PendingRequirements, Color.Grey);

        AnsiConsole.Write(progressBar);

        // Show recent changes if available
        if (status.RecentChanges.Any() && settings.IncludeHistory)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Recent Changes:[/]");
            foreach (var change in status.RecentChanges.Take(5))
            {
                AnsiConsole.MarkupLine($"  • {change}");
            }
        }

        // Export status if requested
        if (!string.IsNullOrEmpty(settings.ExportPath))
        {
            await ExportStatusAsync(status, settings.ExportPath);
        }
    }

    private async Task CheckAllPrdsAsync()
    {
        var prdFiles = await _prdService.FindPrdFilesAsync(Environment.CurrentDirectory);
        
        if (!prdFiles.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No PRD files found in the current directory[/]");
            return;
        }

        var table = new Table()
            .Title("[bold]All PRD Files[/]")
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("File");
        table.AddColumn("Requirements");
        table.AddColumn("Completion");
        table.AddColumn("Last Modified");

        foreach (var file in prdFiles)
        {
            var status = await _prdService.GetPrdStatusAsync(file);
            var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, file);
            
            var completionColor = status.CompletionPercentage switch
            {
                >= 80 => "green",
                >= 60 => "yellow",
                >= 40 => "orange1",
                _ => "red"
            };

            table.AddRow(
                relativePath,
                status.TotalRequirements.ToString(),
                $"[{completionColor}]{status.CompletionPercentage:F1}%[/]",
                status.LastModified.ToString("MM/dd HH:mm")
            );
        }

        AnsiConsole.Write(table);
    }

    private async Task WatchPrdAsync(string filePath)
    {
        AnsiConsole.MarkupLine($"[cyan]👀 Watching PRD file: {filePath}[/]");
        AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop watching[/]");
        AnsiConsole.WriteLine();

        var lastModified = DateTime.MinValue;
        var cancellationToken = new CancellationTokenSource();
        
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cancellationToken.Cancel();
        };

        try
        {
            while (!cancellationToken.Token.IsCancellationRequested)
            {
                if (File.Exists(filePath))
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.LastWriteTime > lastModified)
                    {
                        lastModified = fileInfo.LastWriteTime;
                        AnsiConsole.Clear();
                        AnsiConsole.MarkupLine($"[green]🔄 Updated: {lastModified:HH:mm:ss}[/]");
                        await DisplayPrdStatusAsync(filePath, new PrdStatusSettings());
                    }
                }

                await Task.Delay(2000, cancellationToken.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]👋 Stopped watching PRD file[/]");
        }
    }

    private async Task ExportStatusAsync(PrdStatus status, string exportPath)
    {
        try
        {
            var statusJson = JsonSerializer.Serialize(status, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            await File.WriteAllTextAsync(exportPath, statusJson);
            AnsiConsole.MarkupLine($"[green]✅ Status exported to: {exportPath}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Export error: {ex.Message}[/]");
        }
    }
}

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

    public override int Execute(CommandContext context, PrdValidateSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdValidateSettings settings)
    {
        try
        {
            // Set default file path if not provided
            var filePath = settings.FilePath ?? Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");
            
            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[red]PRD file not found: {filePath}[/]");
                return 1;
            }

            // Load PRD
            var result = await _prdService.LoadPrdAsync(filePath);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD: {result.ErrorMessage}[/]");
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
                    validation = await _prdService.ValidatePrdAsync(result.Document!);
                    
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
            [cyan1]Errors:[/] {validation.Errors.Count}
            [cyan1]Warnings:[/] {validation.Warnings.Count}
            [cyan1]Suggestions:[/] {validation.Suggestions.Count}
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
                    TotalIssues = validation.Errors.Count + validation.Warnings.Count,
                    CriticalIssues = validation.Errors.Count,
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

/// <summary>
/// Generate PRD templates
/// </summary>
[Description("Generate PRD templates for different project types")]
public class PrdTemplateCommand : Command<PrdTemplateSettings>
{
    private readonly IPrdService _prdService;

    public PrdTemplateCommand(IPrdService prdService)
    {
        _prdService = prdService;
    }

    public override int Execute(CommandContext context, PrdTemplateSettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdTemplateSettings settings)
    {
        try
        {
            if (settings.ListTemplates)
            {
                DisplayAvailableTemplates();
                return 0;
            }

            if (string.IsNullOrEmpty(settings.ProjectName))
            {
                AnsiConsole.MarkupLine("[red]Project name is required[/]");
                return 1;
            }

            // Parse template type
            if (!Enum.TryParse<PrdTemplateType>(settings.TemplateType, true, out var templateType))
            {
                AnsiConsole.MarkupLine($"[red]Invalid template type: {settings.TemplateType}[/]");
                DisplayAvailableTemplates();
                return 1;
            }

            // Generate template
            var outputPath = await _prdService.GenerateTemplateAsync(
                settings.ProjectName, 
                templateType, 
                settings.OutputPath);

            var panel = new Panel($"""
                📝 [bold green]PRD template generated![/]
                
                [cyan1]Project:[/] {settings.ProjectName}
                [cyan1]Template Type:[/] {templateType}
                [cyan1]Output:[/] {outputPath}
                
                Next steps:
                • Edit the template with your project details
                • Use [cyan]pks prd validate[/] to check completeness
                • Use [cyan]pks prd status[/] to track progress
                """)
                .Border(BoxBorder.Double)
                .BorderStyle("cyan1")
                .Header(" [bold cyan]📋 Template Ready[/] ");

            AnsiConsole.Write(panel);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }

    private void DisplayAvailableTemplates()
    {
        AnsiConsole.MarkupLine("[bold cyan]Available PRD Templates:[/]");
        AnsiConsole.WriteLine();

        var templates = new[]
        {
            ("standard", "Standard business PRD template"),
            ("technical", "Technical/API focused PRD template"),
            ("mobile", "Mobile application PRD template"),
            ("web", "Web application PRD template"),
            ("api", "API service PRD template"),
            ("minimal", "Lightweight PRD for small projects"),
            ("enterprise", "Comprehensive enterprise PRD template")
        };

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle("cyan");

        table.AddColumn("Template");
        table.AddColumn("Description");

        foreach (var (name, description) in templates)
        {
            table.AddRow($"[green]{name}[/]", description);
        }

        AnsiConsole.Write(table);
        
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Usage: [yellow]pks prd template <project-name> --type <template-type>[/]");
    }
}