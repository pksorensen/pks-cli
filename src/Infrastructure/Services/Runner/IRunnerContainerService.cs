using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Manages the lifecycle of running a GitHub Actions runner inside a devcontainer.
/// Handles: clone -> devcontainer up -> install runner -> run -> cleanup
/// </summary>
public interface IRunnerContainerService
{
    /// <summary>
    /// Execute a full job lifecycle: clone, devcontainer up, install runner, run, cleanup
    /// </summary>
    /// <param name="registration">The runner registration with owner/repo info</param>
    /// <param name="runId">The GitHub workflow run ID</param>
    /// <param name="branch">The branch to clone</param>
    /// <param name="accessToken">GitHub access token for cloning</param>
    /// <param name="encodedJitConfig">Base64-encoded JIT runner configuration</param>
    /// <param name="onProgress">Optional callback for progress reporting</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The final job state</returns>
    Task<RunnerJobState> ExecuteJobAsync(
        RunnerRegistration registration,
        long runId,
        string branch,
        string accessToken,
        string encodedJitConfig,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Execute a full job lifecycle with a named container that persists after the job.
    /// Clone, devcontainer up, install runner, run — but skip cleanup so the container can be reused.
    /// </summary>
    Task<RunnerJobState> ExecuteJobAsync(
        RunnerRegistration registration,
        long runId,
        string branch,
        string accessToken,
        string encodedJitConfig,
        Action<string>? onProgress,
        CancellationToken cancellationToken,
        string? containerName);

    /// <summary>
    /// Execute a job in an existing named container. Skips clone and devcontainer up.
    /// Installs a fresh JIT runner and runs it. Does NOT clean up the container afterwards.
    /// </summary>
    Task<RunnerJobState> ExecuteJobInExistingContainerAsync(
        RunnerRegistration registration,
        long runId,
        long jobId,
        string branch,
        string containerId,
        string clonePath,
        string containerName,
        string encodedJitConfig,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleanup a job's resources (container and clone directory)
    /// </summary>
    /// <param name="job">The job state containing container and path info</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CleanupJobAsync(RunnerJobState job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a container is still running
    /// </summary>
    Task<bool> IsContainerRunningAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discover running containers that have PKS runner labels (pks.runner.name).
    /// Returns entries for containers that can be re-populated into the named container pool.
    /// </summary>
    Task<List<NamedContainerEntry>> DiscoverNamedContainersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if Docker and devcontainer CLI are available
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple indicating availability of Docker, devcontainer CLI, and any error message</returns>
    Task<(bool DockerAvailable, bool DevcontainerCliAvailable, string? Error)> CheckPrerequisitesAsync(
        CancellationToken cancellationToken = default);
}
