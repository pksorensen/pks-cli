using System.ComponentModel;
using System.Globalization;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Azure;

/// <summary>
/// Show Azure cost and sponsorship credit balance for a subscription.
/// </summary>
[Description("Show Azure cost and sponsorship credit balance for a subscription")]
public class AzureUsageCommand : Command<AzureUsageSettings>
{
    private readonly IAzureAuthService _authService;
    private readonly IAzureBillingService _billingService;
    private readonly IAnsiConsole _console;

    public AzureUsageCommand(IAzureAuthService authService, IAzureBillingService billingService, IAnsiConsole console)
    {
        _authService = authService;
        _billingService = billingService;
        _console = console;
    }

    public override int Execute(CommandContext context, AzureUsageSettings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync()
    {
        if (!await _authService.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]Not authenticated with Azure.[/]");
            _console.MarkupLine("[dim]Run [bold]pks azure init[/] to authenticate first.[/]");
            return 1;
        }

        var token = await _authService.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain management token. Try [bold]pks azure init --force[/].[/]");
            return 1;
        }

        var subscriptions = await _authService.ListSubscriptionsAsync(token);
        if (subscriptions.Count == 0)
        {
            _console.MarkupLine("[red]No Azure subscriptions found.[/]");
            return 1;
        }

        AzureSubscription sub;
        if (subscriptions.Count == 1)
        {
            sub = subscriptions[0];
            _console.MarkupLine($"[dim]Using subscription: [bold]{Markup.Escape(sub.DisplayName)}[/][/]");
        }
        else
        {
            var name = _console.Prompt(new SelectionPrompt<string>()
                .Title("[cyan]Select an Azure subscription:[/]")
                .AddChoices(subscriptions.Select(s => s.DisplayName)));
            sub = subscriptions.First(s => s.DisplayName == name);
        }

        var (start, end, windowLabel) = TimeWindow.Prompt(_console);
        _console.MarkupLine($"[dim]Window: [bold]{Markup.Escape(windowLabel)}[/] ({start:yyyy-MM-dd} → {end:yyyy-MM-dd})[/]");
        _console.WriteLine();

        // ── Sponsorship credit balance (billing-profile scoped) ──────────────
        try
        {
            var profiles = await _billingService.ListBillingProfilesAsync(token);
            if (profiles.Count == 0)
            {
                _console.MarkupLine("[dim]No billing profiles visible to this identity — skipping credit balance.[/]");
                _console.MarkupLine("[dim](Legacy Azure Sponsorship subscriptions only show balance at [link]https://www.microsoftazuresponsorships.com/balance[/].)[/]");
                _console.WriteLine();
            }
            else
            {
                var anyLots = false;
                foreach (var profile in profiles)
                {
                    var lots = await _billingService.GetCreditLotsAsync(token, profile.BillingAccountId, profile.BillingProfileId);
                    if (lots.Count == 0) continue;
                    anyLots = true;
                    RenderCreditTable(_console, profile, lots);
                    _console.WriteLine();
                }
                if (!anyLots)
                {
                    _console.MarkupLine("[dim]No active credit lots on any visible billing profile.[/]");
                    _console.WriteLine();
                }
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Could not fetch credit balance: {Markup.Escape(ex.Message)}[/]");
            _console.WriteLine();
        }

        // ── Cost (subscription scope) ─────────────────────────────────────────
        var scope = $"/subscriptions/{sub.SubscriptionId}";
        CostQueryResult summary;
        CostQueryResult byMeter;
        DailyCostResult daily;
        try
        {
            summary = await _billingService.QueryCostAsync(token, scope, start, end, CostGrouping.None);
            daily = await _billingService.QueryDailyCostAsync(token, scope, start, end);
            byMeter = await _billingService.QueryCostAsync(token, scope, start, end, CostGrouping.Meter);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Failed to query cost: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold cyan]Cost summary[/] [dim]({Markup.Escape(sub.DisplayName)})[/]");
        summaryTable.AddColumn("[bold]Metric[/]");
        summaryTable.AddColumn("[bold]Value[/]");
        summaryTable.AddRow("Subscription", Markup.Escape(sub.DisplayName));
        summaryTable.AddRow("Period", $"{start:yyyy-MM-dd} → {end:yyyy-MM-dd}");
        summaryTable.AddRow("Total cost", $"{summary.TotalCost.ToString("N2", CultureInfo.InvariantCulture)} {Markup.Escape(summary.Currency)}");
        _console.Write(summaryTable);
        _console.WriteLine();

        CostChart.Render(_console, daily.Points,
            !string.IsNullOrEmpty(daily.Currency) ? daily.Currency : summary.Currency);
        _console.WriteLine();

        var meterTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Cost by meter[/]");
        meterTable.AddColumn("[bold]Meter[/]");
        meterTable.AddColumn("[bold]Cost[/]");
        var top = byMeter.Rows.OrderByDescending(r => r.Cost).Take(15).ToList();
        if (top.Count == 0)
        {
            meterTable.AddRow("[dim](no usage in this window)[/]", "0.00");
        }
        else
        {
            foreach (var row in top)
                meterTable.AddRow(Markup.Escape(row.Group), row.Cost.ToString("N2", CultureInfo.InvariantCulture));
        }
        _console.Write(meterTable);

        return 0;
    }

