using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Moonshot;

[Description("Register a Moonshot API key")]
public sealed class MoonshotInitCommand : AsyncCommand<MoonshotInitCommand.Settings>
{
    private readonly IMoonshotService _moonshot;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public MoonshotInitCommand(IMoonshotService moonshot, IActionGuard guard, IAnsiConsole console)
    {
        _moonshot = moonshot;
        _guard = guard;
        _console = console;
    }

    public sealed class Settings : MoonshotSettings
    {
        [CommandOption("-f|--force")]
        [Description("Replace an existing Moonshot API key")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!settings.Force && await _moonshot.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[green]Moonshot API key is already registered.[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace it.[/]");
            return 0;
        }

        _console.MarkupLine("[bold cyan]Moonshot API key registration[/]");
        _console.MarkupLine("[dim]Create a key at [link]https://platform.moonshot.ai/console/api-keys[/].[/]");
        var apiKey = _console.Prompt(
            new TextPrompt<string>("[cyan]API Key:[/]")
                .Secret()
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("[red]API key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var valid = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Validating API key...", _ => _moonshot.ValidateApiKeyAsync(apiKey));
        if (!valid)
        {
            _console.MarkupLine("[red]Moonshot rejected the API key.[/]");
            return 1;
        }

        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.CloudAuthWrite,
                "Store Moonshot API credentials"));
        }
        catch (ActionGuardDeniedException exception)
        {
            _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        await _moonshot.StoreCredentialsAsync(new MoonshotStoredCredentials
        {
            ApiKey = SecretValue.From(apiKey),
            CreatedAt = DateTime.UtcNow,
        });

        _console.MarkupLine("[green]Moonshot API key registered.[/]");
        _console.MarkupLine("[dim]Try [bold]pks opencode --model kimi-k3[/].[/]");
        return 0;
    }
}
