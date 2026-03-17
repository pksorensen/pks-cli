using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Switch Azure AI Foundry resource and/or model without re-authenticating.
/// Uses the stored refresh token to get a management token for resource discovery.
/// </summary>
[Description("Switch Foundry resource or model without re-authenticating")]
public class FoundrySelectCommand : Command<FoundrySettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAnsiConsole _console;

    public FoundrySelectCommand(IAzureFoundryAuthService authService, AzureFoundryAuthConfig config, IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _console = console;
    }

    public override int Execute(CommandContext context, FoundrySettings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
    {
        var credentials = await _authService.GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] to authenticate first.[/]");
            return 1;
        }

        // Get management token using stored refresh token
        var managementToken = await _authService.GetAccessTokenAsync(_config.ManagementScope);
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain management token. Try re-authenticating with [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        // List subscriptions
        var subscriptions = await _authService.ListSubscriptionsAsync(managementToken);
        if (subscriptions.Count == 0)
        {
            _console.MarkupLine("[red]No Azure subscriptions found.[/]");
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
            // Pre-select current subscription if possible
            var choices = subscriptions.Select(s => s.DisplayName).ToList();
            var prompt = new SelectionPrompt<string>()
                .Title("[cyan]Select an Azure subscription:[/]")
                .AddChoices(choices);

            var currentIdx = choices.IndexOf(credentials.SelectedSubscriptionName);
            if (currentIdx >= 0)
                prompt.HighlightStyle(new Style(Color.Green));

            var subName = _console.Prompt(prompt);
            selectedSubscription = subscriptions.First(s => s.DisplayName == subName);
        }

        // List Foundry resources
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
            _console.MarkupLine($"[dim]Using resource: [bold]{Markup.Escape(selectedResource.Name)}[/][/]");
        }
        else
        {
            var resourceName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure AI Foundry resource:[/]")
                    .AddChoices(resources.Select(r =>
                    {
                        var rg = ParseResourceGroup(r.Id);
                        return $"{r.Name} ({rg})";
                    })));

            var name = resourceName.Split(' ')[0];
            selectedResource = resources.First(r => r.Name == name);
        }

        var resourceGroup = ParseResourceGroup(selectedResource.Id);

        // List deployments
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

        var foundryEndpoint = $"https://{selectedResource.Name}.services.ai.azure.com";

        // Update credentials, preserving auth info
        credentials.SelectedSubscriptionId = selectedSubscription.SubscriptionId;
        credentials.SelectedSubscriptionName = selectedSubscription.DisplayName;
        credentials.SelectedResourceEndpoint = foundryEndpoint;
        credentials.SelectedResourceName = selectedResource.Name;
        credentials.SelectedResourceGroup = resourceGroup;
        credentials.DefaultModel = selectedDeployment.Name;
        credentials.LastRefreshedAt = DateTime.UtcNow;

        await _authService.StoreCredentialsAsync(credentials);

        // Display result
        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Resource Updated[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Subscription", Markup.Escape(selectedSubscription.DisplayName));
        table.AddRow("Resource", Markup.Escape(selectedResource.Name));
        table.AddRow("Endpoint", Markup.Escape(foundryEndpoint));
        table.AddRow("Default Model", Markup.Escape(selectedDeployment.Name));
        table.AddRow("Resource Group", Markup.Escape(resourceGroup));

        _console.Write(table);
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
}
