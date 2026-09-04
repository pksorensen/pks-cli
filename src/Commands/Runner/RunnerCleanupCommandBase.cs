using PKS.Infrastructure.Services.Runner;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Runner;

/// <summary>
/// The options every runner surface's <c>cleanup</c> exposes. Each surface must declare the
/// properties itself — its settings class already inherits from that surface's branch settings, and
/// C# has no second base to spare — but this interface is what keeps the two spellings honest.
/// </summary>
public interface IRunnerCleanupSettings
{
    bool DryRun { get; }
    bool Yes { get; }
    bool All { get; }
    bool IncludeTranscripts { get; }
    bool IncludeWorkspaces { get; }
}

/// <summary>
/// Shared implementation of <c>runner cleanup</c> for every runner surface. Both
/// <c>pks agentics runner cleanup</c> and <c>pks github runner cleanup</c> are thin subclasses, so
/// the two can never drift into cleaning different things — which is exactly how the previous reaper
/// ended up covering one of the two label schemes in use and none of the volumes.
/// </summary>
public abstract class RunnerCleanupCommandBase<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings, IRunnerCleanupSettings
{
    private readonly IAnsiConsole _console;
    private readonly IRunnerReaper _reaper;

    protected RunnerCleanupCommandBase(IAnsiConsole console, IRunnerReaper reaper)
    {
        _console = console;
        _reaper = reaper;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        var options = BuildOptions(settings);
        var plan = await _reaper.PlanAsync(options);

        if (plan.IsEmpty)
        {
            _console.MarkupLine("[green]Nothing to clean up.[/]");
            return 0;
        }

        RenderPlan(plan);

        var volumeCount = plan.AttachedVolumes.Count + plan.OrphanVolumes.Count;

        if (settings.DryRun)
        {
            _console.MarkupLine(
                $"[cyan]--dry-run: would remove {plan.Containers.Count} container(s) and {volumeCount} volume(s).[/]");
            return 0;
        }

        if (!settings.Yes && !_console.Confirm(
                $"Remove {plan.Containers.Count} container(s) and {volumeCount} volume(s)?",
                defaultValue: false))
        {
            _console.MarkupLine("[grey]Aborted.[/]");
            return 0;
        }

        var result = await _reaper.ReapAsync(options);

        _console.MarkupLine(
            $"[bold]Removed {result.ContainersRemoved} container(s) and {result.VolumesRemoved} volume(s).[/]");

        foreach (var failure in result.Failures)
        {
            _console.MarkupLine($"[red]✗[/] {failure.EscapeMarkup()}");
        }

        return result.Failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Maps the CLI flags onto <see cref="ReapOptions"/>. <c>--all</c> covers the two populations that
    /// are persistent on purpose — named runners and devcontainers spawned outside a runner — but
    /// deliberately does <b>not</b> imply <c>--include-transcripts</c> or <c>--include-workspaces</c>:
    /// those two destroy data rather than reclaim cache, so each has to be said out loud.
    /// </summary>
    internal static ReapOptions BuildOptions(IRunnerCleanupSettings settings) => new()
    {
        DryRun = settings.DryRun,
        IncludeNamed = settings.All,
        IncludeUnattributed = settings.All,
        IncludeTranscripts = settings.IncludeTranscripts,
        IncludeWorkspaces = settings.IncludeWorkspaces,
    };

    private void RenderPlan(ReapPlan plan)
    {
        if (plan.Containers.Count > 0)
        {
            var table = new Table()
                .Title("[bold]Containers[/]")
                .AddColumn("ID")
                .AddColumn("Name")
                .AddColumn("Kind");

            foreach (var container in plan.Containers)
            {
                table.AddRow(
                    container.Id[..Math.Min(12, container.Id.Length)],
                    container.Name.EscapeMarkup(),
                    container.Kind.ToString());
            }

            _console.Write(table);
        }

        if (plan.AttachedVolumes.Count > 0)
        {
            _console.MarkupLine($"[grey]Volumes owned by those containers: {plan.AttachedVolumes.Count}[/]");
        }

        if (plan.OrphanVolumes.Count > 0)
        {
            // The population with no container left to point at it — the one that reached 319 GB.
            _console.MarkupLine($"[yellow]Orphaned volumes (no container references them): {plan.OrphanVolumes.Count}[/]");
        }
    }
}
