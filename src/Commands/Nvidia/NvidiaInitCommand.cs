using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Nvidia;

[Description("Register an NVIDIA NIM (build.nvidia.com) API key")]
public sealed class NvidiaInitCommand : AsyncCommand<NvidiaInitCommand.Settings>
{
    private readonly INvidiaService _nvidia;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public NvidiaInitCommand(INvidiaService nvidia, IActionGuard guard, IAnsiConsole console)
    {
        _nvidia = nvidia;
        _guard = guard;
        _console = console;
    }

    public sealed class Settings : NvidiaSettings
    {
        [CommandOption("-f|--force")]
        [Description("Replace an existing NVIDIA API key")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!settings.Force && await _nvidia.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[green]NVIDIA API key is already registered.[/]");
            _console.MarkupLine("[dim]Use [bold]--force[/] to replace it.[/]");
            return 0;
        }

        _console.MarkupLine("[bold cyan]NVIDIA NIM API key registration[/]");
        _console.MarkupLine("[dim]Create a key at [link]https://build.nvidia.com[/] (starts with [bold]nvapi-[/]).[/]");
        var apiKey = _console.Prompt(
            new TextPrompt<string>("[cyan]API Key:[/]")
                .Secret()
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("[red]API key is required.[/]")
                    : ValidationResult.Success()))
            .Trim();

        var result = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Validating API key...", _ => _nvidia.ValidateApiKeyAsync(apiKey));

        if (result.Verdict == NvidiaKeyVerdict.Rejected)
        {
            _console.MarkupLine($"[red]NVIDIA rejected the API key[/] [dim](HTTP {result.StatusCode}).[/]");
            return 1;
        }

        if (result.Verdict == NvidiaKeyVerdict.Inconclusive)
        {
            // Not a rejection: the probe model may have been retired or a gateway may be down.
            // Storing an unverified key beats sending someone to rotate a working one.
            _console.MarkupLine("[yellow]Could not verify the key against NVIDIA.[/]");
            if (result.Detail is { Length: > 0 })
                _console.MarkupLine($"[dim]{Markup.Escape(result.Detail)}[/]");
            if (!_console.Confirm("Store it anyway?", defaultValue: false))
                return 1;
        }

        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.CloudAuthWrite,
                "Store NVIDIA API credentials"));
        }
        catch (ActionGuardDeniedException exception)
        {
            _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(exception.Message)}");
            return 1;
        }

        await _nvidia.StoreCredentialsAsync(new NvidiaStoredCredentials
        {
            ApiKey = SecretValue.From(apiKey),
            CreatedAt = DateTime.UtcNow,
        });

        _console.MarkupLine("[green]NVIDIA API key registered.[/]");
        _console.MarkupLine("[dim]Point a local tool at it with [bold]pks nvidia proxy --port 8788 --token …[/].[/]");
        return 0;
    }
}