    private static void RenderCreditTable(IAnsiConsole console, BillingProfileRef profile, List<CreditLot> lots)
    {
        var creditTable = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold green]Sponsorship credits[/] [dim]({Markup.Escape(profile.DisplayName)})[/]");
        creditTable.AddColumn("[bold]Source[/]");
        creditTable.AddColumn("[bold]Original[/]");
        creditTable.AddColumn("[bold]Remaining[/]");
        creditTable.AddColumn("[bold]Currency[/]");
        creditTable.AddColumn("[bold]Expires[/]");

        foreach (var l in lots.OrderBy(x => x.ExpirationDate ?? DateTime.MaxValue))
        {
            creditTable.AddRow(
                Markup.Escape(string.IsNullOrEmpty(l.Source) ? "(unknown)" : l.Source),
                l.OriginalAmount.ToString("N2", CultureInfo.InvariantCulture),
                l.ClosedBalance.ToString("N2", CultureInfo.InvariantCulture),
                Markup.Escape(l.CreditCurrency),
                l.ExpirationDate?.ToString("yyyy-MM-dd") ?? "—");
        }

        var totalOriginal = lots.Sum(l => l.OriginalAmount);
        var totalRemaining = lots.Sum(l => l.ClosedBalance);
        creditTable.AddRow(
            "[bold]Total[/]",
            $"[bold]{totalOriginal.ToString("N2", CultureInfo.InvariantCulture)}[/]",
            $"[bold]{totalRemaining.ToString("N2", CultureInfo.InvariantCulture)}[/]",
            Markup.Escape(lots[0].CreditCurrency),
            string.Empty);

        console.Write(creditTable);
    }
}

/// <summary>
/// Shared time-window picker used by both `azure usage` and `foundry usage`.
/// </summary>
internal static class TimeWindow
{
    public static (DateTime start, DateTime end, string label) Prompt(IAnsiConsole console)
    {
        var now = DateTime.UtcNow;
        var thisMonthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonthStart = thisMonthStart.AddMonths(-1);
        var lastMonthEnd = thisMonthStart.AddTicks(-1);

        const string thisMonth = "This month";
        const string lastMonth = "Last month";
        const string last30 = "Last 30 days";
        const string last90 = "Last 90 days";
        const string custom = "Custom";

        var choice = console.Prompt(new SelectionPrompt<string>()
            .Title("[cyan]Select time window:[/]")
            .AddChoices(new[] { thisMonth, lastMonth, last30, last90, custom }));

        return choice switch
        {
            lastMonth => (lastMonthStart, lastMonthEnd, lastMonth),
            last30 => (now.AddDays(-30), now, last30),
            last90 => (now.AddDays(-90), now, last90),
            custom => CustomRange(console, now),
            _ => (thisMonthStart, now, thisMonth),
        };
    }

    private static (DateTime, DateTime, string) CustomRange(IAnsiConsole console, DateTime now)
    {
        var startInput = console.Prompt(new TextPrompt<string>("[cyan]Start date (yyyy-MM-dd):[/]")
            .DefaultValue(now.AddDays(-30).ToString("yyyy-MM-dd")));
        var endInput = console.Prompt(new TextPrompt<string>("[cyan]End date (yyyy-MM-dd):[/]")
            .DefaultValue(now.ToString("yyyy-MM-dd")));
        var start = DateTime.SpecifyKind(DateTime.Parse(startInput, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(DateTime.Parse(endInput, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        return (start, end, "Custom");
    }
}
