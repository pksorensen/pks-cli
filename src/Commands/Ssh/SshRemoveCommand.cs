using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

public class SshRemoveCommand : Command<SshRemoveCommand.Settings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public SshRemoveCommand(ISshTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [Description("SSH target to remove (host, label, or user@host)")]
        public string? Target { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var target = settings.Target;
        if (string.IsNullOrEmpty(target))
        {
            // Show list and let user pick
            var targets = await _configService.ListTargetsAsync();
            if (targets.Count == 0)
            {
                _console.MarkupLine("[yellow]No SSH targets registered.[/]");
                return 0;
            }

            var choices = targets.Select(t =>
                $"{t.Username}@{t.Host}" + (string.IsNullOrEmpty(t.Label) ? "" : $" ({t.Label})")).ToList();

            var selected = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[yellow]Select target to remove:[/]")
                    .AddChoices(choices));

            var index = choices.IndexOf(selected);
            var selectedTarget = targets[index];

            if (!_console.Confirm($"[red]Remove {selectedTarget.Username}@{selectedTarget.Host}?[/]", defaultValue: false))
            {
                _console.MarkupLine("[dim]Cancelled.[/]");
                return 0;
            }

            await _configService.RemoveTargetAsync(selectedTarget.Id);
            _console.MarkupLine($"[green]Removed SSH target {selectedTarget.Username}@{selectedTarget.Host}[/]");
            return 0;
        }

        // Find by target string
        var found = await _configService.FindTargetAsync(target);
        if (found == null)
        {
            _console.MarkupLine($"[red]SSH target not found: {target.EscapeMarkup()}[/]");
            return 1;
        }

        if (!_console.Confirm($"[red]Remove {found.Username}@{found.Host}?[/]", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _configService.RemoveTargetAsync(found.Id);
        _console.MarkupLine($"[green]Removed SSH target {found.Username}@{found.Host}[/]");
        return 0;
    }
}
