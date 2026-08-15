using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Nvidia;

[Description("Check whether the registered NVIDIA key still works")]
public sealed class NvidiaStatusCommand : AsyncCommand<NvidiaStatusCommand.Settings>
{
    private readonly INvidiaService _nvidia;
    private readonly IAnsiConsole _console;

    public NvidiaStatusCommand(INvidiaService nvidia, IAnsiConsole console)
    {
        _nvidia = nvidia;
        _console = console;
    }

    public sealed class Settings : NvidiaSettings;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _nvidia.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]No NVIDIA API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks nvidia init[/].[/]");
            return 1;
        }

        var result = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Probing NVIDIA with the stored key...", _ => _nvidia.ValidateStoredKeyAsync());

        switch (result.Verdict)
        {
            case NvidiaKeyVerdict.Valid:
                _console.MarkupLine("[green]NVIDIA key is registered and working.[/]");
                return 0;
            case NvidiaKeyVerdict.Rejected:
                _console.MarkupLine($"[red]NVIDIA rejected the stored key[/] [dim](HTTP {result.StatusCode}).[/]");
                _console.MarkupLine("[dim]Re-register with [bold]pks nvidia init --force[/].[/]");
                return 1;
            default:
                _console.MarkupLine("[yellow]A key is stored, but NVIDIA could not be reached to confirm it.[/]");
                if (result.Detail is { Length: > 0 })
                    _console.MarkupLine($"[dim]{Markup.Escape(result.Detail)}[/]");
                return 1;
        }
    }
}
