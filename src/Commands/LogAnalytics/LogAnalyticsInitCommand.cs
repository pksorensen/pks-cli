using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.LogAnalytics;

[Description("Discover and configure a Log Analytics workspace for KQL queries")]
public class LogAnalyticsInitCommand : Command<LogAnalyticsInitCommand.Settings>
{
    public class Settings : LogAnalyticsSettings
    {
        [CommandOption("-f|--force")]
        [Description("Re-configure even if already configured")]
        public bool Force { get; set; }

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common' or auto-discovered from email)")]
        public string? TenantId { get; set; }

        [CommandOption("-s|--subscription <ID>")]
        [Description("Azure subscription ID to search (skips the subscription prompt)")]
        public string? SubscriptionId { get; set; }

        [CommandOption("-w|--workspace <NAME_OR_GUID>")]
        [Description("Workspace name or GUID to select (skips the workspace prompt)")]
        public string? Workspace { get; set; }
    }

    private readonly ILogAnalyticsConfigService _configService;
    private readonly IAzureFoundryAuthService _authService;
    private readonly IAnsiConsole _console;

    public LogAnalyticsInitCommand(
        ILogAnalyticsConfigService configService,
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
            _console.MarkupLine("[green]Log Analytics already configured.[/]");
            if (existing is not null)
                _console.MarkupLine($"  Workspace: [cyan]{(existing.WorkspaceName ?? existing.WorkspaceId).EscapeMarkup()}[/]");
            _console.MarkupLine("[dim]Use [cyan]--force[/] to reconfigure.[/]");
            return 0;
        }

        // A bare GUID is already everything the query API needs — no ARM walk.
        if (settings.Workspace is not null && Guid.TryParse(settings.Workspace.Trim(), out var directGuid))
        {
            await _configService.StoreConfigAsync(directGuid.ToString(), null, null, settings.SubscriptionId);
            _console.MarkupLine($"[green]✓ Configured workspace:[/] [cyan]{directGuid}[/]");
            _console.MarkupLine("[dim]Run [cyan]pks kusto \"Heartbeat | take 5\"[/] to query it.[/]");
            return 0;
        }

        string? managementToken = null;
        if (!await _authService.IsAuthenticatedAsync())
        {
            var authResult = await AuthenticateAsync(settings.TenantId);
            if (authResult is null) return 1;
            managementToken = authResult.AccessToken;
        }

        if (string.IsNullOrEmpty(managementToken))
            managementToken = await _authService.GetAccessTokenAsync("https://management.azure.com/.default");

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

        AzureSubscription selectedSub;
        if (!string.IsNullOrWhiteSpace(settings.SubscriptionId))
        {
            var match = subscriptions.FirstOrDefault(s =>
                s.SubscriptionId.Equals(settings.SubscriptionId.Trim(), StringComparison.OrdinalIgnoreCase) ||
                s.DisplayName.Equals(settings.SubscriptionId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"[red]Subscription not found:[/] {settings.SubscriptionId.EscapeMarkup()}");
                return 1;
            }
            selectedSub = match;
        }
        else if (subscriptions.Count == 1)
        {
            selectedSub = subscriptions[0];
        }
        else
        {
            var pick = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .PageSize(15)
                    .AddChoices(subscriptions.Select(s => s.DisplayName)));
            selectedSub = subscriptions.First(s => s.DisplayName == pick);
        }

        _console.MarkupLine($"[dim]Subscription: [bold]{selectedSub.DisplayName.EscapeMarkup()}[/][/]");
        _console.MarkupLine("[dim]Discovering Log Analytics workspaces...[/]");

        var workspaces = await _authService.ListLogAnalyticsWorkspacesAsync(managementToken, selectedSub.SubscriptionId);

        if (workspaces.Count == 0)
        {
            _console.MarkupLine("[red]No Log Analytics workspaces found in this subscription.[/]");
            return 1;
        }

        LogAnalyticsWorkspace selected;
        if (!string.IsNullOrWhiteSpace(settings.Workspace))
        {
            var match = workspaces.FirstOrDefault(w =>
                w.Name.Equals(settings.Workspace.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"[red]Workspace not found in this subscription:[/] {settings.Workspace.EscapeMarkup()}");
                _console.MarkupLine($"[dim]Available: {string.Join(", ", workspaces.Select(w => w.Name)).EscapeMarkup()}[/]");
                return 1;
            }
            selected = match;
        }
        else if (workspaces.Count == 1)
        {
            selected = workspaces[0];
            _console.MarkupLine($"[dim]Workspace: [bold]{selected.Name.EscapeMarkup()}[/] ({ParseResourceGroup(selected.Id).EscapeMarkup()})[/]");
        }
        else
        {
            var choices = workspaces.Select(w => $"{w.Name}  ({ParseResourceGroup(w.Id)}, {w.Location})").ToList();
            var pick = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a Log Analytics workspace:[/]")
                    .PageSize(15)
                    .AddChoices(choices));
            selected = workspaces[choices.IndexOf(pick)];
        }

        await _configService.StoreConfigAsync(
            selected.Properties.CustomerId, selected.Name, selected.Id, selectedSub.SubscriptionId);

        _console.MarkupLine($"[green]✓ Configured:[/] [cyan]{selected.Name.EscapeMarkup()}[/] [dim]({selected.Properties.CustomerId})[/]");
        _console.MarkupLine("[dim]Run [cyan]pks kusto \"Heartbeat | take 5\"[/] to query it.[/]");

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
            var input = _console.Prompt(
                new TextPrompt<string>("[cyan]Enter your email or tenant ID[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
            {
                tenantId = "common";
            }
            else if (Guid.TryParse(input.Trim(), out _))
            {
                tenantId = input.Trim();
                _console.MarkupLine($"[dim]Tenant: [bold]{tenantId.EscapeMarkup()}[/][/]");
            }
            else
            {
                loginHint = input.Trim();
                _console.MarkupLine("[dim]Discovering tenant...[/]");
                var discovered = await _authService.DiscoverTenantAsync(loginHint);
                tenantId = string.IsNullOrEmpty(discovered) ? "common" : discovered;
                if (!string.IsNullOrEmpty(discovered))
                    _console.MarkupLine($"[dim]Tenant: [bold]{tenantId.EscapeMarkup()}[/][/]");
            }
        }

        _console.MarkupLine("[cyan]Starting Azure authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        try
        {
            var result = await _authService.InitiateLoginAsync(
                tenantId,
                loginHint,
                scopeOverride: "https://management.azure.com/.default offline_access");
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
