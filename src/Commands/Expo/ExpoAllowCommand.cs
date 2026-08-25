using System.ComponentModel;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Expo;

/// <summary>
/// Grant or revoke a registered repository's access to the stored Expo token.
/// Usage: pks expo allow owner/repo | pks expo revoke owner/repo
///
/// `pks github runner register --expo` covers a repository being registered for the first time.
/// This covers the ordinary case that is left over: a repository already registered — possibly
/// long ago, possibly by a build of pks-cli that had no such flag — that now needs Expo access.
/// Re-running registration for that is a poor trade: it re-runs device-code auth, re-checks admin
/// permission and prompts to replace the registration, all to flip one boolean.
/// </summary>
public class ExpoAllowCommand : AsyncCommand<ExpoAllowCommand.Settings>
{
    private readonly IRunnerConfigurationService _runners;
    private readonly IAnsiConsole _console;
    private readonly bool _grant;

    protected ExpoAllowCommand(IRunnerConfigurationService runners, IAnsiConsole console, bool grant)
    {
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _grant = grant;
    }

    public ExpoAllowCommand(IRunnerConfigurationService runners, IAnsiConsole console)
        : this(runners, console, grant: true) { }

    public class Settings : ExpoSettings
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

        var (owner, repo) = (parts[0], parts[1]);
        var config = await _runners.LoadAsync();

        var matches = config.Registrations
            .Where(r => string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Repository, repo, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            _console.MarkupLine($"[red]{owner}/{repo} is not registered on this runner.[/]");
            _console.MarkupLine($"[cyan]Register it first:[/] pks github runner register --repo {owner}/{repo} --expo");
            return 1;
        }

        var alreadyThere = matches.All(r => r.ExpoEnabled == _grant);
        foreach (var r in matches)
            r.ExpoEnabled = _grant;

        await _runners.SaveAsync(config);

        if (alreadyThere)
        {
            _console.MarkupLine(_grant
                ? $"[yellow]{owner}/{repo} already had Expo access.[/]"
                : $"[yellow]{owner}/{repo} already had no Expo access.[/]");
            return 0;
        }

        if (_grant)
        {
            _console.MarkupLine($"[green]{owner}/{repo} may now fetch the Expo token.[/]");
            _console.MarkupLine("[dim]Jobs already running keep the token they were handed; this affects the next job.[/]");
        }
        else
        {
            _console.MarkupLine($"[green]{owner}/{repo} can no longer fetch the Expo token.[/]");
            _console.MarkupLine("[dim]A job holding it keeps it until that job ends. Rotate the token at expo.dev if that matters.[/]");
        }

        return 0;
    }
}

/// <summary>Revoke a repository's Expo access. See <see cref="ExpoAllowCommand"/>.</summary>
public class ExpoRevokeCommand : ExpoAllowCommand
{
    public ExpoRevokeCommand(IRunnerConfigurationService runners, IAnsiConsole console)
        : base(runners, console, grant: false) { }
}
