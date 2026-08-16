using System.ComponentModel;
using PKS.Infrastructure.Services.Entra;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Entra;

/// <summary>
/// What pks holds, and — with <c>--directory</c> — what the tenant holds.
///
/// The local list is the useful one: it is the answer to "which alias do I bind?" and "when does that
/// secret expire?", which is the question nobody asks until an app stops signing in on a Tuesday. The
/// remote list exists so adopting an existing registration does not require the portal.
/// </summary>
[Description("List app registrations pks holds, or the tenant's")]
public sealed class EntraAppListCommand : AsyncCommand<EntraAppListCommand.Settings>
{
    private readonly IEntraApplicationService _entra;
    private readonly IAnsiConsole _console;

    public EntraAppListCommand(IEntraApplicationService entra, IAnsiConsole console)
    {
        _entra = entra;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--directory")]
        [Description("List registrations in the tenant instead of the ones pks holds")]
        public bool Directory { get; set; }

        [CommandArgument(0, "[PREFIX]")]
        [Description("With --directory: only names starting with this")]
        public string? Prefix { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (settings.Directory)
        {
            return await ListDirectoryAsync(settings.Prefix);
        }

        var stored = await _entra.ListStoredAsync();
        if (stored.Count == 0)
        {
            _console.MarkupLine("[dim]no app registrations stored.[/]");
            _console.MarkupLine("[dim]create one with [bold]pks entra app init \"My App\"[/].[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("alias");
        table.AddColumn("display name");
        table.AddColumn("client id");
        table.AddColumn("secret");

        foreach (var app in stored)
        {
            // Presence and expiry, never the value. The store can prove a secret exists; that is the
            // whole contract.
            var secret = !app.ClientSecret.HasValue
                ? "[red]missing[/]"
                // A hand-entered one has no end date, and printing 0001-01-01 in a warning colour says
                // something false about a credential that is probably fine.
                : app.SecretExpiresOn == default
                    ? "[dim]stored, expiry unknown[/]"
                : app.IsExpired
                    ? $"[red]expired {app.SecretExpiresOn:yyyy-MM-dd}[/]"
                    : app.SecretExpiresOn - DateTimeOffset.UtcNow < TimeSpan.FromDays(30)
                        ? $"[yellow]expires {app.SecretExpiresOn:yyyy-MM-dd}[/]"
                        : $"[green]expires {app.SecretExpiresOn:yyyy-MM-dd}[/]";

            table.AddRow(
                $"[bold]{app.Alias.EscapeMarkup()}[/]",
                app.DisplayName.EscapeMarkup(),
                app.AppId.EscapeMarkup(),
                secret);
        }

        _console.Write(table);
        return 0;
    }

    private async Task<int> ListDirectoryAsync(string? prefix)
    {
        if (!await _entra.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]not signed in to Azure — run [bold]pks foundry init[/].[/]");
            return 1;
        }

        try
        {
            var apps = await _entra.ListDirectoryAsync(prefix);
            if (apps.Count == 0)
            {
                _console.MarkupLine("[dim]no app registrations found.[/]");
                return 0;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("display name");
            table.AddColumn("client id");
            table.AddColumn("audience");

            foreach (var app in apps.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                table.AddRow(
                    app.DisplayName.EscapeMarkup(),
                    app.AppId.EscapeMarkup(),
                    app.SignInAudience.EscapeMarkup());
            }

            _console.Write(table);
            _console.MarkupLine("[dim]adopt one with [bold]pks entra app init <NAME> --adopt <client id>[/].[/]");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }
    }
}
