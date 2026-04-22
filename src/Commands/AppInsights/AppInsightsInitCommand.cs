using System.ComponentModel;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.AppInsights;

[Description("Discover and configure an Application Insights resource")]
public class AppInsightsInitCommand : Command<AppInsightsInitCommand.Settings>
{
    public class Settings : AppInsightsSettings
    {
        [CommandOption("-f|--force")]
        [Description("Re-configure even if already configured")]
        public bool Force { get; set; }
    }

    private readonly IAppInsightsConfigService _configService;
    private readonly IAzureFoundryAuthService _authService;
    private readonly IAnsiConsole _console;

    public AppInsightsInitCommand(
        IAppInsightsConfigService configService,
        IAzureFoundryAuthService authService,
        IAnsiConsole console)
    {
        _configService = configService;
        _authService = authService;
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
                _console.MarkupLine($"  Resource: [cyan]{(existing.ResourceName ?? existing.AppId).EscapeMarkup()}[/]");
            _console.MarkupLine("[dim]Use [cyan]--force[/] to reconfigure.[/]");
            return 0;
        }

        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[yellow]Not signed in to Azure.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks foundry init[/] first to authenticate.[/]");
            return 1;
        }

        var managementToken = await _authService.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain Azure management token.[/]");
            return 1;
        }

        var subscriptions = await _authService.ListSubscriptionsAsync(managementToken);
        if (subscriptions.Count == 0)
        {
            _console.MarkupLine("[red]No Azure subscriptions found.[/]");
            return 1;
        }

        var selectedSub = subscriptions.Count == 1
            ? subscriptions[0]
            : subscriptions.First(s => s.DisplayName == _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .AddChoices(subscriptions.Select(s => s.DisplayName))));

        _console.MarkupLine($"[dim]Subscription: [bold]{selectedSub.DisplayName.EscapeMarkup()}[/][/]");

        List<Infrastructure.Services.Models.AppInsightsComponent> components = [];
        await _console.Status().StartAsync("Discovering Application Insights resources...", async _ =>
        {
            components = await _authService.ListAppInsightsResourcesAsync(managementToken, selectedSub.SubscriptionId);
        });

        if (components.Count == 0)
        {
            _console.MarkupLine("[red]No Application Insights resources found in this subscription.[/]");
            return 1;
        }

        var selected = components.Count == 1
            ? components[0]
            : components.First(c => c.Name == _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Application Insights resource:[/]")
                    .AddChoices(components.Select(c => c.Name))));

        await _configService.StoreConfigAsync(selected.Properties.AppId, selected.Name, selectedSub.SubscriptionId);

        _console.MarkupLine($"[green]✓ Configured:[/] [cyan]{selected.Name.EscapeMarkup()}[/]");
        _console.MarkupLine("[dim]Run [cyan]pks otel errors[/] to query telemetry data.[/]");

        return 0;
    }
}
