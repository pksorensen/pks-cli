using PKS.Infrastructure.Services.Security;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

/// <summary>
/// Delete the stored TypeSafe key from this host. The allow-list is left alone: it names
/// repositories, not credential material, and survives a key rotation on purpose.
/// Usage: pks typesafe remove
/// </summary>
public class TypeSafeRemoveCommand : AsyncCommand<TypeSafeSettings>
{
    private readonly ISecretStore _secrets;
    private readonly IAnsiConsole _console;

    public TypeSafeRemoveCommand(ISecretStore secrets, IAnsiConsole console)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TypeSafeSettings settings)
    {
        var removed = await _secrets.DeleteAsync(TypeSafeCredentialService.ApiKeyKey);
        if (!removed)
        {
            _console.MarkupLine("[yellow]No TypeSafe API key was stored.[/]");
            return 0;
        }

        _console.MarkupLine("[green]TypeSafe API key removed from this host.[/]");
        _console.MarkupLine("[dim]Revoke it at console.typesafe.ai as well — deleting the local copy does not.[/]");
        return 0;
    }
}
