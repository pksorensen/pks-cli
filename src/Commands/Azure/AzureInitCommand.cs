using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Azure;

/// <summary>
/// Interactive Azure authentication via OAuth2 PKCE flow.
/// Authenticates and selects a subscription; stores credentials for later use.
/// </summary>
[Description("Authenticate with Azure and select a subscription")]
public class AzureInitCommand : Command<AzureInitCommand.Settings>
{
    private readonly IAzureAuthService _authService;
    private readonly IAnsiConsole _console;

    public AzureInitCommand(IAzureAuthService authService, IAnsiConsole console)
    {
        _authService = authService;
        _console = console;
    }

    public class Settings : AzureSettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common')")]
        public string? TenantId { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!settings.Force && await _authService.IsAuthenticatedAsync())
        {
            var existing = await _authService.GetStoredCredentialsAsync();
            _console.MarkupLine("[green]Already authenticated with Azure.[/]");
            if (existing != null)
            {
                _console.MarkupLine($"[green]Tenant: [bold]{Markup.Escape(existing.TenantId)}[/][/]");
                _console.MarkupLine($"[green]Subscription: [bold]{Markup.Escape(existing.SubscriptionName)}[/] ({Markup.Escape(existing.SubscriptionId)})[/]");
            }
            _console.MarkupLine("[dim]Use [bold]--force[/] to re-authenticate.[/]");
            return 0;
        }

        string tenantId;
        string? loginHint = null;

        if (!string.IsNullOrEmpty(settings.TenantId))
        {
            tenantId = settings.TenantId;
        }
        else
        {
            var email = _console.Prompt(
                new TextPrompt<string>("[cyan]Enter your email address[/] [dim](or press Enter for 'common' tenant)[/]:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(email))
            {
                loginHint = email.Trim();
                _console.MarkupLine("[dim]Discovering tenant...[/]");
                var discoveredTenant = await _authService.DiscoverTenantAsync(loginHint);
                if (!string.IsNullOrEmpty(discoveredTenant))
                {
                    tenantId = discoveredTenant;
                    _console.MarkupLine($"[green]Found tenant: [bold]{Markup.Escape(tenantId)}[/][/]");
                }
                else
                {
                    tenantId = "common";
                    _console.MarkupLine("[yellow]Could not discover tenant, using 'common'.[/]");
                }
            }
            else
            {
                tenantId = "common";
            }
        }

        _console.MarkupLine("[cyan]Starting Azure authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        AzureAuthResult authResult;
        try
        {
            authResult = await _authService.InitiateLoginAsync(tenantId, loginHint);
        }
        catch (OperationCanceledException)
        {
            _console.MarkupLine("[red]Authentication timed out.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        // Store partial credentials (TenantId + RefreshToken) so token refresh works
        await _authService.StoreCredentialsAsync(new AzureStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authResult.RefreshToken ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        });

        // Get management token to list subscriptions
        var managementToken = await _authService.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain management access token.[/]");
            return 1;
        }

        // List subscriptions
        var subscriptions = await _authService.ListSubscriptionsAsync(managementToken);
        if (subscriptions.Count == 0)
        {
            _console.MarkupLine("[red]No Azure subscriptions found for this account.[/]");
            return 1;
        }

        AzureSubscription selectedSubscription;
        if (subscriptions.Count == 1)
        {
            selectedSubscription = subscriptions[0];
            _console.MarkupLine($"[dim]Using subscription: [bold]{Markup.Escape(selectedSubscription.DisplayName)}[/][/]");
        }
        else
        {
            var subName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .AddChoices(subscriptions.Select(s => s.DisplayName)));

            selectedSubscription = subscriptions.First(s => s.DisplayName == subName);
        }

        // Store complete credentials
        await _authService.StoreCredentialsAsync(new AzureStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authResult.RefreshToken ?? string.Empty,
            SubscriptionId = selectedSubscription.SubscriptionId,
            SubscriptionName = selectedSubscription.DisplayName,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        });

        // Display success
        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Authentication Successful[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Tenant", Markup.Escape(tenantId));
        table.AddRow("Subscription", Markup.Escape(selectedSubscription.DisplayName));
        table.AddRow("SubscriptionId", Markup.Escape(selectedSubscription.SubscriptionId));

        _console.Write(table);

        return 0;
    }
}
