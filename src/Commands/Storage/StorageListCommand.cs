using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Storage;

[Description("List storage resources across authenticated providers")]
public class StorageListCommand : Command<StorageListCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAnsiConsole _console;

    public StorageListCommand(FileShareProviderRegistry registry, IAnsiConsole console)
    {
        _registry = registry;
        _console = console;
    }

    public class Settings : StorageSettings
    {
        [CommandOption("--account")]
        [Description("Only show shares of this storage account")]
        public string? AccountName { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
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

            if (!string.IsNullOrWhiteSpace(settings.AccountName))
                resources = resources
                    .Where(r => string.Equals(r.AccountName, settings.AccountName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (resources.Count == 0)
            {
                table.AddRow(Markup.Escape(provider.ProviderName), "-", "-", "[dim]No resources found[/]");
                continue;
            }

            // Several accounts come back interleaved from a parallel listing; group them.
            foreach (var resource in resources
                         .OrderBy(r => r.AccountName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(r => r.ResourceName, StringComparer.OrdinalIgnoreCase))
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
