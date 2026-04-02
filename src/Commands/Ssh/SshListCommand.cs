using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

public class SshListCommand : Command<SshSettings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public SshListCommand(ISshTargetConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override int Execute(CommandContext context, SshSettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var targets = await _configService.ListTargetsAsync();

        if (targets.Count == 0)
        {
            _console.MarkupLine("[yellow]No SSH targets registered.[/]");
            _console.MarkupLine("[dim]Use 'pks ssh register user@host -i /path/to/key' to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[yellow]Label[/]")
            .AddColumn("[cyan]Host[/]")
            .AddColumn("[cyan]User[/]")
            .AddColumn("[cyan]Port[/]")
            .AddColumn("[cyan]Key[/]")
            .AddColumn("[dim]Registered[/]");

        foreach (var target in targets)
        {
            table.AddRow(
                target.Label ?? "[dim]-[/]",
                target.Host,
                target.Username,
                target.Port.ToString(),
                target.KeyPath.EscapeMarkup(),
                target.RegisteredAt.ToString("yyyy-MM-dd HH:mm"));
        }

        _console.Write(table);
        _console.MarkupLine($"[dim]{targets.Count} target(s) registered[/]");

        return 0;
    }
}
