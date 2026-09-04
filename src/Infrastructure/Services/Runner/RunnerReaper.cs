using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

/// <inheritdoc cref="IRunnerReaper"/>
public class RunnerReaper : IRunnerReaper
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<RunnerReaper> _logger;

    public RunnerReaper(IProcessRunner processRunner, ILogger<RunnerReaper> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ReapPlan> PlanAsync(ReapOptions options, CancellationToken cancellationToken = default)
    {
        var containers = await DiscoverContainersAsync(cancellationToken);

        var reapable = containers
            .Where(c => RunnerReaperParsing.ShouldReap(c, options))
            .ToList();

        var attached = new List<string>();
        foreach (var container in reapable)
        {
            foreach (var volume in RunnerReaperParsing.ReapableVolumes(container, options))
            {
                if (!attached.Contains(volume, StringComparer.Ordinal))
                {
                    attached.Add(volume);
                }
            }
        }

        // Second pass: volumes whose container is already gone. This is the population that grew to
        // 319 GB on the si14agents host — a containers-only reaper never sees it, because there is no
        // container left to discover.
        var dangling = await ListDanglingVolumesAsync(cancellationToken);
        var orphans = RunnerReaperParsing.SelectOrphanVolumes(dangling, options)
            .Where(v => !attached.Contains(v, StringComparer.Ordinal))
            .ToList();

        return new ReapPlan
        {
            Containers = reapable,
            AttachedVolumes = attached,
            OrphanVolumes = orphans,
        };
    }

    /// <inheritdoc/>
    public async Task<ReapResult> ReapAsync(ReapOptions options, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(options, cancellationToken);

        if (options.DryRun || plan.IsEmpty)
        {
            return new ReapResult();
        }

        var failures = new List<string>();
        var containersRemoved = 0;
        var volumesRemoved = 0;

        // Containers first: a volume cannot be removed while any container still references it, so
        // this ordering is what makes the volume pass able to succeed at all.
        foreach (var container in plan.Containers)
        {
            var result = await RunAsync($"rm -f -v {container.Id}", cancellationToken);
            if (result.ExitCode == 0)
            {
                containersRemoved++;
                _logger.LogInformation("Reaped container {Name} ({Id})", container.Name, Short(container.Id));
            }
            else
            {
                failures.Add($"container {container.Name}: {result.StandardError.Trim()}");
                _logger.LogWarning("Failed to reap container {Name}: {Error}", container.Name, result.StandardError.Trim());
            }
        }

        foreach (var volume in plan.AttachedVolumes.Concat(plan.OrphanVolumes))
        {
            if (await RemoveVolumeAsync(volume, failures, cancellationToken))
            {
                volumesRemoved++;
            }
        }

        return new ReapResult
        {
            ContainersRemoved = containersRemoved,
            VolumesRemoved = volumesRemoved,
            Failures = failures,
        };
    }

    /// <summary>
    /// Discovers every container carrying one of <see cref="RunnerReaperParsing.DiscoveryLabels"/>.
    /// Docker ANDs repeated <c>--filter label=</c> arguments, so this runs one query per key and
    /// unions the ids — the two historical label schemes mark disjoint populations, and a single
    /// combined filter would have returned nothing.
    /// </summary>
    private async Task<List<RunnerContainerInfo>> DiscoverContainersAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();

        foreach (var label in RunnerReaperParsing.DiscoveryLabels)
        {
            var result = await RunAsync($"ps -a --filter label={label} --format {{{{.ID}}}}", cancellationToken);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning("docker ps failed for label {Label}: {Error}", label, result.StandardError.Trim());
                continue;
            }

            foreach (var line in SplitLines(result.StandardOutput))
            {
                if (!ids.Contains(line, StringComparer.Ordinal))
                {
                    ids.Add(line);
                }
            }
        }

        if (ids.Count == 0)
        {
            return new List<RunnerContainerInfo>();
        }

        var inspect = await RunAsync(RunnerReaperParsing.InspectArguments(ids), cancellationToken);

        // A non-zero exit means at least one id vanished between ps and inspect; the lines for the
        // survivors are still on stdout, so parse what came back rather than abandoning the run.
        if (inspect.ExitCode != 0)
        {
            _logger.LogDebug("docker inspect reported errors (continuing with partial output): {Error}",
                inspect.StandardError.Trim());
        }

        return SplitLines(inspect.StandardOutput)
            .Select(RunnerReaperParsing.ParseInspectLine)
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();
    }

    private async Task<List<string>> ListDanglingVolumesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("volume ls -q --filter dangling=true", cancellationToken);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("docker volume ls failed: {Error}", result.StandardError.Trim());
            return new List<string>();
        }

        return SplitLines(result.StandardOutput);
    }

    /// <summary>
    /// Removes one volume, tolerating the two benign outcomes: the volume never existed (a
    /// devcontainer only materialises the mounts its config declares), and the volume is in use (a
    /// job started between the plan and the removal — Docker refuses, which is the behaviour that
    /// makes a concurrent sweep safe without a lock).
    /// </summary>
    private async Task<bool> RemoveVolumeAsync(string volume, List<string> failures, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"volume rm {volume}", cancellationToken);
        if (result.ExitCode == 0)
        {
            _logger.LogInformation("Reaped volume {Volume}", volume);
            return true;
        }

        var error = result.StandardError.Trim();

        if (error.Contains("no such volume", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (error.Contains("volume is in use", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Skipping volume {Volume}: in use by a live container", volume);
            return false;
        }

        failures.Add($"volume {volume}: {error}");
        _logger.LogWarning("Failed to reap volume {Volume}: {Error}", volume, error);
        return false;
    }

    private Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken) =>
        _processRunner.RunAsync("docker", arguments, null, cancellationToken);

    private static List<string> SplitLines(string output) =>
        (output ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private static string Short(string id) => id.Length > 12 ? id[..12] : id;
}
