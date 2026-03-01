using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Manages the lifecycle of running a GitHub Actions runner inside a devcontainer.
/// Orchestrates: clone -> devcontainer up -> install runner -> run -> cleanup
/// </summary>
public class RunnerContainerService : IRunnerContainerService
{
    private const string RunnerVersion = "2.332.0";
    private const string RunnerTarball = $"actions-runner-linux-x64-{RunnerVersion}.tar.gz";
    private const string RunnerDownloadUrl = $"https://github.com/actions/runner/releases/download/v{RunnerVersion}/{RunnerTarball}";
    private const string RunnerInstallPath = "/tmp/actions-runner";

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<RunnerContainerService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunnerContainerService"/> class
    /// </summary>
    /// <param name="processRunner">Abstraction for running external processes</param>
    /// <param name="logger">Logger for diagnostic output</param>
    public RunnerContainerService(IProcessRunner processRunner, ILogger<RunnerContainerService> logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<bool> IsContainerRunningAsync(string containerId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _processRunner.RunAsync("docker", $"inspect --format={{{{.State.Running}}}} {containerId}", null, cancellationToken);
            return result.ExitCode == 0 && result.StandardOutput?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<(bool DockerAvailable, bool DevcontainerCliAvailable, string? Error)> CheckPrerequisitesAsync(
        CancellationToken cancellationToken = default)
    {
        bool dockerAvailable = false;
        bool devcontainerAvailable = false;
        var errors = new List<string>();

        // Check Docker
        try
        {
            var dockerResult = await _processRunner.RunAsync("docker", "version", null, cancellationToken);
            dockerAvailable = dockerResult.ExitCode == 0;
            if (!dockerAvailable)
            {
                errors.Add("Docker is not available or not running");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check Docker availability");
            errors.Add($"Docker check failed: {ex.Message}");
        }

        // Check devcontainer CLI
        try
        {
            var devcontainerResult = await _processRunner.RunAsync("devcontainer", "--version", null, cancellationToken);
            devcontainerAvailable = devcontainerResult.ExitCode == 0;
            if (!devcontainerAvailable)
            {
                errors.Add("devcontainer CLI is not installed. Install with: npm install -g @devcontainers/cli");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check devcontainer CLI availability");
            errors.Add($"devcontainer CLI check failed: {ex.Message}");
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return (dockerAvailable, devcontainerAvailable, error);
    }

    /// <inheritdoc/>
    public Task<RunnerJobState> ExecuteJobAsync(
        RunnerRegistration registration,
        long runId,
        string branch,
        string accessToken,
        string encodedJitConfig,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
        => ExecuteJobAsync(registration, runId, branch, accessToken, encodedJitConfig, onProgress, cancellationToken, containerName: null);

    /// <summary>
    /// Execute a full job lifecycle with optional container name for reuse.
    /// When containerName is set, the container is kept alive after the job completes.
    /// </summary>
    public async Task<RunnerJobState> ExecuteJobAsync(
        RunnerRegistration registration,
        long runId,
        string branch,
        string accessToken,
        string encodedJitConfig,
        Action<string>? onProgress,
        CancellationToken cancellationToken,
        string? containerName)
    {
        var job = new RunnerJobState
        {
            Registration = registration,
            RunId = runId,
            Branch = branch,
            StartedAt = DateTime.UtcNow,
            Status = RunnerJobStatus.Cloning
        };

        var clonePath = Path.Combine(Path.GetTempPath(), $"pks-runner-{Guid.NewGuid():N}");
        job.ClonePath = clonePath;

        try
        {
            // Step 1: Clone the repository
            onProgress?.Invoke($"Cloning {registration.Owner}/{registration.Repository}@{branch} to {clonePath}");
            _logger.LogInformation("Cloning {Owner}/{Repo}@{Branch} to {Path}",
                registration.Owner, registration.Repository, branch, clonePath);

            var cloneArgs = $"clone --depth=1 --branch {branch} https://x-access-token:{accessToken}@github.com/{registration.Owner}/{registration.Repository}.git {clonePath}";
            var cloneResult = await _processRunner.RunAsync("git", cloneArgs, null, cancellationToken);

            if (cloneResult.ExitCode != 0)
            {
                var errorMsg = cloneResult.StandardError?.Trim();
                onProgress?.Invoke($"Clone failed (exit {cloneResult.ExitCode}): {errorMsg}");
                _logger.LogError("Git clone failed with exit code {ExitCode}: {StdErr}",
                    cloneResult.ExitCode, cloneResult.StandardError);
                job.Status = RunnerJobStatus.Failed;
                return job;
            }

            onProgress?.Invoke($"Clone complete: {clonePath}");

            // Step 2: Start devcontainer
            job.Status = RunnerJobStatus.Building;
            onProgress?.Invoke($"Running: devcontainer up --workspace-folder {clonePath} (this may take a few minutes on first run)");
            _logger.LogInformation("Running devcontainer up for {Path}", clonePath);

            var baseArgs = $"up --workspace-folder {clonePath} --remote-env PKS_RUNNER=true";
            var devcontainerArgs = containerName != null
                ? $"{baseArgs} --id-label pks.runner.name={containerName} --id-label pks.runner.owner={registration.Owner} --id-label pks.runner.repo={registration.Repository}"
                : $"{baseArgs} --remove-existing-container";
            var devcontainerResult = await _processRunner.RunAsync("devcontainer", devcontainerArgs, null, cancellationToken);

            if (devcontainerResult.ExitCode != 0)
            {
                var errorMsg = devcontainerResult.StandardError?.Trim();
                if (string.IsNullOrEmpty(errorMsg))
                    errorMsg = devcontainerResult.StandardOutput?.Trim();
                onProgress?.Invoke($"devcontainer up failed (exit {devcontainerResult.ExitCode}): {errorMsg}");
                _logger.LogError("devcontainer up failed with exit code {ExitCode}: {StdErr}",
                    devcontainerResult.ExitCode, devcontainerResult.StandardError);
                job.Status = RunnerJobStatus.Failed;
                return job;
            }

            // Parse container ID from devcontainer up JSON output
            var containerId = ParseContainerId(devcontainerResult.StandardOutput);
            if (string.IsNullOrEmpty(containerId))
            {
                onProgress?.Invoke($"Could not parse container ID from devcontainer output");
                _logger.LogError("Could not parse container ID from devcontainer up output: {Output}",
                    devcontainerResult.StandardOutput);
                job.Status = RunnerJobStatus.Failed;
                return job;
            }

            job.ContainerId = containerId;
            if (containerName != null)
                job.ContainerName = containerName;
            onProgress?.Invoke($"Devcontainer ready: container {containerId[..Math.Min(12, containerId.Length)]}");

            // Step 3: Install the GitHub Actions runner inside the container
            onProgress?.Invoke($"Installing GitHub Actions runner v{RunnerVersion} in container...");
            _logger.LogInformation("Installing runner in container {ContainerId}", containerId);

            var installArgs = $"exec {containerId} bash -c \"mkdir -p {RunnerInstallPath} && curl -sfL {RunnerDownloadUrl} | tar xz -C {RunnerInstallPath}\"";
            var installResult = await _processRunner.RunAsync("docker", installArgs, null, cancellationToken);

            if (installResult.ExitCode != 0)
            {
                var errorMsg = installResult.StandardError?.Trim();
                onProgress?.Invoke($"Runner install failed (exit {installResult.ExitCode}): {errorMsg}");
                _logger.LogError("Runner installation failed with exit code {ExitCode}: {StdErr}",
                    installResult.ExitCode, installResult.StandardError);
                job.Status = RunnerJobStatus.Failed;
                return job;
            }

            onProgress?.Invoke("Runner binary installed");

            // Step 4: Start the runner with JIT config
            job.Status = RunnerJobStatus.Running;
            onProgress?.Invoke("Starting runner with JIT config (waiting for job to complete)...");
            _logger.LogInformation("Starting runner in container {ContainerId} for run {RunId}", containerId, runId);

            // Write the JIT config to a file inside the container to avoid shell escaping issues
            var writeConfigArgs = $"exec {containerId} bash -c \"echo '{encodedJitConfig}' > {RunnerInstallPath}/.jitconfig\"";
            await _processRunner.RunAsync("docker", writeConfigArgs, null, cancellationToken);

            // Run the runner with RUNNER_ALLOW_RUNASROOT and capture all output
            var runArgs = $"exec -w {RunnerInstallPath} -e RUNNER_ALLOW_RUNASROOT=1 {containerId} bash -c \"./run.sh --jitconfig $(cat .jitconfig) 2>&1\"";
            var runResult = await _processRunner.RunAsync("docker", runArgs, null, cancellationToken);

            // Always log the runner output for debugging
            var stdout = runResult.StandardOutput?.Trim();
            var stderr = runResult.StandardError?.Trim();
            if (!string.IsNullOrEmpty(stdout))
                onProgress?.Invoke($"Runner output: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                onProgress?.Invoke($"Runner stderr: {stderr}");

            _logger.LogInformation("Runner exit code {ExitCode} for run {RunId}. stdout: {StdOut}, stderr: {StdErr}",
                runResult.ExitCode, runId, stdout, stderr);

            // Step 5: Runner has exited - determine outcome
            if (runResult.ExitCode == 0 && !string.IsNullOrEmpty(stdout) && stdout.Contains("Runner.Listener"))
            {
                job.Status = RunnerJobStatus.Completed;
                onProgress?.Invoke("Runner completed successfully");
            }
            else if (runResult.ExitCode == 0 && string.IsNullOrEmpty(stdout))
            {
                // Runner exited 0 but produced no output — likely didn't actually start
                job.Status = RunnerJobStatus.Failed;
                onProgress?.Invoke("Runner exited with code 0 but produced no output — may not have started properly");
            }
            else if (runResult.ExitCode == 0)
            {
                job.Status = RunnerJobStatus.Completed;
                onProgress?.Invoke("Runner completed successfully");
            }
            else
            {
                job.Status = RunnerJobStatus.Failed;
                var details = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                onProgress?.Invoke($"Runner exited with code {runResult.ExitCode}: {details}");
            }

            // Step 6: Cleanup (skip for named containers)
            if (containerName == null)
            {
                onProgress?.Invoke("Cleaning up container and clone directory...");
                await CleanupJobAsync(job, cancellationToken);
                onProgress?.Invoke("Cleanup complete");
            }
            else
            {
                onProgress?.Invoke($"Keeping named container '{containerName}' alive for reuse");
            }

            return job;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId} was cancelled", job.JobId);
            job.Status = RunnerJobStatus.Failed;
            onProgress?.Invoke("Job cancelled. Cleaning up...");

            // Best-effort cleanup on cancellation
            try
            {
                await CleanupJobAsync(job, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Cleanup after cancellation failed for job {JobId}", job.JobId);
            }

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during job execution for run {RunId}", runId);
            job.Status = RunnerJobStatus.Failed;
            onProgress?.Invoke($"Error: {ex.Message}");

            // Best-effort cleanup on failure
            try
            {
                await CleanupJobAsync(job, CancellationToken.None);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Cleanup after failure failed for job {JobId}", job.JobId);
            }

            return job;
        }
    }

    /// <inheritdoc/>
    public async Task<RunnerJobState> ExecuteJobInExistingContainerAsync(
        RunnerRegistration registration,
        long runId,
        long jobId,
        string branch,
        string containerId,
        string clonePath,
        string containerName,
        string encodedJitConfig,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var job = new RunnerJobState
        {
            Registration = registration,
            RunId = runId,
            WorkflowJobId = jobId,
            Branch = branch,
            ContainerId = containerId,
            ClonePath = clonePath,
            ContainerName = containerName,
            StartedAt = DateTime.UtcNow,
            Status = RunnerJobStatus.Running
        };

        var runnerPath = $"/tmp/actions-runner-{jobId}";

        try
        {
            // Step 1: Install the GitHub Actions runner to a unique path
            onProgress?.Invoke($"Installing GitHub Actions runner v{RunnerVersion} in existing container {containerId[..Math.Min(12, containerId.Length)]}...");
            _logger.LogInformation("Installing runner to {RunnerPath} in container {ContainerId} for job {JobId}",
                runnerPath, containerId, jobId);

            var installArgs = $"exec {containerId} bash -c \"mkdir -p {runnerPath} && curl -sfL {RunnerDownloadUrl} | tar xz -C {runnerPath}\"";
            var installResult = await _processRunner.RunAsync("docker", installArgs, null, cancellationToken);

            if (installResult.ExitCode != 0)
            {
                var errorMsg = installResult.StandardError?.Trim();
                onProgress?.Invoke($"Runner install failed (exit {installResult.ExitCode}): {errorMsg}");
                _logger.LogError("Runner installation failed with exit code {ExitCode}: {StdErr}",
                    installResult.ExitCode, installResult.StandardError);
                job.Status = RunnerJobStatus.Failed;
                return job;
            }

            onProgress?.Invoke("Runner binary installed");

            // Step 2: Start the runner with JIT config
            onProgress?.Invoke("Starting runner with JIT config (waiting for job to complete)...");
            _logger.LogInformation("Starting runner in container {ContainerId} for run {RunId}, job {JobId}",
                containerId, runId, jobId);

            var writeConfigArgs = $"exec {containerId} bash -c \"echo '{encodedJitConfig}' > {runnerPath}/.jitconfig\"";
            await _processRunner.RunAsync("docker", writeConfigArgs, null, cancellationToken);

            var runArgs = $"exec -w {runnerPath} -e RUNNER_ALLOW_RUNASROOT=1 {containerId} bash -c \"./run.sh --jitconfig $(cat .jitconfig) 2>&1\"";
            var runResult = await _processRunner.RunAsync("docker", runArgs, null, cancellationToken);

            var stdout = runResult.StandardOutput?.Trim();
            var stderr = runResult.StandardError?.Trim();
            if (!string.IsNullOrEmpty(stdout))
                onProgress?.Invoke($"Runner output: {stdout}");
            if (!string.IsNullOrEmpty(stderr))
                onProgress?.Invoke($"Runner stderr: {stderr}");

            _logger.LogInformation("Runner exit code {ExitCode} for run {RunId}, job {JobId}. stdout: {StdOut}, stderr: {StdErr}",
                runResult.ExitCode, runId, jobId, stdout, stderr);

            // Step 3: Determine outcome
            if (runResult.ExitCode == 0 && !string.IsNullOrEmpty(stdout) && stdout.Contains("Runner.Listener"))
            {
                job.Status = RunnerJobStatus.Completed;
                onProgress?.Invoke("Runner completed successfully");
            }
            else if (runResult.ExitCode == 0 && string.IsNullOrEmpty(stdout))
            {
                job.Status = RunnerJobStatus.Failed;
                onProgress?.Invoke("Runner exited with code 0 but produced no output — may not have started properly");
            }
            else if (runResult.ExitCode == 0)
            {
                job.Status = RunnerJobStatus.Completed;
                onProgress?.Invoke("Runner completed successfully");
            }
            else
            {
                job.Status = RunnerJobStatus.Failed;
                var details = !string.IsNullOrEmpty(stderr) ? stderr : stdout;
                onProgress?.Invoke($"Runner exited with code {runResult.ExitCode}: {details}");
            }

            // Step 4: Clean up the runner install directory only (container stays alive)
            onProgress?.Invoke($"Cleaning up runner directory {runnerPath}...");
            try
            {
                await _processRunner.RunAsync("docker", $"exec {containerId} rm -rf {runnerPath}", null, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up runner directory {RunnerPath} in container {ContainerId}", runnerPath, containerId);
            }

            onProgress?.Invoke($"Keeping named container '{containerName}' alive for reuse");
            return job;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Job {JobId} was cancelled", job.JobId);
            job.Status = RunnerJobStatus.Failed;
            onProgress?.Invoke("Job cancelled");
            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during job execution in existing container for run {RunId}, job {JobId}", runId, jobId);
            job.Status = RunnerJobStatus.Failed;
            onProgress?.Invoke($"Error: {ex.Message}");
            return job;
        }
    }

    /// <inheritdoc/>
    public async Task CleanupJobAsync(RunnerJobState job, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(job.ContainerName))
        {
            _logger.LogInformation("Skipping cleanup for named container '{Name}' (job {JobId})", job.ContainerName, job.JobId);
            return;
        }

        _logger.LogInformation("Cleaning up job {JobId}", job.JobId);

        // Remove the Docker container (ignore errors)
        if (!string.IsNullOrEmpty(job.ContainerId))
        {
            try
            {
                var rmResult = await _processRunner.RunAsync("docker", $"rm -f {job.ContainerId}", null, cancellationToken);
                if (rmResult.ExitCode != 0)
                {
                    _logger.LogWarning("docker rm failed for container {ContainerId}: {StdErr}",
                        job.ContainerId, rmResult.StandardError);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove container {ContainerId}", job.ContainerId);
            }
        }

        // Remove the clone directory
        if (!string.IsNullOrEmpty(job.ClonePath) && Directory.Exists(job.ClonePath))
        {
            try
            {
                Directory.Delete(job.ClonePath, recursive: true);
                _logger.LogInformation("Removed clone directory {Path}", job.ClonePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove clone directory {Path}", job.ClonePath);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<List<NamedContainerEntry>> DiscoverNamedContainersAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<NamedContainerEntry>();

        try
        {
            // Find running containers with pks.runner.name label
            var psResult = await _processRunner.RunAsync("docker",
                "ps --filter label=pks.runner.name --filter status=running --format {{.ID}}",
                null, cancellationToken);

            if (psResult.ExitCode != 0 || string.IsNullOrWhiteSpace(psResult.StandardOutput))
                return entries;

            var containerIds = psResult.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var containerId in containerIds)
            {
                try
                {
                    // Read labels from the container
                    var inspectResult = await _processRunner.RunAsync("docker",
                        $"inspect --format {{{{index .Config.Labels \"pks.runner.name\"}}}}|{{{{index .Config.Labels \"pks.runner.owner\"}}}}|{{{{index .Config.Labels \"pks.runner.repo\"}}}} {containerId}",
                        null, cancellationToken);

                    if (inspectResult.ExitCode != 0 || string.IsNullOrWhiteSpace(inspectResult.StandardOutput))
                        continue;

                    var parts = inspectResult.StandardOutput.Trim().Split('|');
                    if (parts.Length < 3 || string.IsNullOrEmpty(parts[0]))
                        continue;

                    var entry = new NamedContainerEntry
                    {
                        Name = parts[0],
                        ContainerId = containerId,
                        Owner = parts[1],
                        Repository = parts[2],
                        CreatedAt = DateTime.UtcNow,
                        LastUsedAt = DateTime.UtcNow
                    };

                    entries.Add(entry);
                    _logger.LogInformation("Discovered named container '{Name}' in container {ContainerId}", entry.Name, containerId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect container {ContainerId} for labels", containerId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover named containers");
        }

        return entries;
    }

    /// <summary>
    /// Parses the container ID from the JSON output of 'devcontainer up'
    /// </summary>
    private string? ParseContainerId(string devcontainerOutput)
    {
        try
        {
            // devcontainer up outputs JSON like: {"outcome":"success","containerId":"abc123",...}
            // The output may contain non-JSON lines before the JSON, so find the JSON object
            var jsonStart = devcontainerOutput.IndexOf('{');
            if (jsonStart < 0)
                return null;

            var jsonEnd = devcontainerOutput.LastIndexOf('}');
            if (jsonEnd < 0)
                return null;

            var json = devcontainerOutput[jsonStart..(jsonEnd + 1)];

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("containerId", out var containerIdElement))
            {
                return containerIdElement.GetString();
            }

            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse devcontainer up JSON output");
            return null;
        }
    }
}
