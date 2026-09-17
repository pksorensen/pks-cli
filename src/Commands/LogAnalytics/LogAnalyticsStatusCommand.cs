using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.LogAnalytics;

[Description("Show the registered Log Analytics workspaces, test the enabled ones and show tenant sign-in state")]
public class LogAnalyticsStatusCommand : Command<LogAnalyticsStatusCommand.Settings>
{
    public class Settings : LogAnalyticsSettings { }

    private readonly IAzureResourceRegistry _registry;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly ILogAnalyticsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public LogAnalyticsStatusCommand(
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        ILogAnalyticsQueryService queryService,
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
        var entries = await _registry.ListAsync(AzureResourceKind.LogAnalytics);
        if (await AzureResourceStatusRenderer.WriteEntriesAsync(_console, _tenants, AzureResourceKind.LogAnalytics, entries))
        {
            _console.WriteLine();
            foreach (var entry in entries.Where(e => e.Enabled))
            {
                var result = await _console.Status().StartAsync(
                    $"Testing {entry.Name.EscapeMarkup()}...",
                    _ => _queryService.TestConnectionAsync(entry));
                if (result.Success)
                    _console.MarkupLine($"[green]Connected[/] - {(result.WorkspaceName ?? entry.Name).EscapeMarkup()}");
                else
                    _console.MarkupLine($"[red]Connection failed[/] - {entry.Name.EscapeMarkup()}: {(result.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
            }
        }

        await AzureResourceStatusRenderer.WriteTenantsAsync(_console, _tenants, AzureResourceKind.LogAnalytics);
        return 0;
    }
}

internal static class LogAnalyticsStatusMarkupExtensions
{
    public static string EscapeMarkupOrDim(this string? value)
        => string.IsNullOrWhiteSpace(value) ? "[dim]not set[/]" : value.EscapeMarkup();
}
