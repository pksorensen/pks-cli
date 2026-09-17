using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.AppInsights;

[Description("Show the registered Application Insights resources, test the enabled ones and show tenant sign-in state")]
public class AppInsightsStatusCommand : Command<AppInsightsStatusCommand.Settings>
{
    public class Settings : AppInsightsSettings { }

    private readonly IAzureResourceRegistry _registry;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public AppInsightsStatusCommand(
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        IAppInsightsQueryService queryService,
        IAnsiConsole console)
    {
        _registry = registry;
        _tenants = tenants;
        _queryService = queryService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var entries = await _registry.ListAsync(AzureResourceKind.AppInsights);
        if (await AzureResourceStatusRenderer.WriteEntriesAsync(_console, _tenants, AzureResourceKind.AppInsights, entries))
        {
            _console.WriteLine();
            foreach (var entry in entries.Where(e => e.Enabled))
            {
                var result = await _console.Status().StartAsync(
                    $"Testing {entry.Name.EscapeMarkup()}...",
                    _ => _queryService.TestConnectionAsync(entry.Key));
                if (result.Success)
                    _console.MarkupLine($"[green]Connected[/] - {(result.ResourceName ?? entry.Name).EscapeMarkup()}");
                else
                    _console.MarkupLine($"[red]Connection failed[/] - {entry.Name.EscapeMarkup()}: {(result.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
            }
        }

        await AzureResourceStatusRenderer.WriteTenantsAsync(_console, _tenants, AzureResourceKind.AppInsights);
        return 0;
    }
}
