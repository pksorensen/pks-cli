using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.AppInsights;

[Description("Configure Application Insights App ID and API key")]
public class AppInsightsInitCommand : Command<AppInsightsInitCommand.Settings>
{
    public class Settings : AppInsightsSettings
    {
        [CommandOption("-f|--force")]
        [Description("Re-configure even if already configured")]
        public bool Force { get; set; }

        [CommandOption("--app-id <ID>")]
        [Description("Application Insights Application ID (skip interactive prompt)")]
        public string? AppId { get; set; }

        [CommandOption("--api-key <KEY>")]
        [Description("Application Insights API Key (skip interactive prompt)")]
        public string? ApiKey { get; set; }
    }

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public AppInsightsInitCommand(
        IAppInsightsConfigService configService,
        IAppInsightsQueryService queryService,
        IAnsiConsole console)
    {
        _configService = configService;
        _queryService = queryService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _configService.IsConfiguredAsync())
        {
            var existing = await _configService.GetConfigAsync();
            _console.MarkupLine("[green]Application Insights already configured.[/]");
            if (existing is not null)
            {
                _console.MarkupLine($"  App ID: [cyan]{existing.AppId.EscapeMarkup()}[/]");
                _console.MarkupLine($"  Resource: [dim]{(existing.ResourceName ?? "unknown").EscapeMarkup()}[/]");
            }
            _console.MarkupLine("[dim]Use [cyan]--force[/] to reconfigure.[/]");
            return 0;
        }

        _console.Write(new Panel("""
            [bold]How to get your Application Insights credentials:[/]

            1. Open [link=https://portal.azure.com]Azure Portal[/]
            2. Navigate to your [cyan]Application Insights[/] resource
            3. Under [cyan]Configure[/] -> [cyan]API Access[/]
            4. Copy the [cyan]Application ID[/]
            5. Create a new [cyan]API key[/] with [italic]Read telemetry[/] permission
            """)
            .Header("[blue] Application Insights Setup [/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Blue));
        _console.WriteLine();

        var appId = settings.AppId;
        var apiKey = settings.ApiKey;

        if (string.IsNullOrWhiteSpace(appId))
            appId = _console.Ask<string>("[cyan]Application ID:[/]").Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = _console.Prompt(
                new TextPrompt<string>("[cyan]API Key:[/]").Secret()).Trim();

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey))
        {
            _console.MarkupLine("[red]App ID and API Key are required.[/]");
            return 1;
        }

        AppInsightsConnectionResult? connectionResult = null;
        await _console.Status().StartAsync("Validating connection...", async ctx =>
        {
            connectionResult = await _queryService.TestConnectionAsync();
        });

        if (connectionResult is null || !connectionResult.Success)
        {
            _console.MarkupLine($"[red]Connection failed:[/] {(connectionResult?.ErrorMessage ?? "Unknown error").EscapeMarkup()}");
            _console.MarkupLine("[dim]Check your Application ID and API Key and try again.[/]");
            return 1;
        }

        // Store credentials with resolved resource name
        await _configService.StoreConfigAsync(appId, apiKey, connectionResult.ResourceName);

        _console.MarkupLine($"[green]Connected to Application Insights[/]");
        if (!string.IsNullOrWhiteSpace(connectionResult.ResourceName))
            _console.MarkupLine($"  Resource: [cyan]{connectionResult.ResourceName.EscapeMarkup()}[/]");
        _console.WriteLine();
        _console.MarkupLine("[dim]You can now use [cyan]pks otel errors[/] to query telemetry data.[/]");

        return 0;
    }
}
