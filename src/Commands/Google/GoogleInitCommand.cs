using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Google;

[Description("Register a Google AI Studio API key")]
public class GoogleInitCommand : Command<GoogleInitCommand.Settings>
{
    private readonly IGoogleAiService _google;
    private readonly IAnsiConsole _console;

    public GoogleInitCommand(IGoogleAiService google, IAnsiConsole console)
    {
        _google = google;
        _console = console;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--force")]
        [Description("Re-register even if a key is already stored")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _google.IsAuthenticatedAsync())
        {
            var existing = await _google.GetApiKeyAsync();
            var registeredAt = await _google.GetRegisteredAtAsync();
            var masked = MaskKey(existing ?? "");

            _console.MarkupLine("[green]Google AI API key already registered.[/]");
            _console.MarkupLine($"  Key:         [dim]{masked}[/]");
            if (!string.IsNullOrEmpty(registeredAt) &&
                DateTime.TryParse(registeredAt, out var dt))
                _console.MarkupLine($"  Registered:  [dim]{dt.ToLocalTime():yyyy-MM-dd HH:mm}[/]");
            _console.WriteLine();
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace the stored key.[/]");
            return 0;
        }

        _console.MarkupLine("[bold cyan]Google AI Studio — API Key Registration[/]");
        _console.WriteLine();
        _console.MarkupLine("[dim]Get your API key at: https://aistudio.google.com/apikey[/]");
        _console.WriteLine();

        var apiKey = _console.Prompt(
            new TextPrompt<string>("[cyan]API Key:[/]")
                .Secret()
                .Validate(k => !string.IsNullOrWhiteSpace(k)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]API key cannot be empty.[/]")));

        var valid = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Validating API key...", async _ =>
                await _google.ValidateApiKeyAsync(apiKey));

        if (!valid)
        {
            _console.MarkupLine("[red]API key validation failed.[/]");
            _console.MarkupLine("[dim]Check that the key is correct and the Generative Language API is enabled.[/]");
            return 1;
        }

        await _google.StoreApiKeyAsync(apiKey);

        _console.WriteLine();
        _console.MarkupLine($"[green]API key registered successfully.[/] [dim]({MaskKey(apiKey)})[/]");
        _console.WriteLine();
        _console.Write(new Panel("[dim]Try [bold]pks image --list-models[/] to see available image generation models.[/]")
            .Border(BoxBorder.Rounded));

        return 0;
    }

    private static string MaskKey(string key)
    {
        if (key.Length <= 4) return "****";
        return key[..4] + new string('*', Math.Min(key.Length - 4, 8));
    }
}
