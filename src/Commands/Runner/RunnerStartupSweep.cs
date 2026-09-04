using PKS.Infrastructure.Services.Runner;
using Spectre.Console;

namespace PKS.Commands.Runner;

/// <summary>
/// The reap every runner performs as it comes up.
///
/// <para>This is the piece that was missing entirely. Job-end cleanup only runs when a job ends
/// normally; a runner killed by a reboot, an OOM or a <c>docker kill</c> never reaches it, and
/// nothing else ever looked. On the si14agents host that left eleven dead containers and 231
/// orphaned volumes accumulating since the 2026-08-21 reboot, with no code path that would ever
/// have removed them. Startup is the one moment a runner can be sure its own previous jobs are
/// over.</para>
/// </summary>
public static class RunnerStartupSweep
{
    /// <summary>
    /// Reaps exited ephemeral job containers and orphaned volumes, then reports what it did.
    ///
    /// <para>Conservative by construction: running containers, named runners, devcontainers spawned
    /// outside a runner, session transcripts and workspace volumes are all left alone — reclaiming
    /// them is what the explicit <c>runner cleanup</c> flags are for. Failures are reported and
    /// swallowed, because a volume that cannot be removed must never stop a runner from starting.</para>
    /// </summary>
    public static async Task RunAsync(IRunnerReaper reaper, IAnsiConsole console, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await reaper.ReapAsync(new ReapOptions(), cancellationToken);

            if (result.ContainersRemoved > 0 || result.VolumesRemoved > 0)
            {
                console.MarkupLine(
                    $"[grey]Startup cleanup: removed {result.ContainersRemoved} stale container(s) " +
                    $"and {result.VolumesRemoved} orphaned volume(s).[/]");
            }

            foreach (var failure in result.Failures)
            {
                console.MarkupLine($"[grey]Startup cleanup skipped: {failure.EscapeMarkup()}[/]");
            }
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[grey]Startup cleanup failed (continuing): {ex.Message.EscapeMarkup()}[/]");
        }
    }
}
