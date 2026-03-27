using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Interactive Azure AI Foundry authentication via OAuth2 device code flow.
/// Authenticates, discovers subscriptions/resources/deployments, and stores
/// credentials for use with Foundry API calls.
/// </summary>
[Description("Authenticate with Azure AI Foundry")]
public class FoundryInitCommand : Command<FoundryInitCommand.Settings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public FoundryInitCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config, IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public class Settings : FoundrySettings
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
            _console.MarkupLine($"[green]Already authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine($"[green]Resource: [bold]{Markup.Escape(existing!.SelectedResourceName)}[/] ({Markup.Escape(existing.SelectedSubscriptionName)})[/]");
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
                new TextPrompt<string>("[cyan]Enter your email address[/] [dim](or press Enter to sign in with 'common' tenant)[/]:")
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

        _console.MarkupLine("[cyan]Starting Azure AI Foundry authentication...[/]");
        _console.MarkupLine("[dim]A browser window will open. If it doesn't, use the URL printed below.[/]");
        _console.WriteLine();

        FoundryAuthResult authResult;
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

        // Store initial credentials (tenantId + refreshToken) so token refresh works
        await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authResult.RefreshToken ?? string.Empty,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        });

        // Get management token to list subscriptions and resources
        var managementToken = await _authService.GetAccessTokenAsync(_config.ManagementScope);
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

        // List Foundry resources (Cognitive Services accounts)
        var resources = await _authService.ListFoundryResourcesAsync(managementToken, selectedSubscription.SubscriptionId);
        if (resources.Count == 0)
        {
            _console.MarkupLine("[red]No Azure AI Foundry resources found in this subscription.[/]");
            return 1;
        }

        CognitiveServicesAccount selectedResource;
        if (resources.Count == 1)
        {
            selectedResource = resources[0];
            _console.MarkupLine($"[dim]Using resource: [bold]{Markup.Escape(selectedResource.Name)}[/] ({Markup.Escape(selectedResource.Properties.Endpoint)})[/]");
        }
        else
        {
            var resourceDisplay = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure AI Foundry resource:[/]")
                    .AddChoices(resources.Select(r =>
                    {
                        var rg = ParseResourceGroup(r.Id);
                        return $"{r.Name} ({rg})";
                    })));

            var resourceName = resourceDisplay.Split(' ')[0];
            selectedResource = resources.First(r => r.Name == resourceName);
        }

        var resourceGroup = ParseResourceGroup(selectedResource.Id);

        _console.MarkupLine($"[dim]Endpoint: [bold]{Markup.Escape(selectedResource.Properties.Endpoint)}[/][/]");

        // List deployments for the selected resource
        var deployments = await _authService.ListDeploymentsAsync(managementToken, selectedSubscription.SubscriptionId, resourceGroup, selectedResource.Name);
        if (deployments.Count == 0)
        {
            _console.MarkupLine("[red]No model deployments found for this resource.[/]");
            return 1;
        }

        FoundryDeployment selectedDeployment;
        if (deployments.Count == 1)
        {
            selectedDeployment = deployments[0];
            _console.MarkupLine($"[dim]Using deployment: [bold]{Markup.Escape(selectedDeployment.Name)}[/] (model: {Markup.Escape(selectedDeployment.Properties.Model.Name)})[/]");
        }
        else
        {
            var deploymentDisplay = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a default model deployment:[/]")
                    .AddChoices(deployments.Select(d => $"{d.Name} (model: {d.Properties.Model.Name})")));

            var deploymentName = deploymentDisplay.Split(' ')[0];
            selectedDeployment = deployments.First(d => d.Name == deploymentName);
        }

        // Derive the AI Foundry inference endpoint from the resource name.
        // The ARM API returns the older cognitiveservices.azure.com endpoint,
        // but the Anthropic-compatible API lives at services.ai.azure.com.
        var foundryEndpoint = $"https://{selectedResource.Name}.services.ai.azure.com";

        // Store complete credentials
        await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
        {
            TenantId = tenantId,
            RefreshToken = authResult.RefreshToken ?? string.Empty,
            SelectedSubscriptionId = selectedSubscription.SubscriptionId,
            SelectedSubscriptionName = selectedSubscription.DisplayName,
            SelectedResourceEndpoint = foundryEndpoint,
            SelectedResourceName = selectedResource.Name,
            SelectedResourceGroup = resourceGroup,
            DefaultModel = selectedDeployment.Name,
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
        table.AddRow("Resource", Markup.Escape(selectedResource.Name));
        table.AddRow("Endpoint", Markup.Escape(foundryEndpoint));
        table.AddRow("Default Model", Markup.Escape(selectedDeployment.Name));
        table.AddRow("Resource Group", Markup.Escape(resourceGroup));

        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine("[dim]Tip: Use [bold]pks foundry token[/] to get an access token for API calls.[/]");

        return 0;
    }

    /// <summary>
    /// Parses the resource group name from an ARM resource ID.
    /// Format: /subscriptions/{sub}/resourceGroups/{rg}/providers/...
    /// </summary>
    private static string ParseResourceGroup(string resourceId)
    {
        var parts = resourceId.Split('/');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }
        return string.Empty;
    }
}
