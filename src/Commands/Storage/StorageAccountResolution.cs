using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Commands.Storage;

/// <summary>
/// Shared by <c>ls</c>, <c>sync</c> and <c>rm</c>: with several storage accounts enabled, a bare
/// <c>--share</c> is only unambiguous when one account holds a share of that name. Otherwise the
/// user picks (interactive) or is told to pass <c>--account</c> (agent / piped).
/// </summary>
internal static class StorageAccountResolution
{
    /// <returns>The account holding <paramref name="shareName"/>, or <c>null</c> after printing why
    /// none could be chosen — the caller returns exit code 1.</returns>
    public static string? AccountForShare(IAnsiConsole console, IReadOnlyList<StorageResource> resources, string shareName)
    {
        var holders = resources
            .Where(r => string.Equals(r.ResourceName, shareName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.AccountName)
            .Distinct()
            .ToList();

        if (holders.Count == 1)
            return holders[0];

        if (holders.Count == 0)
        {
            // Not in the listing: with a single account there is nothing to disambiguate, so let the
            // provider report the share itself; with several, the user has to say which.
            var accounts = resources.Select(r => r.AccountName).Distinct().ToList();
            if (accounts.Count == 1)
                return accounts[0];

            console.MarkupLine($"[red]Share '[bold]{Markup.Escape(shareName)}[/]' was not found in any enabled storage account.[/]");
            if (accounts.Count > 1)
                console.MarkupLine($"[dim]Pass --account <name> to target one of: {Markup.Escape(string.Join(", ", accounts))}.[/]");
            return null;
        }

        if (console.Profile.Capabilities.Interactive)
        {
            return console.Prompt(new SelectionPrompt<string>()
                .Title($"[cyan]Share '{Markup.Escape(shareName)}' exists in several storage accounts — select one:[/]")
                .AddChoices(holders));
        }

        console.MarkupLine($"[red]Share '[bold]{Markup.Escape(shareName)}[/]' exists in several storage accounts: {Markup.Escape(string.Join(", ", holders))}.[/]");
        console.MarkupLine("[dim]Pass --account <name> to choose one.[/]");
        return null;
    }
}
