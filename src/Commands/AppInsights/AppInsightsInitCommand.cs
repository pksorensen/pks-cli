using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
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

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common' or auto-discovered from email)")]
        public string? TenantId { get; set; }
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
            var authResult = await AuthenticateAsync(settings.TenantId);
            if (authResult is null) return 1;
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
        _console.MarkupLine("[dim]Discovering Application Insights resources...[/]");

        var components = await _authService.ListAppInsightsResourcesAsync(managementToken, selectedSub.SubscriptionId);

        if (components.Count == 0)
        {
            _console.MarkupLine("[red]No Application Insights resources found in this subscription.[/]");
            return 1;
        }

        AppInsightsComponent selected;
        if (components.Count == 1)
        {
            selected = components[0];
            var rg = ParseResourceGroup(selected.Id);
            _console.MarkupLine($"[dim]Resource: [bold]{selected.Name.EscapeMarkup()}[/] ({rg.EscapeMarkup()})[/]");
        }
        else
        {
            var choices = components.Select(c => $"{c.Name}  ({ParseResourceGroup(c.Id)})").ToList();
            var pick = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Application Insights resource:[/]")
                    .AddChoices(choices));
            selected = components[choices.IndexOf(pick)];
        }

        await _configService.StoreConfigAsync(selected.Properties.AppId, selected.Name, selectedSub.SubscriptionId);

        _console.MarkupLine($"[green]✓ Configured:[/] [cyan]{selected.Name.EscapeMarkup()}[/]");
        _console.MarkupLine("[dim]Run [cyan]pks otel errors[/] to query telemetry data.[/]");

        return 0;
    }

    private static string ParseResourceGroup(string resourceId)
    {
        var parts = resourceId.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }
        return string.Empty;
    }

    private async Task<FoundryAuthResult?> AuthenticateAsync(string? tenantIdOverride)
    {
        string tenantId;
        string? loginHint = null;

        if (!string.IsNullOrEmpty(tenantIdOverride))
        {
            tenantId = tenantIdOverride;
        }
        else
        {
            var email = _console.Prompt(
                new TextPrompt<string>("[cyan]Enter your email address[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(email))
            {
                loginHint = email.Trim();
                _console.MarkupLine("[dim]Discovering tenant...[/]");
                var discovered = await _authService.DiscoverTenantAsync(loginHint);
                tenantId = string.IsNullOrEmpty(discovered) ? "common" : discovered;
                if (!string.IsNullOrEmpty(discovered))
                    _console.MarkupLine($"[dim]Tenant: [bold]{tenantId.EscapeMarkup()}[/][/]");
            }
            else
            {
                tenantId = "common";
            }
        }

        _console.MarkupLine("[cyan]Starting Azure authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        try
        {
            var result = await _authService.InitiateLoginAsync(tenantId, loginHint);
            await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
            {
                TenantId = tenantId,
                RefreshToken = result.RefreshToken ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
                LastRefreshedAt = DateTime.UtcNow,
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[red]Authentication timed out.[/]");
            return null;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed: {ex.Message.EscapeMarkup()}[/]");
            return null;
        }
    }
}
