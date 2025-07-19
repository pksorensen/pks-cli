using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;

namespace PKS.Commands.Prd;

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