using System.ComponentModel;
using PKS.Infrastructure.Services.Entra;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Entra;

/// <summary>
/// Drops the local copy of an alias — the client id, the tenant and the stored secret.
///
/// Deliberately local only. Deleting the registration itself is a different act with a different blast
/// radius (anything still signing in with it stops, and the audit trail in the directory is somebody
/// else's), so this says what it left behind rather than reaching into the tenant.
/// </summary>
[Description("Forget a stored app registration (the directory is untouched)")]
public sealed class EntraAppForgetCommand : AsyncCommand<EntraAppForgetCommand.Settings>
{
    private readonly IEntraApplicationService _entra;
    private readonly IAnsiConsole _console;

    public EntraAppForgetCommand(IEntraApplicationService entra, IAnsiConsole console)
    {
        _entra = entra;
        _console = console;
    }

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<ALIAS>")]
        [Description("The alias to forget")]
        public string Alias { get; set; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var stored = await _entra.GetStoredAsync(settings.Alias);
        if (!await _entra.ForgetAsync(settings.Alias))
        {
            _console.MarkupLine($"[yellow]no stored app registration called {settings.Alias.EscapeMarkup()}.[/]");
            return 1;
        }

        _console.MarkupLine($"[green]forgot[/] {settings.Alias.EscapeMarkup()}");
        if (stored is not null && !string.IsNullOrEmpty(stored.AppId))
        {
            _console.MarkupLine($"[dim]the registration {stored.AppId.EscapeMarkup()} still exists in the tenant, with the credential pks minted still on it.[/]");
        }
        return 0;
    }
}
