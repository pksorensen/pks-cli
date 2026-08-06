using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

/// <summary>
/// File a consent request up front, instead of discovering the gate by tripping it. Gated commands
/// file their own request when denied, so this is for the case where a caller wants the human's
/// decision in hand before it starts work.
/// </summary>
[Description("File a consent request and print its id for a human to approve")]
public class ConsentRequestCommand : Command<ConsentRequestCommand.Settings>
{
    private readonly IConsentStore _store;
    private readonly IActionCatalog _catalog;
    private readonly IAnsiConsole _console;

    public ConsentRequestCommand(IConsentStore store, IActionCatalog catalog, IAnsiConsole console)
    {
        _store = store;
        _catalog = catalog;
        _console = console;
    }

    public class Settings : ConsentSettings
    {
        [CommandArgument(0, "<action>")]
        [Description("Action id, e.g. storage.delete (see: pks actions)")]
        public string Action { get; set; } = string.Empty;

        [CommandOption("--resource")]
        [Description("Resource the action applies to, e.g. azure-fileshare:account/share")]
        public string Resource { get; set; } = string.Empty;

        [CommandOption("--target")]
        [Description("A specific item the action will touch (repeatable). Approval binds to this exact set.")]
        public string[] Targets { get; set; } = [];

        [CommandOption("--summary")]
        [Description("One-line description shown to the approver")]
        public string? Summary { get; set; }

        [CommandOption("--minutes")]
        [Description("How long the request stays approvable, in minutes (default: 15)")]
        public int Minutes { get; set; } = 15;

        [CommandOption("--json")]
        [Description("Output as JSON (agent-friendly)")]
        public bool Json { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (_catalog.Find(settings.Action) == null)
        {
            _console.MarkupLine($"[red]Unknown action '{Markup.Escape(settings.Action)}'.[/] [dim]See [bold]pks actions[/].[/]");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Resource))
        {
            _console.MarkupLine("[red]--resource is required[/] [dim](a grant is always scoped to something).[/]");
            return 1;
        }

        if (settings.Targets.Length == 0)
        {
            _console.MarkupLine("[red]At least one --target is required[/] [dim](approval binds to the resolved items, never to a pattern).[/]");
            return 1;
        }

        var request = await _store.CreateAsync(
            settings.Action,
            settings.Resource,
            settings.Summary ?? $"{settings.Action} on {settings.Resource} ({settings.Targets.Length} target(s))",
            settings.Targets,
            TimeSpan.FromMinutes(Math.Max(1, settings.Minutes)));

        if (settings.Json)
        {
            _console.WriteLine(JsonSerializer.Serialize(new
            {
                id = request.Id,
                status = request.Status.ToString().ToLowerInvariant(),
                expires = request.ExpiresUtc,
                approveWith = $"pks consent approve {request.Id}",
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        _console.MarkupLine($"[yellow]Consent requested.[/] Request id: [bold]{Markup.Escape(request.Id)}[/]");
        _console.MarkupLine($"[dim]Ask a human to run:[/]  [bold]pks consent approve {Markup.Escape(request.Id)}[/]");
        _console.MarkupLine($"[dim]It expires {request.ExpiresUtc.ToLocalTime():HH:mm}.[/]");
        return 0;
    }
}
