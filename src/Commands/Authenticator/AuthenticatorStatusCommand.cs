using System.ComponentModel;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Authenticator;

/// <summary>Show whether a second factor is enrolled and how many recovery codes remain. Never reveals the seed.</summary>
[Description("Show two-factor enrollment status")]
public class AuthenticatorStatusCommand : Command<AuthenticatorStatusCommand.Settings>
{
    private readonly ITotpSeedStore _store;
    private readonly IAnsiConsole _console;

    public AuthenticatorStatusCommand(ITotpSeedStore store, IAnsiConsole console)
    {
        _store = store;
        _console = console;
    }

    public class Settings : AuthenticatorSettings
    {
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var enrolled = await _store.IsEnrolledAsync();
        if (!enrolled)
        {
            _console.MarkupLine("[yellow]No authenticator enrolled.[/] Sensitive actions run without a second factor.");
            _console.MarkupLine("[dim]Enroll one with [bold]pks authenticator init[/].[/]");
            return 0;
        }

        var remaining = await _store.RecoveryCodesRemainingAsync();
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Cyan1)
            .AddColumn("[bold]Property[/]").AddColumn("[bold]Value[/]");
        table.AddRow("Second factor", "[green]TOTP enrolled[/]");
        table.AddRow("Recovery codes left", remaining.ToString());
        _console.Write(table);
        _console.MarkupLine("[dim]Re-enroll with [bold]pks authenticator init[/] (requires a current code).[/]");
        return 0;
    }
}
