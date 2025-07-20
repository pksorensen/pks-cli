using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using System.Text.Json;

namespace PKS.Commands.Prd;

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

    public override int Execute(CommandContext context, PrdStatusSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
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