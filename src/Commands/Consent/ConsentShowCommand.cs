using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Consent;

[Description("Show a consent request, including every target it would touch")]
public class ConsentShowCommand : Command<ConsentShowCommand.Settings>
{
    private readonly IConsentStore _store;
    private readonly IAnsiConsole _console;

    public ConsentShowCommand(IConsentStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : ConsentSettings
    {
        [CommandArgument(0, "<id>")]
        [Description("Consent request id")]
        public string Id { get; set; } = string.Empty;

        [CommandOption("--json")]
        [Description("Output as JSON (agent-friendly)")]
        public bool Json { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var request = await _store.GetAsync(settings.Id);
        if (request == null)
        {
            _console.MarkupLine($"[red]No consent request '{Markup.Escape(settings.Id)}'.[/]");
            return 1;
        }

        if (settings.Json)
        {
            _console.WriteLine(JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }));
            return 0;
        }

        Render(_console, request);
        return 0;
    }

    /// <summary>Shared with approve, so the human decides on exactly what the list shows.</summary>
    internal static void Render(IAnsiConsole console, ConsentRequest request)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[dim]id[/]", $"[bold]{Markup.Escape(request.Id)}[/]");
        grid.AddRow("[dim]status[/]", ConsentListCommand.StatusMarkup(request.Status));
        grid.AddRow("[dim]action[/]", Markup.Escape(request.ActionId));
        grid.AddRow("[dim]resource[/]", Markup.Escape(request.Resource));
        grid.AddRow("[dim]requested by[/]", Markup.Escape(request.RequestedBy ?? "-"));
        grid.AddRow("[dim]created[/]", request.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        grid.AddRow("[dim]expires[/]", request.ExpiresUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (request.GrantExpiresUtc is { } g)
            grid.AddRow("[dim]grant until[/]", $"{g.ToLocalTime():yyyy-MM-dd HH:mm} ({request.RemainingUses} use(s) left)");
        if (!string.IsNullOrEmpty(request.DeniedReason))
            grid.AddRow("[dim]denied[/]", Markup.Escape(request.DeniedReason));

        console.Write(new Panel(grid)
            .Border(BoxBorder.Rounded)
            .Header($" [bold]{Markup.Escape(request.Summary)}[/] "));

        console.WriteLine();
        console.MarkupLine($"[bold]{request.Targets.Count}[/] target{(request.Targets.Count == 1 ? "" : "s")} [dim](fingerprint {Markup.Escape(request.TargetsFingerprint)})[/]");
        foreach (var t in request.Targets)
            console.MarkupLine($"  [red]•[/] {Markup.Escape(t)}");
    }
}
