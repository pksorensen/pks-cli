using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Google;

[Description("Show registered Google AI credentials")]
public class GoogleStatusCommand : Command<GoogleStatusCommand.Settings>
{
    private readonly IGoogleAiService _google;
    private readonly IAnsiConsole _console;

    public GoogleStatusCommand(IGoogleAiService google, IAnsiConsole console)
    {
        _google = google;
        _console = console;
    }

    public class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        if (!await _google.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]No Google AI API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks google init[/] to register one.[/]");
            return 1;
        }

        var key = await _google.GetApiKeyAsync() ?? "";
        var registeredAt = await _google.GetRegisteredAtAsync();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Google AI — Registered Credentials[/]");

        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("API Key", MaskKey(key));

        if (!string.IsNullOrEmpty(registeredAt) &&
            DateTime.TryParse(registeredAt, out var dt))
            table.AddRow("Registered", dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine("[dim]Run [bold]pks image --list-models[/] to see available image generation models.[/]");

        return 0;
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 4) return "****";
        return key[..4] + new string('*', Math.Min(key.Length - 4, 8));
    }
}
