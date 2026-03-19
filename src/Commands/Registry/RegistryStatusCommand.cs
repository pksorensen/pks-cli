using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Registry;

/// <summary>
/// List registered container registries.
/// Usage: pks registry status [hostname]
/// </summary>
public class RegistryStatusCommand : Command<RegistrySettings>
{
    private readonly IRegistryConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public RegistryStatusCommand(IRegistryConfigurationService configService, IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override int Execute(CommandContext context, RegistrySettings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(CommandContext context, RegistrySettings settings)
    {
        var panel = new Panel("[bold cyan]Registry Status[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();

        if (!string.IsNullOrWhiteSpace(settings.Hostname))
        {
            var entry = await _configService.GetByHostnameAsync(settings.Hostname);
            if (entry == null)
            {
                _console.MarkupLine($"[red]No registry registered for '{settings.Hostname.EscapeMarkup()}'[/]");
                return 1;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .AddColumn("[yellow]Property[/]")
                .AddColumn("[cyan]Value[/]");

            table.AddRow("Hostname", entry.Hostname);
            table.AddRow("Username", entry.Username);
            table.AddRow("Registered", entry.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

            _console.Write(table);
        }
        else
        {
            var registries = await _configService.ListAsync();

            if (registries.Count == 0)
            {
                _console.MarkupLine("[yellow]No registries registered. Run 'pks registry init' to add one.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Cyan1)
                .AddColumn("[yellow]Hostname[/]")
                .AddColumn("[cyan]Username[/]")
                .AddColumn("[dim]Registered[/]");

            foreach (var r in registries)
                table.AddRow(r.Hostname, r.Username, r.RegisteredAt.ToString("yyyy-MM-dd HH:mm:ss UTC"));

            _console.Write(table);
        }

        return 0;
    }
}
