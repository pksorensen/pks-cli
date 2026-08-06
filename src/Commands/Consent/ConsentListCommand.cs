using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

[Description("List consent requests awaiting a decision")]
public class ConsentListCommand : Command<ConsentListCommand.Settings>
{
    private readonly IConsentStore _store;
    private readonly IAnsiConsole _console;

    public ConsentListCommand(IConsentStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : ConsentSettings
    {
        [CommandOption("--all")]
        [Description("Include resolved requests (approved-and-spent, denied, expired)")]
        public bool All { get; set; }

        [CommandOption("--json")]
        [Description("Output as JSON (agent-friendly)")]
        public bool Json { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var requests = await _store.ListAsync(settings.All);

        if (settings.Json)
        {
            _console.WriteLine(JsonSerializer.Serialize(requests.Select(r => new
            {
                id = r.Id,
                action = r.ActionId,
                resource = r.Resource,
                status = r.Status.ToString().ToLowerInvariant(),
                targets = r.Targets.Count,
                created = r.CreatedUtc,
                expires = r.Status == ConsentStatus.Approved ? r.GrantExpiresUtc : r.ExpiresUtc,
                remainingUses = r.RemainingUses,
            }), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        if (requests.Count == 0)
        {
            _console.MarkupLine(settings.All
                ? "[dim]No consent requests.[/]"
                : "[dim]No pending or approved consent requests.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn("Status");
        table.AddColumn("Action");
        table.AddColumn("Resource");
        table.AddColumn(new TableColumn("Targets").RightAligned());
        table.AddColumn("Expires");

        foreach (var r in requests)
        {
            var deadline = r.Status == ConsentStatus.Approved ? r.GrantExpiresUtc : r.ExpiresUtc;
            table.AddRow(
                $"[bold]{Markup.Escape(r.Id)}[/]",
                StatusMarkup(r.Status),
                Markup.Escape(r.ActionId),
                Markup.Escape(r.Resource),
                r.Targets.Count.ToString(),
                deadline?.ToLocalTime().ToString("HH:mm") ?? "-");
        }

        _console.Write(table);
        _console.MarkupLine("[dim]Approve one with [bold]pks consent approve <id>[/] (interactive terminal, second factor).[/]");
        return 0;
    }

    internal static string StatusMarkup(ConsentStatus status) => status switch
    {
        ConsentStatus.Pending => "[yellow]pending[/]",
        ConsentStatus.Approved => "[green]approved[/]",
        ConsentStatus.Consumed => "[dim]consumed[/]",
        ConsentStatus.Denied => "[red]denied[/]",
        _ => "[dim]expired[/]",
    };
}
