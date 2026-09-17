using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Services.Azure;

/// <summary>
/// What the user asked of <c>pks loganalytics|appinsights|fileshare init</c>, already parsed.
/// </summary>
public sealed class AzureResourceInitOptions
{
    /// <summary>Open the browser even though a tenant is known (<c>--reauth</c>).</summary>
    public bool Reauth { get; set; }

    /// <summary>Tenant id or email address to limit auth and discovery to (<c>--tenant</c>).</summary>
    public string? Tenant { get; set; }

    /// <summary>Subscription id or display name to limit discovery to (<c>--subscription</c>).</summary>
    public string? Subscription { get; set; }

    /// <summary>Names or keys to enable without discovery or prompt (<c>--enable</c>).</summary>
    public List<string> Enable { get; set; } = new();

    /// <summary>Names or keys to disable without discovery or prompt (<c>--disable</c>).</summary>
    public List<string> Disable { get; set; } = new();

    /// <summary>Print the registry entries of the kind and stop (<c>--list</c>).</summary>
    public bool List { get; set; }

    /// <summary>Names or keys to enable after discovery, instead of the checkbox prompt
    /// (<c>loganalytics init --workspace &lt;name&gt;</c>).</summary>
    public List<string> EnableAfterDiscovery { get; set; } = new();
}

/// <summary>
/// The one init flow shared by the three Azure resource verticals: sign in only when needed,
/// discover every resource of the kind across the known tenants and subscriptions, record them in
/// the <see cref="IAzureResourceRegistry"/>, and let the user tick the ones to enable.
/// </summary>
public interface IAzureResourceInitFlow
{
    Task<int> RunAsync(AzureResourceKind kind, AzureResourceInitOptions options, IAnsiConsole console, CancellationToken ct = default);
}

public sealed class AzureResourceInitFlow : IAzureResourceInitFlow
{
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAzureArmDiscovery _discovery;
    private readonly IAzureResourceRegistry _registry;

    public AzureResourceInitFlow(IAzureTenantCredentialStore tenants, IAzureArmDiscovery discovery, IAzureResourceRegistry registry)
    {
        _tenants = tenants;
        _discovery = discovery;
        _registry = registry;
    }

