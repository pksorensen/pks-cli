using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Spectre.Console;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Foreground <c>run</c> for a machine that serves several projects: one ordinary
/// <c>pks agentics runner run --project owner/project</c> child per saved registration, kept
/// alive, with this process as their supervisor.
///
/// It is the own-hardware counterpart to <see cref="InstallationRunnerHost"/>. That one is for a
/// self-hosted *platform* — it asks the platform which projects exist and mints credentials for
/// them — which means it needs an installation identity the installer writes, and only
/// <c>install.sh</c> writes one. A box that merely runs jobs for projects on agentics.dk has no
/// such identity, and before this mode its only option was one <c>run</c> per project. So the
/// project list comes from the local registrations instead: <c>register</c> is the opt-in,
/// <c>unregister</c> the opt-out, and the platform is not consulted at all.
///
/// A child is not re-registered when it dies, unlike the installation host: this process has no
/// authority to mint a credential, only the registrations the operator already saved. A child
/// that keeps dying is therefore backed off rather than repaired, and its project is named in the
/// log so the operator knows which <c>register</c> to run again.
/// </summary>
public static class LocalMultiProjectRunnerMode
{
    public static readonly TimeSpan SuperviseInterval = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait between restart attempts for a child that keeps exiting.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A child that stayed up this long before dying is treated as a fresh failure rather than a
    /// continuation of an earlier crash loop. Without this, a runner that worked for a week and
    /// then hit a transient error would inherit an old failure count and be made to wait minutes.
    /// </summary>
    public static readonly TimeSpan HealthyUptime = TimeSpan.FromMinutes(2);

    public static async Task<int> RunAsync(
        IAgenticsRunnerConfigurationService configService,
        IAnsiConsole console,
        IProjectRunnerLauncher? launcher = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null,
        Func<AgenticsRunnerRegistration, CancellationToken, Task<bool>>? alreadyDetached = null,
        CancellationToken ct = default)
    {
        void Log(string message) => console.MarkupLine($"[dim]{message.EscapeMarkup()}[/]");

        launcher ??= new ChildProcessProjectRunnerLauncher(RunnerLauncher.ResolveSelf(), Log);
        delay ??= Task.Delay;
        now ??= () => DateTimeOffset.UtcNow;

        var running = new Dictionary<string, Child>(StringComparer.OrdinalIgnoreCase);

        // Logged once per project, not once per pass — a 10s loop would otherwise fill the screen.
        var skippedAsDetached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Re-read every pass, so `register` and `unregister` in another shell take effect
                // without restarting the supervisor.
                var registrations = await configService.ListRegistrationsAsync();
                var wanted = registrations.ToDictionary(r => Key(r.Owner, r.Project), StringComparer.OrdinalIgnoreCase);

                foreach (var key in running.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
                {
                    Log($"{key}: unregistered — stopping its runner");
                    await running[key].Process.StopAsync(TimeSpan.FromSeconds(30));
                    running.Remove(key);
                }

                foreach (var (key, registration) in wanted)
                {
                    // `runner start` may already have a detached runner for this project. Nothing
                    // in the platform stops two runners claiming for one project, so starting a
                    // second here would quietly double it.
                    if (!running.ContainsKey(key)
                        && alreadyDetached is not null
                        && await alreadyDetached(registration, ct))
                    {
                        if (skippedAsDetached.Add(key))
                            Log($"{key}: already served by a detached runner ('runner stop {key}' to take it over) — skipping");
                        continue;
                    }

                    if (running.TryGetValue(key, out var child))
                    {
                        if (!child.Process.HasExited)
                            continue;

                        if (now() < child.RetryAt)
                            continue;

                        // A child that was up long enough to be doing real work is not part of an
                        // earlier crash loop.
                        var failures = now() - child.StartedAt >= HealthyUptime ? 1 : child.Failures + 1;

                        // Doubling, because the usual reason a child exits immediately is a
                        // credential this process cannot repair. Retrying every 10s would bury the
                        // reason in a scrolling log. The wait applies to the *next* restart --
                        // this one happens now, so a one-off crash costs nothing.
                        var backoff = TimeSpan.FromSeconds(
                            Math.Min(MaxBackoff.TotalSeconds, 10 * Math.Pow(2, failures - 1)));

                        Log($"{key}: runner exited ({child.Process.ExitCode?.ToString() ?? "?"}) — " +
                            $"restarted (attempt {failures}; another exit waits {backoff.TotalSeconds:0}s)" +
                            (failures >= 3 ? $" — repeatedly failing, check 'pks agentics runner register {key}'" : ""));

                        running[key] = new Child(launcher.Start(registration), failures, now() + backoff, now());
                        continue;
                    }

                    running[key] = new Child(launcher.Start(registration), 0, DateTimeOffset.MinValue, now());
                    Log($"{key}: runner started");
                }

                if (running.Count == 0 && skippedAsDetached.Count == 0)
                {
                    console.MarkupLine("[yellow]No runner registrations saved — nothing to serve.[/]");
                    console.MarkupLine("[dim]pks agentics runner register owner/project[/]");
                    return 1;
                }

                await delay(SuperviseInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C: fall through and stop the children.
        }

        console.MarkupLine($"[dim]Stopping {running.Count} runner(s)...[/]");
        await Task.WhenAll(running.Values.Select(c => c.Process.StopAsync(TimeSpan.FromSeconds(30))));
        return 0;
    }

    private static string Key(string owner, string project) => $"{owner}/{project}";

    private sealed record Child(
        IProjectRunnerProcess Process,
        int Failures,
        DateTimeOffset RetryAt,
        DateTimeOffset StartedAt);
}
