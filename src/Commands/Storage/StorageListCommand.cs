using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

[Description("List storage resources across authenticated providers")]
public class StorageListCommand : Command<StorageSettings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public StorageListCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public override int Execute(CommandContext context, StorageSettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var authenticated = (await _registry.GetAuthenticatedProvidersAsync()).ToList();

        if (authenticated.Count == 0)
        {
            _console.MarkupLine("[yellow]No authenticated storage providers found.[/]");
            _console.MarkupLine("[dim]Run [bold]pks fileshare init[/] to authenticate with a provider.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Available Storage Resources[/]");

        table.AddColumn("[bold]Provider[/]");
        table.AddColumn("[bold]Account[/]");
        table.AddColumn("[bold]Share[/]");
        table.AddColumn("[bold]Details[/]");

        foreach (var provider in authenticated)
        {
            var resources = (await provider.ListResourcesAsync()).ToList();

            if (resources.Count == 0)
            {
                table.AddRow(Markup.Escape(provider.ProviderName), "-", "-", "[dim]No resources found[/]");
                continue;
            }

            foreach (var resource in resources)
            {
                table.AddRow(
                    Markup.Escape(resource.ProviderName),
                    Markup.Escape(resource.AccountName),
                    Markup.Escape(resource.ResourceName),
                    Markup.Escape(resource.Description ?? string.Empty));
            }
        }

        _console.Write(table);
        return 0;
    }
}
