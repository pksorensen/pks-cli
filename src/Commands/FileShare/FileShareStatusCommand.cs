using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.FileShares;

[Description("Show authentication status for all file share providers")]
public class FileShareStatusCommand : Command<FileShareSettings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public FileShareStatusCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public override int Execute(CommandContext context, FileShareSettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var providers = _registry.GetAllProviders().ToList();

        if (providers.Count == 0)
        {
            _console.MarkupLine("[yellow]No file share providers registered.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]File Share Provider Status[/]");

        table.AddColumn("[bold]Provider[/]");
        table.AddColumn("[bold]Status[/]");
        table.AddColumn("[bold]Details[/]");

        foreach (var provider in providers)
        {
            var authenticated = await provider.IsAuthenticatedAsync();
            var status = authenticated
                ? "[green]Authenticated[/]"
                : "[dim]Not authenticated[/]";

            string details;
            if (authenticated)
            {
                var resources = (await provider.ListResourcesAsync()).ToList();
                details = resources.Count > 0
                    ? $"{resources.Count} share(s) available"
                    : "No shares found";
            }
            else
            {
                details = $"Run [bold]pks fileshare init[/] to authenticate";
            }

            table.AddRow(Markup.Escape(provider.ProviderName), status, details);
        }

        _console.Write(table);
        return 0;
    }
}
