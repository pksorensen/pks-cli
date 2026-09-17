using PKS.Infrastructure.Services.Azure;
using Spectre.Console;

namespace PKS.Commands.Azure;

/// <summary>
/// The two sections <c>loganalytics status</c>, <c>appinsights status</c> and <c>fileshare status</c>
/// share: the table of registered entries and the sign-in state of every known tenant.
/// </summary>
public static class AzureResourceStatusRenderer
{
    /// <summary>Prints the entries table. Returns false (after a hint) when there are none.</summary>
    public static async Task<bool> WriteEntriesAsync(IAnsiConsole console, IAzureTenantCredentialStore tenants, AzureResourceKind kind, IReadOnlyList<AzureResourceEntry> entries)
    {
        var label = AzureResourceInitFlow.Label(kind);
        var command = AzureResourceInitFlow.CommandName(kind);
        if (entries.Count == 0)
        {
            console.MarkupLine($"[yellow]{Capitalize(label)}s are not configured.[/]");
            console.MarkupLine($"[dim]Run [cyan]pks {command} init[/] to discover and enable some.[/]");
            return false;
        }

        var names = await TenantNamesAsync(tenants);
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]{Capitalize(label)}s[/]")
            .AddColumn("Enabled").AddColumn("Name").AddColumn("Key").AddColumn("Subscription").AddColumn("Tenant");
        foreach (var entry in entries)
        {
            table.AddRow(
                entry.Enabled ? "[green]✓[/]" : "[dim]✗[/]",
                entry.Name.EscapeMarkup(),
                entry.Key.EscapeMarkup(),
                (entry.SubscriptionName ?? entry.SubscriptionId ?? "-").EscapeMarkup(),
                (entry.TenantId == null ? "-" : names.GetValueOrDefault(entry.TenantId, entry.TenantId)).EscapeMarkup());
        }
        console.Write(table);
        return true;
    }

    /// <summary>Prints one line per known tenant: signed in, or how to sign in again. The probe's
    /// result is discarded; nothing about it is ever printed.</summary>
    public static async Task WriteTenantsAsync(IAnsiConsole console, IAzureTenantCredentialStore tenants, AzureResourceKind kind, CancellationToken ct = default)
    {
        var command = AzureResourceInitFlow.CommandName(kind);
        IReadOnlyList<AzureTenantInfo> known;
        try
        {
            known = await tenants.ListTenantsAsync();
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[yellow]Could not read the Azure sign-ins:[/] {ex.Message.EscapeMarkup()}");
            return;
        }

        console.WriteLine();
        if (known.Count == 0)
        {
            console.MarkupLine($"[yellow]No Azure sign-in.[/] [dim]Run [cyan]pks {command} init[/] to sign in.[/]");
            return;
        }

        console.MarkupLine("[bold]Tenants[/]");
        foreach (var tenant in known)
        {
            var display = string.IsNullOrWhiteSpace(tenant.TenantName) ? tenant.TenantId : $"{tenant.TenantName} ({tenant.TenantId})";
            string state;
            try
            {
                _ = await tenants.GetAccessTokenAsync(tenant.TenantId, AzureArmDiscovery.ManagementScope, ct);
                state = "[green]signed in[/]";
            }
            catch (AzureAuthExpiredException)
            {
                state = $"[red]sign-in needed[/] [dim](run [cyan]pks {command} init --reauth {tenant.TenantId}[/])[/]";
            }
            catch (Exception ex)
            {
                state = $"[yellow]could not verify[/] [dim]({ex.Message.EscapeMarkup()})[/]";
            }
            console.MarkupLine($"  {display.EscapeMarkup()}: {state}");
        }
    }

    private static async Task<Dictionary<string, string>> TenantNamesAsync(IAzureTenantCredentialStore tenants)
    {
        try
        {
            return (await tenants.ListTenantsAsync()).ToDictionary(
                t => t.TenantId,
                t => string.IsNullOrWhiteSpace(t.TenantName) ? t.TenantId : t.TenantName!,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string Capitalize(string value)
        => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
