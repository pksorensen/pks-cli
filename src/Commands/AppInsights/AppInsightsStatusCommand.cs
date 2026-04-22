using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.AppInsights;

[Description("Show Application Insights configuration and connection status")]
public class AppInsightsStatusCommand : Command<AppInsightsStatusCommand.Settings>
{
    public class Settings : AppInsightsSettings { }

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public AppInsightsStatusCommand(
        IAppInsightsConfigService configService,
        IAppInsightsQueryService queryService,
        IAnsiConsole console)
    {
        _configService = configService;
        _queryService = queryService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var isConfigured = await _configService.IsConfiguredAsync();

        if (!isConfigured)
        {
            _console.MarkupLine("[yellow]Application Insights is not configured.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks appinsights init[/] to configure.[/]");
            return 0;
        }

        var config = await _configService.GetConfigAsync();
        if (config is null) return 0;

        var maskedKey = config.ApiKey.Length > 8
            ? config.ApiKey[..4] + "***" + config.ApiKey[^4..]
            : "***";

        var table = new Table().Border(TableBorder.Rounded).AddColumn("Setting").AddColumn("Value");
        table.AddRow("App ID", config.AppId);
        table.AddRow("API Key", maskedKey);
        table.AddRow("Resource", config.ResourceName ?? "[dim]not set[/]");
        table.AddRow("Configured At", config.RegisteredAt == DateTime.MinValue
            ? "[dim]unknown[/]"
            : config.RegisteredAt.ToString("yyyy-MM-dd HH:mm") + " UTC");

        _console.Write(table);
        _console.WriteLine();

        await _console.Status().StartAsync("Testing connection...", async ctx =>
        {
            var result = await _queryService.TestConnectionAsync();
            if (result.Success)
                _console.MarkupLine($"[green]Connected[/] - {(result.ResourceName ?? "Application Insights").EscapeMarkup()}");
            else
                _console.MarkupLine($"[red]Connection failed:[/] {(result.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
        });

        return 0;
    }
}
