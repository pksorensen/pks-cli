using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Rsync;

public class RsyncListCommand : AsyncCommand<RsyncSettings>
{
    private readonly IRsyncTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RsyncListCommand(IRsyncTargetConfigurationService configService, IAnsiConsole console)
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
            _console.MarkupLine("[dim]Run [cyan]pks rsync init[/] to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Label[/]")
            .AddColumn("[cyan]Host[/]")
            .AddColumn("[cyan]User[/]")
            .AddColumn("[cyan]Port[/]")
            .AddColumn("[cyan]Remote path[/]")
            .AddColumn("[cyan]Key[/]")
            .AddColumn("[dim]Registered[/]");

        foreach (var t in targets)
        {
            table.AddRow(
                t.Label ?? "[dim]-[/]",
                t.Host,
                t.Username,
                t.Port.ToString(),
                t.RemotePath.EscapeMarkup(),
                t.KeyPath?.EscapeMarkup() ?? "[dim](agent)[/]",
                t.RegisteredAt.ToString("yyyy-MM-dd HH:mm"));
        }

        _console.Write(table);
        _console.MarkupLine($"[dim]{targets.Count} target(s) registered[/]");

        return 0;
    }
}
