using System.ComponentModel;
using PKS.Commands.Runner;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.GitHub.Runner;

/// <summary>
/// Removes the devcontainers and volumes left behind by GitHub Actions runner jobs.
///
/// <para>New in this surface. <c>pks github runner</c> previously had no cleanup at all — only
/// <c>prune</c>, which removes duplicate <i>registrations</i> and never touched Docker — so a runner
/// killed by a reboot left its containers and their volumes on disk permanently. Identical behaviour
/// to <c>pks agentics runner cleanup</c>: both are thin subclasses of the same base.</para>
/// </summary>
public sealed class GitHubRunnerCleanupCommand : RunnerCleanupCommandBase<GitHubRunnerCleanupCommand.Settings>
{
    public GitHubRunnerCleanupCommand(IAnsiConsole console, IRunnerReaper reaper)
        : base(console, reaper)
    {
    }

    public sealed class Settings : GitHubSettings, IRunnerCleanupSettings
    {
        [Description("Show what would be removed without removing anything.")]
        [CommandOption("-n|--dry-run")]
        public bool DryRun { get; set; }

        [Description("Skip the confirmation prompt.")]
        [CommandOption("-y|--yes")]
        public bool Yes { get; set; }

        [Description("Also remove exited named runners and devcontainers spawned outside a runner.")]
        [CommandOption("--all")]
        public bool All { get; set; }

        [Description("Also remove claude-code-config-* session transcripts. These feed Brain/ASF ingest.")]
        [CommandOption("--include-transcripts")]
        public bool IncludeTranscripts { get; set; }

        [Description("Also remove dangling devcontainer-* workspace volumes. These can hold unpushed work.")]
        [CommandOption("--include-workspaces")]
        public bool IncludeWorkspaces { get; set; }
    }
}
