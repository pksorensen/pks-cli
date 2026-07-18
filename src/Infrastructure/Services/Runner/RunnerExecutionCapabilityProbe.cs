namespace PKS.Infrastructure.Services.Runner;

/// <inheritdoc cref="IRunnerExecutionCapabilityProbe"/>
public sealed class RunnerExecutionCapabilityProbe : IRunnerExecutionCapabilityProbe
{
    /// <summary>
    /// How long a probe result is trusted before <see cref="GetStatusAsync"/> pings Docker
    /// again. 60s keeps a default 10s poll loop from hammering the daemon every cycle, while
    /// still noticing a Docker daemon that comes back (or goes away) within one to two
    /// minutes -- see docs/remote-runner-targets-plan.md Phase 1.
    /// </summary>
    private static readonly TimeSpan MemoDuration = TimeSpan.FromSeconds(60);

    private readonly IDevcontainerSpawnerService _spawnerService;
    private readonly object _gate = new();
    private RunnerExecutionCapabilityStatus? _cached;
    private DateTime _cachedAtUtc;

    public RunnerExecutionCapabilityProbe(IDevcontainerSpawnerService spawnerService)
    {
        _spawnerService = spawnerService ?? throw new ArgumentNullException(nameof(spawnerService));
    }

    public async Task<RunnerExecutionCapabilityStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cached != null && DateTime.UtcNow - _cachedAtUtc < MemoDuration)
                return _cached;
        }

        var result = await _spawnerService.CheckDockerAvailabilityAsync();
        var status = new RunnerExecutionCapabilityStatus(
            result.IsAvailable && result.IsRunning,
            result.Message);

        lock (_gate)
        {
            _cached = status;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return status;
    }
}
