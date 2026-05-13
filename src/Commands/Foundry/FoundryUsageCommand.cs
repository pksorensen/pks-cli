using System.ComponentModel;
using System.Globalization;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Foundry;

/// <summary>
/// Show cost breakdown scoped to a single Azure AI Foundry resource.
/// </summary>
[Description("Show cost breakdown for the selected Foundry resource")]
public class FoundryUsageCommand : Command<FoundrySettings>
{
    private readonly IAzureFoundryAuthService _authService;
    private readonly AzureFoundryAuthConfig _config;
    private readonly IAzureBillingService _billingService;
    private readonly IAnsiConsole _console;

    public FoundryUsageCommand(
        IAzureFoundryAuthService authService,
        AzureFoundryAuthConfig config,
        IAzureBillingService billingService,
        IAnsiConsole console)
    {
        _authService = authService;
        _config = config;
        _billingService = billingService;
        _console = console;
    }

    public override int Execute(CommandContext context, FoundrySettings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync()
    {
        var credentials = await _authService.GetStoredCredentialsAsync();
        if (credentials == null || string.IsNullOrEmpty(credentials.RefreshToken))
        {
            _console.MarkupLine("[red]Not authenticated with Azure AI Foundry.[/]");
            _console.MarkupLine("[dim]Run [bold]pks foundry init[/] to authenticate first.[/]");
            return 1;
        }

        var token = await _authService.GetAccessTokenAsync(_config.ManagementScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain management token. Try [bold]pks foundry init --force[/].[/]");
            return 1;
        }

        string subId, subName, rg, resourceName;
        var hasSaved = !string.IsNullOrEmpty(credentials.SelectedSubscriptionId)
                       && !string.IsNullOrEmpty(credentials.SelectedResourceName)
                       && !string.IsNullOrEmpty(credentials.SelectedResourceGroup);

        var reuseSaved = false;
        if (hasSaved)
        {
            _console.MarkupLine($"[dim]Saved Foundry resource: [bold]{Markup.Escape(credentials.SelectedResourceName)}[/] in [bold]{Markup.Escape(credentials.SelectedSubscriptionName)}[/][/]");
            reuseSaved = _console.Prompt(new ConfirmationPrompt("Use this resource?") { DefaultValue = true });
        }

        if (reuseSaved)
        {
            subId = credentials.SelectedSubscriptionId;
            subName = credentials.SelectedSubscriptionName;
            rg = credentials.SelectedResourceGroup;
            resourceName = credentials.SelectedResourceName;
        }
        else
        {
            var subs = await _authService.ListSubscriptionsAsync(token);
            if (subs.Count == 0)
            {
                _console.MarkupLine("[red]No Azure subscriptions found.[/]");
                return 1;
            }

            AzureSubscription sub;
            if (subs.Count == 1)
            {
                sub = subs[0];
                _console.MarkupLine($"[dim]Using subscription: [bold]{Markup.Escape(sub.DisplayName)}[/][/]");
            }
            else
            {
                var name = _console.Prompt(new SelectionPrompt<string>()
                    .Title("[cyan]Select an Azure subscription:[/]")
                    .AddChoices(subs.Select(s => s.DisplayName)));
                sub = subs.First(s => s.DisplayName == name);
            }

            var resources = await _authService.ListFoundryResourcesAsync(token, sub.SubscriptionId);
            if (resources.Count == 0)
            {
                _console.MarkupLine("[red]No Foundry resources found in this subscription.[/]");
                return 1;
            }

            CognitiveServicesAccount res;
            if (resources.Count == 1)
            {
                res = resources[0];
                _console.MarkupLine($"[dim]Using resource: [bold]{Markup.Escape(res.Name)}[/][/]");
            }
            else
            {
                var label = _console.Prompt(new SelectionPrompt<string>()
                    .Title("[cyan]Select a Foundry resource:[/]")
                    .AddChoices(resources.Select(r => $"{r.Name} ({ParseResourceGroup(r.Id)})")));
                res = resources.First(r => label.StartsWith(r.Name));
            }

            subId = sub.SubscriptionId;
            subName = sub.DisplayName;
            resourceName = res.Name;
            rg = ParseResourceGroup(res.Id);
        }

        var (start, end, windowLabel) = TimeWindow.Prompt(_console);
        _console.MarkupLine($"[dim]Window: [bold]{Markup.Escape(windowLabel)}[/] ({start:yyyy-MM-dd} → {end:yyyy-MM-dd})[/]");
        _console.WriteLine();

        var scope = $"/subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{resourceName}";
        CostQueryResult summary;
        CostQueryResult byMeter;
        try
        {
            summary = await _billingService.QueryCostAsync(token, scope, start, end, CostGrouping.None);
            byMeter = await _billingService.QueryCostAsync(token, scope, start, end, CostGrouping.Meter);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to query cost: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold cyan]Cost summary[/] [dim]({Markup.Escape(resourceName)})[/]");
        summaryTable.AddColumn("[bold]Metric[/]");
        summaryTable.AddColumn("[bold]Value[/]");
        summaryTable.AddRow("Subscription", Markup.Escape(subName));
        summaryTable.AddRow("Resource group", Markup.Escape(rg));
        summaryTable.AddRow("Resource", Markup.Escape(resourceName));
        summaryTable.AddRow("Period", $"{start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
        summaryTable.AddRow("Total cost", $"{summary.TotalCost.ToString("N2", CultureInfo.InvariantCulture)} {Markup.Escape(summary.Currency)}");
        _console.Write(summaryTable);
        _console.WriteLine();

        var meterTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Cost by meter[/]");
        meterTable.AddColumn("[bold]Meter[/]");
        meterTable.AddColumn("[bold]Cost[/]");
        var top = byMeter.Rows.OrderByDescending(r => r.Cost).Take(15).ToList();
        if (top.Count == 0)
            meterTable.AddRow("[dim](no usage in this window)[/]", "0.00");
        else
            foreach (var row in top)
                meterTable.AddRow(Markup.Escape(row.Group), row.Cost.ToString("N2", CultureInfo.InvariantCulture));
        _console.Write(meterTable);

        return 0;
    }

    private static string ParseResourceGroup(string resourceId)
    {
        var parts = resourceId.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return string.Empty;
    }
}
