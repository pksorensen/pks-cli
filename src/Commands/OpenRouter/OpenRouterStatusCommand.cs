using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.OpenRouter;

[Description("Show the registered OpenRouter key's label, credit and tier")]
public sealed class OpenRouterStatusCommand : AsyncCommand<OpenRouterStatusCommand.Settings>
{
    private readonly IOpenRouterService _openRouter;
    private readonly IAnsiConsole _console;

    public OpenRouterStatusCommand(IOpenRouterService openRouter, IAnsiConsole console)
    {
        _openRouter = openRouter;
        _console = console;
    }

    public sealed class Settings : OpenRouterSettings;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (!await _openRouter.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]No OpenRouter API key registered.[/]");
            _console.MarkupLine("[dim]Run [bold]pks openrouter init[/].[/]");
            return 1;
        }

        var info = await _console.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Asking OpenRouter about the key...", _ => _openRouter.GetStoredKeyInfoAsync());
        if (info == null)
        {
            // A key is stored but OpenRouter will not own it — revoked, rotated, or the network is out.
            _console.MarkupLine("[red]The stored key was rejected by OpenRouter.[/]");
            _console.MarkupLine("[dim]Re-register with [bold]pks openrouter init --force[/].[/]");
            return 1;
        }

        OpenRouterInitCommand.WriteKeyInfo(_console, info);
        return 0;
    }
}
