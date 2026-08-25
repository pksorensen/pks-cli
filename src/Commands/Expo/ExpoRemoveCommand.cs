using PKS.Infrastructure.Services.Expo;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Expo;

/// <summary>
/// Delete the stored Expo token from this host.
/// Usage: pks expo remove
/// </summary>
public class ExpoRemoveCommand : AsyncCommand<ExpoSettings>
{
    private readonly ISecretStore _secrets;
    private readonly IAnsiConsole _console;

    public ExpoRemoveCommand(ISecretStore secrets, IAnsiConsole console)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ExpoSettings settings)
    {
        var removed = await _secrets.DeleteAsync(ExpoCredentialService.TokenKey);
        if (!removed)
        {
            _console.MarkupLine("[yellow]No Expo token was stored.[/]");
            return 0;
        }

        _console.MarkupLine("[green]Expo token removed from this host.[/]");
        _console.MarkupLine("[dim]Revoke it at expo.dev as well — deleting the local copy does not.[/]");
        return 0;
    }
}
