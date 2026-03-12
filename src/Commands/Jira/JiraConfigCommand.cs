using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Jira;

/// <summary>
/// View or set Jira field mappings (e.g. acceptance criteria custom field ID).
/// </summary>
[Description("Configure Jira field mappings")]
public class JiraConfigCommand : Command<JiraConfigCommand.Settings>
{
    private readonly IJiraService _jiraService;
    private readonly IAnsiConsole _console;

    public JiraConfigCommand(IJiraService jiraService, IAnsiConsole console)
    {
        _jiraService = jiraService;
        _console = console;
    }

    public class Settings : JiraSettings
    {
        [CommandOption("--ac-field")]
        [Description("Set the acceptance criteria custom field ID (e.g. customfield_10064)")]
        public string? AcField { get; set; }

        [CommandOption("--show")]
        [Description("Show current field mappings")]
        public bool Show { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (_jiraService is not JiraService svc)
        {
            _console.MarkupLine("[red]Configuration not available.[/]");
            return 1;
        }

        if (!string.IsNullOrEmpty(settings.AcField))
        {
            await svc.SetAcceptanceCriteriaFieldAsync(settings.AcField);
            _console.MarkupLine($"[green]Acceptance criteria field set to:[/] {Markup.Escape(settings.AcField)}");
            return 0;
        }

        // Default: show current config
        var acField = await svc.GetAcceptanceCriteriaFieldAsync();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Setting[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Acceptance Criteria Field", acField ?? "[dim]not set (auto-discover)[/]");

        _console.Write(table);

        if (acField == null)
        {
            _console.MarkupLine("");
            _console.MarkupLine("[dim]Tip: If AC is not exported, find the field ID with --debug and set it:[/]");
            _console.MarkupLine("[cyan]  pks jira config --ac-field customfield_10064[/]");
        }

        return 0;
    }
}
