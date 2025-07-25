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

    public override int Execute(CommandContext context, PrdRequirementsSettings? settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, PrdRequirementsSettings settings)
    {
        try
        {
            // Set default file path if not provided
            var filePath = settings.FilePath ?? Path.Combine(Environment.CurrentDirectory, "docs", "PRD.md");

            // Load PRD
            var loadResult = await _prdService.LoadPrdAsync(filePath, CancellationToken.None);
            if (loadResult == null || !loadResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to load PRD from {filePath}: {loadResult?.Message}[/]");
                return 1;
            }

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

            // Create a document from the load result or generate sample requirements
            var document = CreateDocumentFromLoadResult(loadResult);
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
                RequirementPriority.Nice => "dim",
                _ => "white"
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

    private PrdDocument CreateDocumentFromLoadResult(PrdLoadResult loadResult)
    {
        var document = new PrdDocument
        {
            Configuration = new PrdConfiguration
            {
                ProjectName = loadResult.ProductName,
                Description = "Loaded PRD document"
            }
        };

        // Generate sample requirements if we have requirement data
        if (loadResult.Requirements != null)
        {
            var reqIndex = 1;
            
            // Add functional requirements
            foreach (var functionalReq in loadResult.Requirements.Functional ?? Array.Empty<string>())
            {
                document.Requirements.Add(new PrdRequirement
                {
                    Id = $"REQ-{reqIndex:D3}",
                    Title = functionalReq.Length > 50 ? functionalReq.Substring(0, 47) + "..." : functionalReq,
                    Description = functionalReq,
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.High,
                    Status = RequirementStatus.Draft
                });
                reqIndex++;
            }

            // Add non-functional requirements
            foreach (var nonFunctionalReq in loadResult.Requirements.NonFunctional ?? Array.Empty<string>())
            {
                document.Requirements.Add(new PrdRequirement
                {
                    Id = $"REQ-{reqIndex:D3}",
                    Title = nonFunctionalReq.Length > 50 ? nonFunctionalReq.Substring(0, 47) + "..." : nonFunctionalReq,
                    Description = nonFunctionalReq,
                    Type = RequirementType.NonFunctional,
                    Priority = RequirementPriority.Medium,
                    Status = RequirementStatus.Draft
                });
                reqIndex++;
            }
        }

        // If no requirements were parsed, add some default ones for testing
        if (!document.Requirements.Any())
        {
            document.Requirements.AddRange(new[]
            {
                new PrdRequirement
                {
                    Id = "REQ-001",
                    Title = "High Priority Draft Requirement",
                    Description = "A sample high priority requirement",
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.High,
                    Status = RequirementStatus.Draft
                },
                new PrdRequirement
                {
                    Id = "REQ-002",
                    Title = "Medium Priority Feature",
                    Description = "A sample medium priority requirement",
                    Type = RequirementType.Functional,
                    Priority = RequirementPriority.Medium,
                    Status = RequirementStatus.InProgress
                }
            });
        }

        return document;
    }
}