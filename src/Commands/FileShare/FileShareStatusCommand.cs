using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.FileShares;

[Description("Show file share provider status, the registered storage accounts and tenant sign-in state")]
public class FileShareStatusCommand : Command<FileShareSettings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAzureResourceRegistry _resources;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAnsiConsole _console;

    public FileShareStatusCommand(
        FileShareProviderRegistry registry,
        IAzureResourceRegistry resources,
        IAzureTenantCredentialStore tenants,
        IAnsiConsole console)
    {
        _registry = registry;
        _resources = resources;
        _tenants = tenants;
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
        _console.WriteLine();

        var entries = await _resources.ListAsync(AzureResourceKind.Storage);
        await AzureResourceStatusRenderer.WriteEntriesAsync(_console, _tenants, AzureResourceKind.Storage, entries);
        await AzureResourceStatusRenderer.WriteTenantsAsync(_console, _tenants, AzureResourceKind.Storage);
        return 0;
    }
}
