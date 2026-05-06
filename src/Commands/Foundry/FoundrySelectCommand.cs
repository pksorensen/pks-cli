using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
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

        var managementToken = await _authService.GetAccessTokenAsync(_config.ManagementScope);
        if (string.IsNullOrEmpty(managementToken))
        {
            _console.MarkupLine("[red]Failed to obtain management token. Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        // ── Subscription ────────────────────────────────────────────────────
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
            var subName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .AddChoices(subscriptions.Select(s => s.DisplayName)));
            selectedSubscription = subscriptions.First(s => s.DisplayName == subName);
        }

        // ── Resource ─────────────────────────────────────────────────────────
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
            var resourceLabel = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure AI Foundry resource:[/]")
                    .AddChoices(resources.Select(r => $"{r.Name} ({ParseResourceGroup(r.Id)})")));
            selectedResource = resources.First(r => resourceLabel.StartsWith(r.Name));
        }

        var resourceGroup = ParseResourceGroup(selectedResource.Id);

        // ── Deployments — multi-select which to enable ────────────────────
        var deployments = await _authService.ListDeploymentsAsync(
            managementToken, selectedSubscription.SubscriptionId, resourceGroup, selectedResource.Name);
        if (deployments.Count == 0)
        {
            _console.MarkupLine("[red]No model deployments found for this resource.[/]");
            return 1;
        }

        // Build display map: "claude-sonnet-4-6 (model: claude-sonnet-4-6)" → deployment name
        var choiceMap = deployments.ToDictionary(
            d => $"{d.Name} (model: {d.Properties.Model.Name})",
            d => d.Name);

        List<string> enabledNames;
        if (deployments.Count == 1)
        {
            enabledNames = [deployments[0].Name];
            _console.MarkupLine($"[dim]Using deployment: [bold]{Markup.Escape(deployments[0].Name)}[/][/]");
        }
        else
        {
            var prompt = new MultiSelectionPrompt<string>()
                .Title("[cyan]Tick the model deployments you want to enable (space to toggle, enter to confirm):[/]")
                .Required()
                .AddChoices(choiceMap.Keys);

            // Pre-tick whatever was previously enabled
            foreach (var key in choiceMap.Keys)
                if (credentials.EnabledModels.Contains(choiceMap[key]))
                    prompt.Select(key);

            var selected = _console.Prompt(prompt);
            enabledNames = selected.Select(n => choiceMap[n]).ToList();
        }

        // ── Default model (from enabled list) ────────────────────────────
        string defaultName;
        if (enabledNames.Count == 1)
        {
            defaultName = enabledNames[0];
        }
        else
        {
            defaultName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select the default model deployment:[/]")
                    .AddChoices(enabledNames));
        }

        // ── Voice classifier model (optional) ────────────────────────────
        // The classifier interprets speech and maps it to terminal commands.
        // A fast/cheap model (haiku, gpt-4o-mini) is ideal.
        _console.WriteLine();
        _console.MarkupLine("[dim]heypoul uses a fast model to classify voice commands (e.g. \"launch claude\" → pks claude).[/]");
        _console.MarkupLine("[dim]Speech transcription itself uses the Azure Speech API — no model deployment needed for that.[/]");

        string? classifierModel = null;
        if (enabledNames.Count > 0)
        {
            var classifierChoices = new[] { "(none — use simple text matching)" }.Concat(enabledNames).ToList();
            var currentClassifier = credentials.VoiceClassifierModel ?? "(none — use simple text matching)";

            var classifierPrompt = new SelectionPrompt<string>()
                .Title("[cyan]Voice classifier model:[/]")
                .AddChoices(classifierChoices);

            var classifierChoice = _console.Prompt(classifierPrompt);
            classifierModel = classifierChoice.StartsWith("(none") ? null : classifierChoice;
        }

        var foundryEndpoint = $"https://{selectedResource.Name}.services.ai.azure.com";

        // ── Fetch subscription key (for Speech REST API) ──────────────────
        var apiKey = await FetchSubscriptionKeyAsync(
            managementToken,
            selectedSubscription.SubscriptionId,
            resourceGroup,
            selectedResource.Name);

        if (string.IsNullOrEmpty(apiKey))
        {
            // ARM call failed (permissions or network) — prompt the user.
            _console.WriteLine();
            _console.MarkupLine("[yellow]Could not auto-fetch the subscription key. Enter it manually:[/]");
            _console.MarkupLine($"[dim]Azure portal → {Markup.Escape(selectedResource.Name)} → Keys and Endpoint → KEY 1[/]");
            var entered = _console.Ask<string>("[dim]Subscription key (blank to skip):[/]").Trim();
            if (!string.IsNullOrEmpty(entered)) apiKey = entered;
        }
        else
        {
            _console.MarkupLine($"[dim]  subscription key: {apiKey[..Math.Min(8, apiKey.Length)]}…[/]");
        }

        // ── Persist ───────────────────────────────────────────────────────
        credentials.SelectedSubscriptionId = selectedSubscription.SubscriptionId;
        credentials.SelectedSubscriptionName = selectedSubscription.DisplayName;
        credentials.SelectedResourceEndpoint = foundryEndpoint;
        credentials.SelectedResourceName = selectedResource.Name;
        credentials.SelectedResourceGroup = resourceGroup;
        credentials.DefaultModel = defaultName;
        credentials.EnabledModels = enabledNames;
        credentials.VoiceClassifierModel = classifierModel;
        credentials.LastRefreshedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(apiKey))
            credentials.ApiKey = apiKey;

        await _authService.StoreCredentialsAsync(credentials);

        _console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold green]Resource Updated[/]");

        table.AddColumn("[bold]Property[/]");
        table.AddColumn("[bold]Value[/]");
        table.AddRow("Subscription", Markup.Escape(selectedSubscription.DisplayName));
        table.AddRow("Resource", Markup.Escape(selectedResource.Name));
        table.AddRow("Endpoint", Markup.Escape(foundryEndpoint));
        table.AddRow("Enabled models", Markup.Escape(string.Join(", ", enabledNames)));
        table.AddRow("Default model", Markup.Escape(defaultName));
        table.AddRow("Voice classifier", Markup.Escape(classifierModel ?? "(simple matching)"));
        table.AddRow("Resource group", Markup.Escape(resourceGroup));
        table.AddRow("Subscription key", apiKey != null ? apiKey[..Math.Min(8, apiKey.Length)] + "…" : "(not set)");

        _console.Write(table);
        return 0;
    }

    private static async Task<string?> FetchSubscriptionKeyAsync(
        string managementToken, string subscriptionId, string resourceGroup, string resourceName)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managementToken);
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
                      $"/providers/Microsoft.CognitiveServices/accounts/{resourceName}/listKeys?api-version=2023-05-01";
            var resp = await http.PostAsync(url, null);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("key1", out var k) ? k.GetString() : null;
        }
        catch
        {
            return null;
        }
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
