using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Security;

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
    private readonly IManagedIdentityClient? _managedIdentity;

    public FoundryInitCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAnsiConsole console,
        IManagedIdentityClient? managedIdentity = null)
    {
        _authService = authService;
        _config = config;
        _console = console;
        _managedIdentity = managedIdentity;
    }

    public class Settings : FoundrySettings
    {
        [CommandOption("-f|--force")]
        [Description("Force re-authentication even if already authenticated")]
        public bool Force { get; set; }

        [CommandOption("-t|--tenant")]
        [Description("Azure AD tenant ID (defaults to 'common')")]
        public string? TenantId { get; set; }

        [CommandOption("--managed-identity")]
        [Description("Use this Azure machine's managed identity instead of signing in (no token is stored)")]
        public bool ManagedIdentity { get; set; }

        [CommandOption("--client-id <ID>")]
        [Description("Client id of a user-assigned managed identity (default: the system-assigned one)")]
        public string? ManagedIdentityClientId { get; set; }

        [CommandOption("--subscription <ID>")]
        [Description("Subscription id or name to use, instead of choosing from a list")]
        public string? Subscription { get; set; }

        [CommandOption("--resource <NAME>")]
        [Description("Foundry resource (account) name to use, instead of choosing from a list")]
        public string? Resource { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Never prompt: enable every deployment, default to the first, skip the API key")]
        public bool Yes { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return RunAsync(settings).GetAwaiter().GetResult();
    }

    /// <summary>The whole flow, callable without a <see cref="CommandContext"/> — `pks providers
    /// init` runs it as one of its steps.</summary>
    public async Task<int> RunAsync(Settings settings)
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

        var useManagedIdentity = settings.ManagedIdentity;
        if (!useManagedIdentity && !settings.Yes && string.IsNullOrEmpty(settings.TenantId)
            && _managedIdentity is not null && await _managedIdentity.IsAvailableAsync())
        {
            useManagedIdentity = _console.Confirm(
                "[cyan]This machine has an Azure managed identity.[/] Use it for Foundry instead of signing in?", true);
        }

        string tenantId;
        var refreshToken = SecretValue.None;
        if (useManagedIdentity)
        {
            var mi = await StoreManagedIdentityAsync(settings.ManagedIdentityClientId);
            if (mi is null) return 1;
            tenantId = mi;
        }
        else if (settings.Yes)
        {
            _console.MarkupLine("[red]--yes needs --managed-identity: signing in is interactive.[/]");
            return 1;
        }
        else
        {
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

            refreshToken = SecretValue.From(authResult.RefreshToken);

            // Store initial credentials (tenantId + refreshToken) so token refresh works
            await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
            {
                TenantId = tenantId,
                RefreshToken = refreshToken,
                CreatedAt = DateTime.UtcNow,
                LastRefreshedAt = DateTime.UtcNow,
            });
        }

        // Get management token to list subscriptions and resources
        string? managementToken;
        using (_activitySource.StartActivity("foundry.get_management_token"))
            managementToken = await _authService.GetAccessTokenAsync(_config.ManagementScope);
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain management access token.[/]");
            return 1;
        }
        var nonInteractive = settings.Yes;

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
            if (useManagedIdentity) WriteManagedIdentityRoleHint();
            return 1;
        }

        AzureSubscription selectedSubscription;
        if (!string.IsNullOrWhiteSpace(settings.Subscription))
        {
            var wanted = settings.Subscription.Trim();
            var match = subscriptions.FirstOrDefault(s =>
                s.SubscriptionId.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || s.DisplayName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"[red]Subscription '{Markup.Escape(wanted)}' is not visible to this identity.[/]");
                return 1;
            }
            selectedSubscription = match;
        }
        else if (subscriptions.Count == 1)
        {
            selectedSubscription = subscriptions[0];
            _console.MarkupLine($"[dim]Using subscription: [bold]{Markup.Escape(selectedSubscription.DisplayName)}[/][/]");
        }
        else if (nonInteractive)
        {
            _console.MarkupLine("[red]Several subscriptions are visible; pick one with --subscription.[/]");
            return 1;
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
            if (useManagedIdentity) WriteManagedIdentityRoleHint();
            return 1;
        }

        CognitiveServicesAccount selectedResource;
        if (!string.IsNullOrWhiteSpace(settings.Resource))
        {
            var match = resources.FirstOrDefault(r => r.Name.Equals(settings.Resource.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _console.MarkupLine($"[red]Foundry resource '{Markup.Escape(settings.Resource)}' is not in this subscription, or not visible to this identity.[/]");
                return 1;
            }
            selectedResource = match;
        }
        else if (resources.Count == 1)
        {
            selectedResource = resources[0];
            _console.MarkupLine($"[dim]Using resource: [bold]{Markup.Escape(selectedResource.Name)}[/] ({Markup.Escape(selectedResource.Properties.Endpoint)})[/]");
        }
        else if (nonInteractive)
        {
            _console.MarkupLine("[red]Several Foundry resources are visible; pick one with --resource.[/]");
            return 1;
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
        if (deploymentPool.Count == 1 || nonInteractive)
        {
            selectedDeploymentNames = deploymentPool.Select(d => d.Name).ToList();
            _console.MarkupLine($"[dim]Using deployments: [bold]{Markup.Escape(string.Join(", ", selectedDeploymentNames))}[/][/]");
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
        if (selectedDeploymentNames.Count == 1 || nonInteractive)
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

        // Optional API key — enables launching claude without az CLI (DefaultAzureCredential fallback).
        // Not asked with a managed identity: the point of one is that no key sits on the box.
        string? apiKey = null;
        if (!useManagedIdentity && !nonInteractive)
        {
            _console.WriteLine();
            _console.MarkupLine("[dim]An Azure resource API key allows launching claude without 'az login' in the devcontainer.[/]");
            _console.MarkupLine("[dim]Find it in the Azure AI Foundry portal under your resource → Keys and Endpoint.[/]");
            var apiKeyInput = _console.Prompt(
                new TextPrompt<string>("[cyan]Azure resource API key[/] [dim](optional — press Enter to skip):[/]")
                    .AllowEmpty());
            apiKey = string.IsNullOrWhiteSpace(apiKeyInput) ? null : apiKeyInput.Trim();
        }

        // Store complete credentials
        using (_activitySource.StartActivity("foundry.store_credentials"))
            await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
            {
                TenantId = tenantId,
                RefreshToken = refreshToken,
                AuthMode = useManagedIdentity ? FoundryStoredCredentials.ManagedIdentityMode : string.Empty,
                ManagedIdentityClientId = useManagedIdentity ? settings.ManagedIdentityClientId : null,
                SelectedSubscriptionId = selectedSubscription.SubscriptionId,
                SelectedSubscriptionName = selectedSubscription.DisplayName,
                SelectedResourceEndpoint = foundryEndpoint,
                SelectedResourceName = selectedResource.Name,
                SelectedResourceGroup = resourceGroup,
                DefaultModel = selectedDeployment.Name,
                EnabledModels = selectedDeploymentNames,
                ApiKey = SecretValue.From(apiKey),
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
        table.AddRow("Sign-in", useManagedIdentity ? "[green]managed identity[/] [dim](nothing stored)[/]" : "user (refresh token)");
        table.AddRow("API Key", apiKey != null ? "[green]stored[/]" : "[dim]not set — using DefaultAzureCredential[/]");

        _console.Write(table);

        _console.WriteLine();
        _console.MarkupLine("[dim]Tip: Use [bold]pks foundry token[/] to get an access token for API calls.[/]");
        if (useManagedIdentity)
            _console.MarkupLine("[dim]Model calls need a data-plane role for the identity on this resource: 'Azure AI User' (or 'Cognitive Services OpenAI User').[/]");
        else if (apiKey == null)
            _console.MarkupLine("[dim]Note: Without an API key, claude in the devcontainer needs 'az login' or AZURE_CLIENT_ID/SECRET env vars.[/]");

        return 0;
    }

    /// <summary>
    /// Probes the managed identity and stores a Foundry session that points at it. Returns the tenant
    /// id read from the token (for display — IMDS already knows the tenant), or null when the machine
    /// has no usable identity.
    /// </summary>
    private async Task<string?> StoreManagedIdentityAsync(string? clientId)
    {
        if (_managedIdentity is null || !await _managedIdentity.IsAvailableAsync())
        {
            _console.MarkupLine("[red]No Azure managed identity here: the instance metadata service did not answer.[/]");
            return null;
        }
        var token = await _managedIdentity.GetTokenAsync(_config.ManagementScope, clientId);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine(string.IsNullOrEmpty(clientId)
                ? "[red]This VM has no system-assigned identity.[/] Turn it on, or pass [bold]--client-id[/] for a user-assigned one."
                : $"[red]No managed identity with client id {Markup.Escape(clientId)} on this VM.[/]");
            return null;
        }

        var tenantId = TenantFromToken(token) ?? string.Empty;
        await _authService.StoreCredentialsAsync(new FoundryStoredCredentials
        {
            TenantId = tenantId,
            AuthMode = FoundryStoredCredentials.ManagedIdentityMode,
            ManagedIdentityClientId = clientId,
            CreatedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        });
        _console.MarkupLine("[green]Using the machine's managed identity.[/]");
        return tenantId;
    }

    private void WriteManagedIdentityRoleHint()
    {
        _console.MarkupLine("[yellow]The managed identity sees nothing yet — it needs roles:[/]");
        _console.MarkupLine("  [bold]Reader[/] on the subscription or resource group that holds the Foundry resource (to list it),");
        _console.MarkupLine("  [bold]Azure AI User[/] (or [bold]Cognitive Services OpenAI User[/]) on the Foundry resource (to call models).");
        _console.MarkupLine("[dim]Role assignments can take a few minutes to apply; run this again afterwards.[/]");
    }

    /// <summary>The <c>tid</c> claim of an access token. Display only — never validated here.</summary>
    internal static string? TenantFromToken(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("tid", out var tid) ? tid.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
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
