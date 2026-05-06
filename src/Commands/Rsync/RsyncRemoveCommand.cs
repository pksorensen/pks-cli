using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Rsync;

public class RsyncRemoveCommand : AsyncCommand<RsyncSettings>
{
    private readonly IRsyncTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RsyncRemoveCommand(IRsyncTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, RsyncSettings settings)
    {
        var targets = await _configService.ListTargetsAsync();

        if (targets.Count == 0)
        {
            _console.MarkupLine("[yellow]No rsync targets registered.[/]");
            return 0;
        }

        var choices = targets
            .Select(t => $"{t.Username}@{t.Host}" + (t.Label != null ? $" ({t.Label})" : ""))
            .ToList();

        var selected = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[yellow]Select target to remove:[/]")
                .AddChoices(choices));

        var index = choices.IndexOf(selected);
        var target = targets[index];

        if (!_console.Confirm($"[red]Remove {target.Username}@{target.Host}?[/]", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _configService.RemoveTargetAsync(target.Id);
        _console.MarkupLine($"[green]Removed rsync target {target.Username}@{target.Host}.[/]");
        return 0;
    }
}
