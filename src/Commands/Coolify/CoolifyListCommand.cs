using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Coolify;

/// <summary>
/// List registered Coolify instances.
/// Usage: pks coolify list
/// </summary>
public class CoolifyListCommand : Command<CoolifySettings>
{
    private readonly ICoolifyConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public CoolifyListCommand(
        ICoolifyConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override int Execute(CommandContext context, CoolifySettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var instances = await _configService.ListInstancesAsync();

        if (!instances.Any())
        {
            _console.MarkupLine("[yellow]No Coolify instances registered.[/]");
            _console.MarkupLine("[cyan]Use 'pks coolify register <url>' to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Title($"[cyan]Coolify Instances ({instances.Count})[/]")
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn("[yellow]ID[/]")
            .AddColumn("[cyan]URL[/]")
            .AddColumn("[blue]Registered[/]");

        foreach (var inst in instances)
        {
            table.AddRow(
                inst.Id.Length > 8 ? inst.Id[..8] + "..." : inst.Id,
                inst.Url,
                inst.RegisteredAt.ToString("yyyy-MM-dd HH:mm"));
        }

        _console.Write(table);
        return 0;
    }
}
