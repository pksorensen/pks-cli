using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Agentics.Runner;

/// <summary>
/// Removes devcontainers from PREVIOUS runner instances. See ADR 0002.
/// Containers are bound to the runner-process that spawned them via the
/// <c>pks.agentics.runner-instance</c> Docker label. When a runner restarts, its previously-spawned
/// containers become orphans (their bind-mounts point at temp dirs the new runner doesn't share).
/// This command lists those orphans and removes them on confirmation.
/// </summary>
public sealed class AgenticsRunnerCleanupCommand : AsyncCommand<AgenticsRunnerCleanupCommand.Settings>
{
    private readonly IAnsiConsole _console;

    public AgenticsRunnerCleanupCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public sealed class Settings : AgenticsRunnerSettings
    {
        [Description("Show what would be removed without removing anything.")]
        [CommandOption("-n|--dry-run")]
        public bool DryRun { get; set; }

        [Description("Skip the confirmation prompt.")]
        [CommandOption("-y|--yes")]
        public bool Yes { get; set; }

        [Description("Remove ALL pks.agentics.* containers (including those of currently-running runners). Use with care.")]
        [CommandOption("--all")]
        public bool All { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // 1. List every pks.agentics.* container (running OR stopped) with its runner-instance label.
        var (containers, listErr) = await ListContainersAsync();
        if (listErr != null)
        {
            _console.MarkupLine($"[red]Failed to list containers: {listErr.EscapeMarkup()}[/]");

            return 1;
        }

        if (containers.Count == 0)
        {
            _console.MarkupLine("[grey]No pks.agentics.* containers found.[/]");

            return 0;
        }

        // 2. Discover live runner instances. The current AgenticsRunnerStartCommand uses
        // a per-process static Guid for runner-instance, so we can't enumerate them directly —
        // approximate by treating any container whose runner process is still in `pgrep` as live.
        // Fallback: --all ignores the live check and removes everything.
        var liveRunnerPids = settings.All ? new HashSet<int>() : await ListRunningRunnerPidsAsync();

        // 3. Sort: orphans first, then live (only "orphans" are removed unless --all).
        var (orphans, live) = SplitOrphans(containers, liveRunnerPids);

        var table = new Table()
            .Title("[bold]pks.agentics containers[/]")
            .AddColumn("ID")
            .AddColumn("Owner / Project")
            .AddColumn("Runner Instance")
            .AddColumn("Status")
            .AddColumn("State");
        foreach (var c in orphans)
            table.AddRow(c.Id[..12], $"{c.Owner}/{c.Project}", (c.RunnerInstance ?? "—")[..Math.Min(12, c.RunnerInstance?.Length ?? 1)], c.Status, "[yellow]ORPHAN[/]");
        foreach (var c in live)
            table.AddRow(c.Id[..12], $"{c.Owner}/{c.Project}", (c.RunnerInstance ?? "—")[..Math.Min(12, c.RunnerInstance?.Length ?? 1)], c.Status, "[green]LIVE[/]");
        _console.Write(table);

        var toRemove = settings.All ? containers : orphans;
        if (toRemove.Count == 0)
        {
            _console.MarkupLine("[green]Nothing to remove.[/]");

            return 0;
        }

        if (settings.DryRun)
        {
            _console.MarkupLine($"[cyan]--dry-run: would remove {toRemove.Count} container(s).[/]");

            return 0;
        }

        if (!settings.Yes)
        {
            var confirm = _console.Confirm($"Remove {toRemove.Count} container(s)?", defaultValue: false);
            if (!confirm)
            {
                _console.MarkupLine("[grey]Aborted.[/]");

                return 0;
            }
        }

        var removed = 0;
        foreach (var c in toRemove)
        {
            var (ok, err) = await RunDockerAsync("rm", "-f", c.Id);
            if (ok)
            {
                _console.MarkupLine($"[green]✓[/] removed {c.Id[..12]} ({c.Owner}/{c.Project})");
                removed++;
            }
            else
            {
                _console.MarkupLine($"[red]✗[/] {c.Id[..12]}: {err.EscapeMarkup()}");
            }
        }
        _console.MarkupLine($"[bold]Removed {removed} of {toRemove.Count}.[/]");

        return removed == toRemove.Count ? 0 : 1;
    }

    private record ContainerInfo(string Id, string Owner, string Project, string? RunnerInstance, string Status);

    private static async Task<(List<ContainerInfo> containers, string? error)> ListContainersAsync()
    {
        // {{.Label "x"}} substitutions need to survive Spectre/CLI parsing — pass via ArgumentList.
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("ps");
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add("label=pks.agentics.fingerprint");
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("{{.ID}}\t{{.Label \"pks.agentics.owner\"}}\t{{.Label \"pks.agentics.project\"}}\t{{.Label \"pks.agentics.runner-instance\"}}\t{{.Status}}");
        try
        {
            var proc = Process.Start(psi);
            if (proc == null) return (new(), "Failed to start docker");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (proc.ExitCode != 0) return (new(), stderr.Trim());

            var list = new List<ContainerInfo>();
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;
                list.Add(new ContainerInfo(
                    Id: parts[0].Trim(),
                    Owner: parts[1].Trim(),
                    Project: parts[2].Trim(),
                    RunnerInstance: string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3].Trim(),
                    Status: parts[4].Trim()));
            }
            return (list, null);
        }
        catch (Exception ex) { return (new(), ex.Message); }
    }

    /// <summary>Approximate live-runner detection — any pks-cli "runner start" process is treated as live.</summary>
    private static async Task<HashSet<int>> ListRunningRunnerPidsAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("pgrep")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("pks-cli agentics runner start");
            var proc = Process.Start(psi);
            if (proc == null) return new();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return new HashSet<int>(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(s => int.TryParse(s.Trim(), out _))
                .Select(s => int.Parse(s.Trim())));
        }
        catch { return new(); }
    }

    /// <summary>
    /// Split containers into (orphans, live). A container is "live" if it has a non-empty
    /// runner-instance label AND there's at least one running pks-cli runner process. We can't
    /// match instance-id to a specific PID without an out-of-band registry, so the heuristic
    /// is: if ANY runner is live, assume containers labelled with a runner-instance might
    /// belong to it. Operators using --all override this conservative default.
    /// </summary>
    private static (List<ContainerInfo> orphans, List<ContainerInfo> live) SplitOrphans(
        List<ContainerInfo> containers,
        HashSet<int> liveRunnerPids)
    {
        // Conservative: if no live runners, every container is an orphan.
        if (liveRunnerPids.Count == 0)
            return (containers.ToList(), new());
        // Otherwise: containers WITHOUT a runner-instance label are definitely orphans
        // (legacy or manually-created); containers WITH one might be live, leave them alone.
        var orphans = containers.Where(c => string.IsNullOrEmpty(c.RunnerInstance)).ToList();
        var live = containers.Where(c => !string.IsNullOrEmpty(c.RunnerInstance)).ToList();
        return (orphans, live);
    }

    private static async Task<(bool ok, string err)> RunDockerAsync(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi);
            if (proc == null) return (false, "failed to start docker");
            await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return (proc.ExitCode == 0, stderr.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
