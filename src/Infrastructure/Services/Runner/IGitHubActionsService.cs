namespace PKS.Infrastructure.Services.Runner;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Service for interacting with GitHub Actions API endpoints
/// </summary>
public interface IGitHubActionsService
{
    /// <summary>
    /// Generate a JIT (just-in-time) runner configuration token
    /// POST /repos/{owner}/{repo}/actions/runners/generate-jitconfig
    /// </summary>
    Task<GitHubJitRunnerConfig> GenerateJitConfigAsync(string owner, string repo, string name, string[] labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a runner registration from a repository.
    /// DELETE /repos/{owner}/{repo}/actions/runners/{runnerId}
    /// </summary>
    /// <remarks>
    /// Only needed for registrations that never ran a job. GitHub deletes an ephemeral runner by
    /// itself once it completes one, so the happy path never calls this.
    /// </remarks>
    Task<bool> DeleteRunnerAsync(string owner, string repo, int runnerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get queued workflow runs for a repository
    /// GET /repos/{owner}/{repo}/actions/runs?status=queued
    /// </summary>
    Task<List<QueuedWorkflowRun>> GetQueuedRunsAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the authenticated user has admin permission on the repo (required for JIT config)
    /// </summary>
    Task<bool> CheckAdminPermissionAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the GitHub App is installed on a repository and has the required permissions.
    /// Returns (isInstalled, hasAdminPermission).
    /// </summary>
    Task<(bool IsInstalled, bool HasAdmin)> CheckAppInstallationAsync(string owner, string repo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get jobs for a specific workflow run to access job-level labels.
    /// GET /repos/{owner}/{repo}/actions/runs/{run_id}/jobs?filter=latest
    /// </summary>
    Task<List<WorkflowJob>> GetJobsForRunAsync(
        string owner, string repo, long runId,
        CancellationToken cancellationToken = default);
}
