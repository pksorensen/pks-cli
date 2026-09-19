using System.ComponentModel;
using PKS.Infrastructure.Services.TypeSafe;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.TypeSafe;

/// <summary>
/// Grant or revoke a repository's access to the stored TypeSafe key.
/// Usage: pks typesafe allow owner/repo | pks typesafe revoke owner/repo
///
/// The list is keyed on the git repository a job's token was minted for — the same owner/name
/// the credential socket sees in the job's claims — so it covers both a GitHub Actions job on a
/// self-hosted runner and an assembly-line station whose project points at that repository.
/// </summary>
public class TypeSafeAllowCommand : AsyncCommand<TypeSafeAllowCommand.Settings>
{
    private readonly ITypeSafeCredentialService _typeSafe;
    private readonly IAnsiConsole _console;
    private readonly bool _grant;

    protected TypeSafeAllowCommand(ITypeSafeCredentialService typeSafe, IAnsiConsole console, bool grant)
    {
        _typeSafe = typeSafe ?? throw new ArgumentNullException(nameof(typeSafe));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _grant = grant;
    }

    public TypeSafeAllowCommand(ITypeSafeCredentialService typeSafe, IAnsiConsole console)
        : this(typeSafe, console, grant: true) { }

    public class Settings : TypeSafeSettings
    {
        [CommandArgument(0, "<repository>")]
        [Description("Repository in owner/repo form")]
        public string Repository { get; set; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var parts = (settings.Repository ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            _console.MarkupLine("[red]Repository must be in owner/repo form.[/]");
            return 1;
        }

        var wanted = $"{parts[0]}/{parts[1]}";
        var current = (await _typeSafe.GetAllowedRepositoriesAsync()).ToList();
        var present = current.Any(r => string.Equals(r, wanted, StringComparison.OrdinalIgnoreCase));

        if (_grant == present)
        {
            _console.MarkupLine(_grant
                ? $"[yellow]{Markup.Escape(wanted)} already had TypeSafe access.[/]"
                : $"[yellow]{Markup.Escape(wanted)} already had no TypeSafe access.[/]");
            return 0;
        }

        if (_grant) current.Add(wanted);
        else current.RemoveAll(r => string.Equals(r, wanted, StringComparison.OrdinalIgnoreCase));
        await _typeSafe.SetAllowedRepositoriesAsync(current);

        if (_grant)
        {
            _console.MarkupLine($"[green]{Markup.Escape(wanted)} may now call TypeSafe through the credential socket.[/]");
            if (!await _typeSafe.HasApiKeyAsync())
                _console.MarkupLine("[yellow]No key is stored yet — run 'pks typesafe init'.[/]");
            _console.MarkupLine("[dim]Takes effect for the next request; nothing is cached in running jobs.[/]");
        }
        else
        {
            _console.MarkupLine($"[green]{Markup.Escape(wanted)} can no longer call TypeSafe.[/]");
            _console.MarkupLine("[dim]A job that fetched the raw key with /typesafe/token keeps it until that job ends. Rotate the key at console.typesafe.ai if that matters.[/]");
        }

        return 0;
    }
}

/// <summary>Revoke a repository's TypeSafe access. See <see cref="TypeSafeAllowCommand"/>.</summary>
public class TypeSafeRevokeCommand : TypeSafeAllowCommand
{
    public TypeSafeRevokeCommand(ITypeSafeCredentialService typeSafe, IAnsiConsole console)
        : base(typeSafe, console, grant: false) { }
}
