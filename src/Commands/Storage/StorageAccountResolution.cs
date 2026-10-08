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
    /// <summary>
    /// Picks one of <paramref name="choices"/>: the only one, the user's pick when there is a
    /// terminal to ask in, or — piped or run by an agent — nothing, after naming what to pass and
    /// what exists. A selection prompt without a terminal only throws, and says neither.
    /// </summary>
    /// <param name="what">What is being chosen, e.g. "storage account".</param>
    /// <param name="howToChoose">How to choose without a prompt, e.g. "Pass --account &lt;name&gt;".</param>
    /// <returns>The choice, or <c>null</c> after printing why none was made — the caller returns 1.</returns>
    public static string? Choose(IAnsiConsole console, string what, IReadOnlyList<string> choices, string howToChoose)
    {
        if (choices.Count == 1)
            return choices[0];

        if (choices.Count == 0)
        {
            console.MarkupLine($"[red]No {Markup.Escape(what)} is available.[/]");
            return null;
        }

        if (console.Profile.Capabilities.Interactive)
            return console.Prompt(new SelectionPrompt<string>()
                .Title($"[cyan]Select a {Markup.Escape(what)}:[/]")
                .AddChoices(choices));

        console.MarkupLine($"[red]Several {Markup.Escape(what)}s are available and there is no terminal to choose in.[/]");
        console.MarkupLine($"[dim]{Markup.Escape(howToChoose)} — one of: {Markup.Escape(string.Join(", ", choices))}.[/]");
        return null;
    }

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
