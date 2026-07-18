namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Result of probing whether this machine can actually serve devcontainer-spawning work
/// right now. <see cref="DockerAvailable"/> mirrors
/// <see cref="PKS.Infrastructure.Services.Models.DockerAvailabilityResult.IsAvailable"/> /
/// <c>IsRunning</c>; <see cref="Reason"/> is the human-readable message from that probe
/// (either "Docker is running (version X)" or "Docker is not available: &lt;exception
/// message&gt;"), suitable for direct display to the operator.
/// </summary>
public sealed record RunnerExecutionCapabilityStatus(bool DockerAvailable, string Reason);

/// <summary>
/// Probes whether this runner can actually spawn devcontainers (Docker reachable and
/// running), wrapping <see cref="IDevcontainerSpawnerService.CheckDockerAvailabilityAsync"/>
/// with a short memo so the poll loop (default: every 10s) doesn't ping the Docker daemon
/// on every single cycle.
///
/// Existence of this probe is Defect A's fix (docs/remote-runner-targets-plan.md): a
/// Docker-less runner must advertise capabilities honestly instead of claiming a devcontainer
/// job and failing it at <c>CheckDockerAvailabilityAsync</c>-time. It also backs the
/// client-side pre-claim refusal — see <c>AgenticsRunnerStartCommand.PollAndDispatchOnceAsync</c>.
/// </summary>
public interface IRunnerExecutionCapabilityProbe
{
    /// <summary>
    /// Returns the current spawn-capability status. A probe result younger than the memo
    /// window is returned without touching Docker again.
    /// </summary>
    Task<RunnerExecutionCapabilityStatus> GetStatusAsync(CancellationToken ct = default);
}
