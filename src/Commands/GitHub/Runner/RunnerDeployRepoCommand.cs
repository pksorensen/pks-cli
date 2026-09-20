using System.ComponentModel;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Add or remove an extra deploy repository on an already-registered repository.
///
/// `pks github runner register --deploy-repo` covers a repository being registered for the first
/// time. This covers what is left over, and it is the ordinary case: a repository registered long
/// ago that has just grown a Coolify application it does not own. Re-running registration to add
/// one entry re-runs device-code auth, re-checks admin permission and prompts to replace the
/// registration — the same trade `pks expo allow` exists to avoid.
///
/// The list takes effect for the next job. A job already running was handed its applications when
/// its container was prepared, and keeps exactly those.
/// </summary>
public class RunnerDeployRepoCommand : AsyncCommand<RunnerDeployRepoCommand.Settings>
{
    private readonly IRunnerConfigurationService _runners;
    private readonly IAnsiConsole _console;

    public RunnerDeployRepoCommand(IRunnerConfigurationService runners, IAnsiConsole console)
    {
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public class Settings : GitHubSettings
    {
        [CommandArgument(0, "<repository>")]
        [Description("The registered repository, in owner/repo form")]
        public string Repository { get; set; } = string.Empty;

        [CommandArgument(1, "[deploy-repository]")]
        [Description("The repository whose Coolify app it may deploy: owner/repo, or owner/repo@branch. Omit to list.")]
        public string? DeployRepository { get; set; }

        [CommandOption("--remove")]
        [Description("Remove the entry instead of adding it")]
        public bool Remove { get; set; }
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
            _console.MarkupLine($"[cyan]Register it first:[/] pks github runner register {owner}/{repo}");
            return 1;
        }

        // No entry given: report what is configured. A list command that is the default of the
        // command that edits the list means nobody has to guess the spelling before changing it.
        if (string.IsNullOrWhiteSpace(settings.DeployRepository))
        {
            var current = matches.SelectMany(r => r.DeployRepositories).Distinct().ToList();
            if (current.Count == 0)
            {
                _console.MarkupLine($"[yellow]{owner}/{repo} has no deploy repositories.[/]");
                _console.MarkupLine($"[dim]Its jobs may deploy only the Coolify apps built from {owner}/{repo} itself.[/]");
                return 0;
            }

            _console.MarkupLine($"[cyan]{owner}/{repo} may also deploy the Coolify apps built from:[/]");
            foreach (var entry in current)
                _console.MarkupLine($"  [green]{entry.EscapeMarkup()}[/]");
            return 0;
        }

        var value = settings.DeployRepository.Trim();
        var (slug, _) = Infrastructure.Services.Runner.RunnerContainerService.ParseDeployRepository(value);
        if (slug.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2)
        {
            _console.MarkupLine("[red]Deploy repository must be owner/repo or owner/repo@branch.[/]");
            return 1;
        }

        var changed = false;
        foreach (var registration in matches)
        {
            var existing = registration.DeployRepositories
                .FirstOrDefault(e => string.Equals(e, value, StringComparison.OrdinalIgnoreCase));

            if (settings.Remove)
            {
                if (existing != null)
                {
                    registration.DeployRepositories.Remove(existing);
                    changed = true;
                }
            }
            else if (existing == null)
            {
                registration.DeployRepositories.Add(value);
                changed = true;
            }
        }

        if (!changed)
        {
            _console.MarkupLine(settings.Remove
                ? $"[yellow]{owner}/{repo} did not have {value.EscapeMarkup()}.[/]"
                : $"[yellow]{owner}/{repo} already had {value.EscapeMarkup()}.[/]");
            return 0;
        }

        await _runners.SaveAsync(config);

        if (settings.Remove)
        {
            _console.MarkupLine($"[green]{owner}/{repo} can no longer deploy apps built from {value.EscapeMarkup()}.[/]");
        }
        else
        {
            _console.MarkupLine($"[green]{owner}/{repo} may now deploy the Coolify apps built from {value.EscapeMarkup()}.[/]");
            _console.MarkupLine($"[dim]Name it in the workflow with `coolify-app:` if more than one app resolves.[/]");
        }

        _console.MarkupLine("[dim]Takes effect for the next job; the daemon reads this list when it prepares a container.[/]");
        return 0;
    }
}
