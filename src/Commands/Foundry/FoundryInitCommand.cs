using System.ComponentModel;
using System.Diagnostics;
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
    /// <summary>ActivitySource name for foundry commands. Referenced by Program.cs SetupTracing.</summary>
    public const string ActivitySourceName = "pks-cli.foundry";
    private static readonly ActivitySource _activitySource = new(ActivitySourceName, "1.0.0");

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
        using var rootSpan = _activitySource.StartActivity("foundry.init");
        rootSpan?.SetTag("foundry.force", settings.Force);
        rootSpan?.SetTag("foundry.tenant_id_provided", !string.IsNullOrEmpty(settings.TenantId));

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
                using var discoverSpan = _activitySource.StartActivity("foundry.tenant_discover");
                var discoveredTenant = await _authService.DiscoverTenantAsync(loginHint);
                discoverSpan?.SetTag("foundry.tenant_discovered", !string.IsNullOrEmpty(discoveredTenant));
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
        {
            using var loginSpan = _activitySource.StartActivity("foundry.login");
            loginSpan?.SetTag("foundry.tenant", tenantId);
            try
            {
                authResult = await _authService.InitiateLoginAsync(tenantId, loginHint);
            }
            catch (OperationCanceledException)
            {
                loginSpan?.SetStatus(ActivityStatusCode.Error, "timeout");
                _console.MarkupLine("[red]Authentication timed out.[/]");
                return 1;
            }
            catch (Exception ex)
            {
                loginSpan?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _console.MarkupLine($"[red]Authentication failed: {Markup.Escape(ex.Message)}[/]");
                return 1;
            }
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
        string? managementToken;
        using (_activitySource.StartActivity("foundry.get_management_token"))
            managementToken = await _authService.GetAccessTokenAsync(_config.ManagementScope);
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain management access token.[/]");
            return 1;
        }

        // List subscriptions
        List<AzureSubscription> subscriptions;
        using (var span = _activitySource.StartActivity("foundry.list_subscriptions"))
        {
            subscriptions = await _authService.ListSubscriptionsAsync(managementToken);
            span?.SetTag("foundry.subscription_count", subscriptions.Count);
        }
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
        List<CognitiveServicesAccount> resources;
        using (var span = _activitySource.StartActivity("foundry.list_resources"))
        {
            span?.SetTag("foundry.subscription_id", selectedSubscription.SubscriptionId);
            resources = await _authService.ListFoundryResourcesAsync(managementToken, selectedSubscription.SubscriptionId);
            span?.SetTag("foundry.resource_count", resources.Count);
        }
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
        List<FoundryDeployment> deployments;
        using (var span = _activitySource.StartActivity("foundry.list_deployments"))
        {
            span?.SetTag("foundry.resource_name", selectedResource.Name);
            span?.SetTag("foundry.resource_group", resourceGroup);
            deployments = await _authService.ListDeploymentsAsync(managementToken, selectedSubscription.SubscriptionId, resourceGroup, selectedResource.Name);
            span?.SetTag("foundry.deployment_count", deployments.Count);
        }
        if (deployments.Count == 0)
        {
            _console.MarkupLine("[red]No model deployments found for this resource.[/]");
            return 1;
        }

        // Offer all deployments — TTS, embeddings, Claude, etc. The user picks which to enable.
        var deploymentPool = deployments;

        List<string> selectedDeploymentNames;
        if (deploymentPool.Count == 1)
        {
            selectedDeploymentNames = new List<string> { deploymentPool[0].Name };
            _console.MarkupLine($"[dim]Using deployment: [bold]{Markup.Escape(deploymentPool[0].Name)}[/] (model: {Markup.Escape(deploymentPool[0].Properties.Model.Name)})[/]");
        }
        else
        {
            var choiceMap = deploymentPool.ToDictionary(
                d => $"{d.Name} (model: {d.Properties.Model.Name}, format: {d.Properties.Model.Format})",
                d => d.Name);

            var selectedDisplayNames = _console.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title("[cyan]Select model deployments to enable (at least 1):[/]")
                    .Required()
                    .AddChoices(choiceMap.Keys));

            selectedDeploymentNames = selectedDisplayNames.Select(n => choiceMap[n]).ToList();
        }

        string defaultDeploymentName;
        if (selectedDeploymentNames.Count == 1)
        {
            defaultDeploymentName = selectedDeploymentNames[0];
        }
        else
        {
            defaultDeploymentName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select the default model deployment:[/]")
                    .AddChoices(selectedDeploymentNames));
        }

        var selectedDeployment = deployments.First(d => d.Name == defaultDeploymentName);

        // Derive the AI Foundry inference endpoint from the resource name.
        // The ARM API returns the older cognitiveservices.azure.com endpoint,
        // but the Anthropic-compatible API lives at services.ai.azure.com.
        var foundryEndpoint = $"https://{selectedResource.Name}.services.ai.azure.com";

        // Optional API key — enables launching claude without az CLI (DefaultAzureCredential fallback)
        _console.WriteLine();
        _console.MarkupLine("[dim]An Azure resource API key allows launching claude without 'az login' in the devcontainer.[/]");
        _console.MarkupLine("[dim]Find it in the Azure AI Foundry portal under your resource → Keys and Endpoint.[/]");
        var apiKeyInput = _console.Prompt(
            new TextPrompt<string>("[cyan]Azure resource API key[/] [dim](optional — press Enter to skip):[/]")
                .AllowEmpty());
        var apiKey = string.IsNullOrWhiteSpace(apiKeyInput) ? null : apiKeyInput.Trim();

        // Store complete credentials
        using (_activitySource.StartActivity("foundry.store_credentials"))
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
                EnabledModels = selectedDeploymentNames,
                ApiKey = apiKey,
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
        table.AddRow("Enabled Models", Markup.Escape(string.Join(", ", selectedDeploymentNames)));
        table.AddRow("Resource Group", Markup.Escape(resourceGroup));
        table.AddRow("API Key", apiKey != null ? "[green]stored[/]" : "[dim]not set — using DefaultAzureCredential[/]");

        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine("[dim]Tip: Use [bold]pks foundry token[/] to get an access token for API calls.[/]");
        if (apiKey == null)
            _console.MarkupLine("[dim]Note: Without an API key, claude in the devcontainer needs 'az login' or AZURE_CLIENT_ID/SECRET env vars.[/]");

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