    public async Task<int> RunAsync(AzureResourceKind kind, AzureResourceInitOptions options, IAnsiConsole console, CancellationToken ct = default)
    {
        var label = Label(kind);
        var command = CommandName(kind);

        // ── --list needs neither a token nor a browser ──────────────────────────
        if (options.List)
        {
            var entries = await _registry.ListAsync(kind);
            if (entries.Count == 0)
            {
                console.MarkupLine($"[yellow]No {label}s registered.[/] Run [cyan]pks {command} init[/] to discover some.");
                return 0;
            }
            console.Write(await AllTable(kind, entries));
            return 0;
        }

        // ── 1. Ensure auth ───────────────────────────────────────────────────────
        // --enable/--disable only flip flags in the registry, so they open a browser only when
        // the user asked for one with --reauth or named a tenant that is not signed in yet.
        var flagsOnly = options.Enable.Count > 0 || options.Disable.Count > 0;
        var auth = flagsOnly && !options.Reauth && string.IsNullOrWhiteSpace(options.Tenant)
            ? new AuthOutcome(true, null)
            : await EnsureAuthAsync(options, console, ct);
        if (!auth.Ok) return 1;

        // ── 2. --enable / --disable: no discovery, no prompt ─────────────────────
        if (flagsOnly)
        {
            foreach (var name in options.Enable)
                if (!await _registry.SetEnabledAsync(kind, name, true))
                    return await UnknownEntryAsync(kind, name, console);
            foreach (var name in options.Disable)
                if (!await _registry.SetEnabledAsync(kind, name, false))
                    return await UnknownEntryAsync(kind, name, console);

            await PrintEnabledAsync(kind, console);
            return 0;
        }

        // ── 3. Discover ──────────────────────────────────────────────────────────
        var discovery = await DiscoverAsync(kind, options, auth.TenantFilter, console, ct);
        if (discovery.Entries.Count > 0)
            await _registry.UpsertAsync(discovery.Entries);

        var all = await _registry.ListAsync(kind);
        var discoveredIds = new HashSet<string>(discovery.Entries.Select(Identity), StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<AzureResourceEntry>(ReferenceEqualityComparer.Instance);
        foreach (var entry in all)
        {
            if (entry.TenantId == null) continue;                                    // registered by hand, never discovered
            if (!discovery.ScannedTenants.Contains(entry.TenantId)) continue;        // tenant skipped this run
            if (entry.SubscriptionId != null && discovery.FailedSubscriptions.Contains(entry.SubscriptionId)) continue;
            if (options.Subscription != null && !SubscriptionMatches(entry, options.Subscription)) continue;
            if (!discoveredIds.Contains(Identity(entry))) missing.Add(entry);
        }

        if (options.EnableAfterDiscovery.Count > 0)
        {
            foreach (var name in options.EnableAfterDiscovery)
                if (!await _registry.SetEnabledAsync(kind, name, true))
                    return await UnknownEntryAsync(kind, name, console);
            await PrintEnabledAsync(kind, console);
            return 0;
        }

        if (all.Count == 0)
        {
            console.MarkupLine($"[yellow]No {label}s found in the tenants and subscriptions you have access to.[/]");
            return 0;
        }

        // ── 4. Choose ────────────────────────────────────────────────────────────
        if (!console.Profile.Capabilities.Interactive)
        {
            console.MarkupLine($"[dim]Non-interactive console: use [cyan]pks {command} init --enable <name>[/] / [cyan]--disable <name>[/] to change the selection, [cyan]--list[/] to see every {label}.[/]");
            await PrintEnabledAsync(kind, console);
            return 0;
        }

        var tenantNames = await TenantNamesAsync();
        var headers = new HashSet<AzureResourceEntry>(ReferenceEqualityComparer.Instance);
        var prompt = new MultiSelectionPrompt<AzureResourceEntry>(ReferenceEqualityComparer.Instance as IEqualityComparer<AzureResourceEntry>)
            .Title($"Select the {label}s to enable [dim](space toggles, enter saves)[/]")
            .NotRequired()
            .PageSize(15)
            .MoreChoicesText("[dim](move up and down to reveal more)[/]")
            .InstructionsText("[dim](press [blue]<space>[/] to toggle, [green]<enter>[/] to save)[/]")
            .UseConverter(e => headers.Contains(e)
                ? $"[bold]{e.Name.EscapeMarkup()}[/]"
                : $"{e.Name.EscapeMarkup()}  [dim]({SubscriptionLabel(e).EscapeMarkup()}, {e.Key.EscapeMarkup()})[/]{(missing.Contains(e) ? " [yellow](not found)[/]" : "")}");

        var groups = all
            .GroupBy(e => (Tenant: e.TenantId ?? "", Sub: e.SubscriptionId ?? ""))
            .ToList();

        if (groups.Count > 1)
        {
            foreach (var group in groups)
            {
                var first = group.First();
                var tenantLabel = first.TenantId == null ? "no tenant" : tenantNames.GetValueOrDefault(first.TenantId, first.TenantId);
                var header = new AzureResourceEntry { Kind = kind, Name = $"{tenantLabel} / {SubscriptionLabel(first)}", Key = $"group:{group.Key.Tenant}/{group.Key.Sub}" };
                headers.Add(header);
                prompt.AddChoiceGroup(header, group);
            }
        }
        else
        {
            prompt.AddChoices(all);
        }

        foreach (var entry in all.Where(e => e.Enabled))
            prompt.Select(entry);

        var selected = new HashSet<AzureResourceEntry>(await prompt.ShowAsync(console, ct), ReferenceEqualityComparer.Instance);
        selected.RemoveWhere(headers.Contains);

        // ── 5. Save ──────────────────────────────────────────────────────────────
        foreach (var entry in all)
        {
            var want = selected.Contains(entry);
            if (want != entry.Enabled)
                await _registry.SetEnabledAsync(kind, entry.Key, want);
        }

        await PrintEnabledAsync(kind, console);
        return 0;
    }

    // ── auth ─────────────────────────────────────────────────────────────────────

    private readonly record struct AuthOutcome(bool Ok, string? TenantFilter);

    private async Task<AuthOutcome> EnsureAuthAsync(AzureResourceInitOptions options, IAnsiConsole console, CancellationToken ct)
    {
        var tenantOption = string.IsNullOrWhiteSpace(options.Tenant) ? null : options.Tenant.Trim();

        if (options.Reauth)
        {
            if (tenantOption != null)
            {
                var info = await LoginAsync(tenantOption, console, ct);
                return info == null ? new(false, null) : new(true, info.TenantId);
            }

            var known = await _tenants.ListTenantsAsync();
            if (known.Count == 0)
            {
                var info = await LoginAsync(null, console, ct);
                return new(info != null, null);
            }

            foreach (var tenant in known)
            {
                console.MarkupLine($"[cyan]Signing in to tenant {Describe(tenant).EscapeMarkup()}...[/]");
                if (await LoginAsync(tenant.TenantId, console, ct) == null) return new(false, null);
            }
            return new(true, null);
        }

        if (tenantOption != null)
        {
            if (tenantOption.Contains('@') || !await _tenants.HasTenantAsync(tenantOption))
            {
                var info = await LoginAsync(tenantOption, console, ct);
                return info == null ? new(false, null) : new(true, info.TenantId);
            }
            return new(true, tenantOption);
        }

        if ((await _tenants.ListTenantsAsync()).Count == 0)
        {
            console.MarkupLine("[cyan]No Azure sign-in yet. Opening the browser...[/]");
            var info = await LoginAsync(null, console, ct);
            return new(info != null, null);
        }

        return new(true, null);
    }

    private async Task<AzureTenantInfo?> LoginAsync(string? tenantIdOrEmail, IAnsiConsole console, CancellationToken ct)
    {
        try
        {
            return await _tenants.LoginAsync(tenantIdOrEmail, console, ct);
        }
        catch (OperationCanceledException)
        {
            console.MarkupLine("[red]Authentication timed out.[/]");
            return null;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Authentication failed:[/] {ex.Message.EscapeMarkup()}");
            return null;
        }
    }

    // ── discovery ────────────────────────────────────────────────────────────────

    private sealed class DiscoveryResult
    {
        public List<AzureResourceEntry> Entries { get; } = new();
        public HashSet<string> ScannedTenants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailedSubscriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<DiscoveryResult> DiscoverAsync(AzureResourceKind kind, AzureResourceInitOptions options, string? tenantFilter, IAnsiConsole console, CancellationToken ct)
    {
        var result = new DiscoveryResult();
        var label = Label(kind);
        var known = await _tenants.ListTenantsAsync();
        var tenantIds = tenantFilter != null
            ? new List<string> { tenantFilter }
            : known.Select(t => t.TenantId).ToList();
        var names = known.ToDictionary(t => t.TenantId, Describe, StringComparer.OrdinalIgnoreCase);

        foreach (var tenantId in tenantIds)
        {
            var display = names.GetValueOrDefault(tenantId, tenantId);
            var retried = false;
            while (true)
            {
                var warnings = new List<string>();
                try
                {
                    await console.Status().StartAsync($"Discovering {label}s in {display.EscapeMarkup()}...", async _ =>
                    {
                        await DiscoverTenantAsync(kind, tenantId, options.Subscription, result, warnings, ct);
                    });
                    foreach (var warning in warnings) console.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");
                    result.ScannedTenants.Add(tenantId);
                    break;
                }
                catch (AzureAuthExpiredException)
                {
                    foreach (var warning in warnings) console.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");
                    if (retried || !console.Profile.Capabilities.Interactive
                        || !console.Confirm($"Tenant [bold]{display.EscapeMarkup()}[/] needs sign-in — sign in now?"))
                    {
                        console.MarkupLine($"[yellow]Skipping tenant {display.EscapeMarkup()}: sign-in needed. Run [cyan]pks {CommandName(kind)} init --reauth {tenantId}[/] later.[/]");
                        break;
                    }
                    retried = true;
                    if (await LoginAsync(tenantId, console, ct) == null)
                    {
                        console.MarkupLine($"[yellow]Skipping tenant {display.EscapeMarkup()}.[/]");
                        break;
                    }
                }
            }
        }

        return result;
    }

    private async Task DiscoverTenantAsync(AzureResourceKind kind, string tenantId, string? subscriptionFilter, DiscoveryResult result, List<string> warnings, CancellationToken ct)
    {
        var subscriptions = await _discovery.ListSubscriptionsAsync(tenantId, ct);
        if (subscriptionFilter != null)
        {
            var wanted = subscriptionFilter.Trim();
            subscriptions = subscriptions.Where(s =>
                string.Equals(s.SubscriptionId, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        foreach (var subscription in subscriptions)
        {
            try
            {
                var entries = kind switch
                {
                    AzureResourceKind.LogAnalytics => (await _discovery.ListLogAnalyticsWorkspacesAsync(tenantId, subscription.SubscriptionId, ct))
                        .Select(w => Entry(kind, tenantId, subscription, w.Name, w.Properties.CustomerId, w.Id)),
                    AzureResourceKind.AppInsights => (await _discovery.ListAppInsightsResourcesAsync(tenantId, subscription.SubscriptionId, ct))
                        .Select(c => Entry(kind, tenantId, subscription, c.Name, c.Properties.AppId, c.Id)),
                    AzureResourceKind.Storage => (await _discovery.ListStorageAccountsAsync(tenantId, subscription.SubscriptionId, ct))
                        .Select(a => Entry(kind, tenantId, subscription, a.Name, a.Name, a.Id)),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
                };
                result.Entries.AddRange(entries.Where(e => !string.IsNullOrWhiteSpace(e.Key)));
            }
            catch (AzureAuthExpiredException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.FailedSubscriptions.Add(subscription.SubscriptionId);
                warnings.Add($"Subscription {subscription.DisplayName} ({subscription.SubscriptionId}) failed: {ex.Message}");
            }
        }
    }

    private static AzureResourceEntry Entry(AzureResourceKind kind, string tenantId, AzureSubscription subscription, string name, string key, string resourceId) => new()
    {
        Kind = kind,
        TenantId = tenantId,
        SubscriptionId = subscription.SubscriptionId,
        SubscriptionName = string.IsNullOrWhiteSpace(subscription.DisplayName) ? null : subscription.DisplayName,
        Name = name,
        Key = key,
        ResourceId = string.IsNullOrWhiteSpace(resourceId) ? null : resourceId,
        ResourceGroup = ParseResourceGroup(resourceId),
        Enabled = false,
        DiscoveredAt = DateTime.UtcNow,
    };

    /// <summary>The resource group segment of an ARM resource id, or null when there is none.</summary>
    public static string? ParseResourceGroup(string? resourceId)
    {
        if (string.IsNullOrEmpty(resourceId)) return null;
        var parts = resourceId.Split('/');
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        return null;
    }

    // ── output ───────────────────────────────────────────────────────────────────

    private async Task<int> UnknownEntryAsync(AzureResourceKind kind, string name, IAnsiConsole console)
    {
        var known = await _registry.ListAsync(kind);
        console.MarkupLine($"[red]Unknown {Label(kind)}:[/] {name.EscapeMarkup()}");
        console.MarkupLine(known.Count == 0
            ? $"[dim]Nothing registered yet. Run [cyan]pks {CommandName(kind)} init[/] to discover.[/]"
            : $"[dim]Known: {string.Join(", ", known.Select(e => e.Name)).EscapeMarkup()}[/]");
        return 1;
    }

    private async Task PrintEnabledAsync(AzureResourceKind kind, IAnsiConsole console)
    {
        var enabled = await _registry.ListEnabledAsync(kind);
        var label = Label(kind);
        if (enabled.Count == 0)
        {
            console.MarkupLine($"[yellow]No {label}s enabled.[/] [dim]Run [cyan]pks {CommandName(kind)} init[/] to pick some.[/]");
            return;
        }

        var tenantNames = await TenantNamesAsync();
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]Enabled {label}s[/]")
            .AddColumn("Name").AddColumn("Key").AddColumn("Subscription").AddColumn("Tenant");
        foreach (var entry in enabled)
            table.AddRow(entry.Name.EscapeMarkup(), entry.Key.EscapeMarkup(), SubscriptionLabel(entry).EscapeMarkup(), TenantLabel(entry, tenantNames).EscapeMarkup());
        console.Write(table);
    }

    private async Task<Table> AllTable(AzureResourceKind kind, IReadOnlyList<AzureResourceEntry> entries)
    {
        var tenantNames = await TenantNamesAsync();
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{Label(kind)}s[/]")
            .AddColumn("Enabled").AddColumn("Name").AddColumn("Key").AddColumn("Subscription").AddColumn("Tenant");
        foreach (var entry in entries)
            table.AddRow(entry.Enabled ? "[green]✓[/]" : "[dim]✗[/]", entry.Name.EscapeMarkup(), entry.Key.EscapeMarkup(),
                SubscriptionLabel(entry).EscapeMarkup(), TenantLabel(entry, tenantNames).EscapeMarkup());
        return table;
    }

    private async Task<Dictionary<string, string>> TenantNamesAsync()
    {
        try
        {
            return (await _tenants.ListTenantsAsync()).ToDictionary(t => t.TenantId, Describe, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Describe(AzureTenantInfo tenant)
        => string.IsNullOrWhiteSpace(tenant.TenantName) ? tenant.TenantId : $"{tenant.TenantName} ({tenant.TenantId})";

    private static string SubscriptionLabel(AzureResourceEntry entry)
        => entry.SubscriptionName ?? entry.SubscriptionId ?? "no subscription";

    private static string TenantLabel(AzureResourceEntry entry, Dictionary<string, string> tenantNames)
        => entry.TenantId == null ? "-" : tenantNames.GetValueOrDefault(entry.TenantId, entry.TenantId);

    private static bool SubscriptionMatches(AzureResourceEntry entry, string filter)
        => string.Equals(entry.SubscriptionId, filter.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(entry.SubscriptionName, filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Identity(AzureResourceEntry entry)
        => !string.IsNullOrEmpty(entry.ResourceId) ? "id:" + entry.ResourceId : "key:" + entry.Key;

    public static string Label(AzureResourceKind kind) => kind switch
    {
        AzureResourceKind.LogAnalytics => "Log Analytics workspace",
        AzureResourceKind.AppInsights => "Application Insights resource",
        AzureResourceKind.Storage => "storage account",
        _ => kind.ToString(),
    };

    public static string CommandName(AzureResourceKind kind) => kind switch
    {
        AzureResourceKind.LogAnalytics => "loganalytics",
        AzureResourceKind.AppInsights => "appinsights",
        AzureResourceKind.Storage => "fileshare",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
