using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

public class RunnerDaemonService : IRunnerDaemonService
{
    private readonly IRunnerConfigurationService _configService;
    private readonly IGitHubActionsService _actionsService;
    private readonly IRunnerContainerService _containerService;
    private readonly IGitHubAuthenticationService _authService;
    private readonly IGitHubApiClient _apiClient;
    private readonly INamedContainerPool _containerPool;
    private readonly ILogger<RunnerDaemonService> _logger;

    private static readonly HashSet<string> ReservedLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "self-hosted", "devcontainer-runner"
    };

    // State tracking
    private bool _isRunning;
    private bool _shutdownRequested;
    private DateTime? _startedAt;
    private int _totalJobsCompleted;
    private int _totalJobsFailed;
    private readonly List<RunnerJobState> _activeJobs = new();
    private readonly Dictionary<string, DateTime> _lastPollTimes = new();
    private readonly List<Task<RunnerJobState>> _runningTasks = new();
    private readonly HashSet<long> _dispatchedJobIds = new();
    private readonly object _lock = new();
    private int _consecutiveAuthFailures;

    public event EventHandler<RunnerJobState>? JobStarted;
    public event EventHandler<RunnerJobState>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    public RunnerDaemonService(
        IRunnerConfigurationService configService,
        IGitHubActionsService actionsService,
        IRunnerContainerService containerService,
        IGitHubAuthenticationService authService,
        IGitHubApiClient apiClient,
        INamedContainerPool containerPool,
        ILogger<RunnerDaemonService> logger)
    {
        _configService = configService;
        _actionsService = actionsService;
        _containerService = containerService;
        _authService = authService;
        _apiClient = apiClient;
        _containerPool = containerPool;
        _logger = logger;
    }

    public RunnerDaemonStatus GetStatus()
    {
        lock (_lock)
        {
            return new RunnerDaemonStatus
            {
                IsRunning = _isRunning,
                StartedAt = _startedAt,
                ActiveJobs = new List<RunnerJobState>(_activeJobs),
                LastPollTimes = new Dictionary<string, DateTime>(_lastPollTimes),
                TotalJobsCompleted = _totalJobsCompleted,
                TotalJobsFailed = _totalJobsFailed,
                NamedContainers = _containerPool.GetAll().ToList()
            };
        }
    }

    public void RequestShutdown()
    {
        _shutdownRequested = true;
        _logger.LogInformation("Graceful shutdown requested");
        OnStatusChanged("Shutdown requested - finishing active jobs");
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Load configuration
        var config = await _configService.LoadAsync();

        // Get auth token and set it on the API client
        var storedToken = await _authService.GetStoredTokenAsync();
        if (storedToken == null || !storedToken.IsValid)
        {
            throw new InvalidOperationException(
                "No valid GitHub authentication token found. Run 'pks github runner register --repo owner/repo' first.");
        }
        var accessToken = storedToken.AccessToken;
        _apiClient.SetAuthenticationToken(accessToken);

        // Filter to enabled registrations
        var enabledRegistrations = config.Registrations
            .Where(r => r.Enabled)
            .ToList();

        if (enabledRegistrations.Count == 0)
        {
            _logger.LogWarning("No enabled registrations found. Exiting daemon.");
            OnStatusChanged("No enabled registrations");
            return;
        }

        // Mark as running
        lock (_lock)
        {
            _isRunning = true;
            _startedAt = DateTime.UtcNow;
        }
        OnStatusChanged($"Daemon started, watching {enabledRegistrations.Count} registration(s)");

        // Discover existing named containers from previous sessions
        try
        {
            var discovered = await _containerService.DiscoverNamedContainersAsync(cancellationToken);
            foreach (var entry in discovered)
            {
                _containerPool.Register(entry);
                OnStatusChanged($"Recovered named container '{entry.Name}' ({entry.ContainerId[..Math.Min(12, entry.ContainerId.Length)]})");
            }
            if (discovered.Count > 0)
                OnStatusChanged($"Recovered {discovered.Count} named container(s) from previous session");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover existing named containers, continuing without recovery");
            OnStatusChanged("Container discovery failed, starting fresh");
        }

        try
        {
            await PollLoop(config, enabledRegistrations, accessToken, cancellationToken);
        }
        finally
        {
            // Wait for any remaining active jobs
            await WaitForActiveJobs();

            lock (_lock)
            {
                _isRunning = false;
            }
            OnStatusChanged("Daemon stopped");
        }
    }

    private async Task PollLoop(
        RunnerConfiguration config,
        List<RunnerRegistration> registrations,
        string accessToken,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            try
            {
                // Collect completed tasks
                CollectCompletedJobs();

                // Poll each registration
                foreach (var registration in registrations)
                {
                    if (cancellationToken.IsCancellationRequested || _shutdownRequested)
                        break;

                    await PollRegistration(registration, config, accessToken, cancellationToken);
                }

                // Polling succeeded — reset auth failure counter
                _consecutiveAuthFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex.Message.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)
                                     || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveAuthFailures++;

                if (_consecutiveAuthFailures > 3)
                {
                    _logger.LogError("Token refresh failed {Count} consecutive times. Re-run 'pks github runner register' to re-authenticate.",
                        _consecutiveAuthFailures);
                    OnStatusChanged($"Auth failing repeatedly ({_consecutiveAuthFailures}x) — re-authenticate with 'pks github runner register'");

                    // Wait longer before retrying to avoid hammering the API
                    try { await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _logger.LogWarning("Token expired (attempt {Count}), attempting refresh...", _consecutiveAuthFailures);
                OnStatusChanged("Token expired, refreshing...");

                var newToken = await _authService.RefreshTokenAsync();
                if (newToken != null)
                {
                    accessToken = newToken.AccessToken;
                    _apiClient.SetAuthenticationToken(accessToken);
                    OnStatusChanged("Token refreshed successfully");
                }
                else
                {
                    _logger.LogError("Token refresh failed. Re-run 'pks github runner register' to re-authenticate.");
                    OnStatusChanged("Token refresh failed — re-authenticate with 'pks github runner register'");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during polling cycle");
                OnStatusChanged($"Polling error: {ex.Message}");
            }

            // Wait for polling interval
            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(config.PollingIntervalSeconds),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollRegistration(
        RunnerRegistration registration,
        RunnerConfiguration config,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var repoKey = $"{registration.Owner}/{registration.Repository}";
        _logger.LogDebug("Polling {Repo} for queued runs", repoKey);

        var queuedRuns = await _actionsService.GetQueuedRunsAsync(
            registration.Owner, registration.Repository, cancellationToken);

        lock (_lock)
        {
            _lastPollTimes[repoKey] = DateTime.UtcNow;
        }

        OnStatusChanged($"Polled {repoKey}: {queuedRuns.Count} queued run(s)");

        if (queuedRuns.Count == 0)
            return;

        // Fetch jobs for each queued run to get job-level labels
        foreach (var run in queuedRuns)
        {
            if (_shutdownRequested || cancellationToken.IsCancellationRequested)
                break;

            List<WorkflowJob> jobs;
            try
            {
                jobs = await _actionsService.GetJobsForRunAsync(
                    registration.Owner, registration.Repository, run.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                // Fallback: if Jobs API fails, dispatch at run level as ephemeral (backward compat)
                _logger.LogWarning(ex, "Failed to fetch jobs for run {RunId}, falling back to run-level dispatch", run.Id);
                await DispatchRunLevelFallback(registration, run, config, accessToken, cancellationToken);
                continue;
            }

            var queuedJobs = jobs.Where(j =>
                string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var job in queuedJobs)
            {
                if (_shutdownRequested)
                    break;

                // Skip if already dispatched
                lock (_lock)
                {
                    if (_dispatchedJobIds.Contains(job.Id))
                        continue;
                }

                // Check concurrency limit
                int activeCount;
                lock (_lock)
                {
                    activeCount = _activeJobs.Count;
                }

                if (activeCount >= config.MaxConcurrentJobs)
                {
                    _logger.LogDebug(
                        "Max concurrent jobs ({Max}) reached, skipping job {JobId}",
                        config.MaxConcurrentJobs, job.Id);
                    OnStatusChanged($"Max concurrent jobs reached, skipping job {job.Id}");
                    break;
                }

                // Extract container name from labels (non-reserved label = demand)
                var containerName = ExtractContainerName(job.Labels);

                var dispatchInfo = new JobDispatchInfo
                {
                    Job = job,
                    Run = run,
                    Registration = registration,
                    ContainerName = containerName
                };

                await DispatchJob(dispatchInfo, accessToken, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Fallback for when the Jobs API is unavailable — dispatches at the run level as ephemeral.
    /// </summary>
    private async Task DispatchRunLevelFallback(
        RunnerRegistration registration,
        QueuedWorkflowRun run,
        RunnerConfiguration config,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // Use a synthetic job ID based on run ID to avoid conflicts
        lock (_lock)
        {
            if (_dispatchedJobIds.Contains(run.Id))
                return;

            if (_activeJobs.Count >= config.MaxConcurrentJobs)
            {
                OnStatusChanged($"Max concurrent jobs reached, skipping run {run.Id}");
                return;
            }
        }

        var syntheticJob = new WorkflowJob
        {
            Id = run.Id, // Use run ID as synthetic job ID
            RunId = run.Id,
            Name = run.Name,
            Status = "queued",
            Labels = new List<string>()
        };

        var dispatchInfo = new JobDispatchInfo
        {
            Job = syntheticJob,
            Run = run,
            Registration = registration,
            ContainerName = null // Always ephemeral in fallback
        };

        await DispatchJob(dispatchInfo, accessToken, cancellationToken);
    }

    private async Task DispatchJob(
        JobDispatchInfo dispatchInfo,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var registration = dispatchInfo.Registration;
        var run = dispatchInfo.Run;
        var job = dispatchInfo.Job;
        var repoKey = $"{registration.Owner}/{registration.Repository}";
        var containerLabel = dispatchInfo.ContainerName != null
            ? $" (container: {dispatchInfo.ContainerName})"
            : "";

        _logger.LogInformation("Dispatching job {JobId} for run {RunId} on {Repo}{Container}",
            job.Id, run.Id, repoKey, containerLabel);

        try
        {
            // Build labels for JIT config — use the job's actual labels so GitHub matches them.
            // Fall back to registration labels if job has none (e.g. fallback dispatch).
            var jobLabels = job.Labels.Count > 0
                ? job.Labels
                : registration.Labels
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

            var labels = new[] { "self-hosted" }
                .Concat(jobLabels)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var runnerName = $"pks-runner-{job.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var jitConfig = await _actionsService.GenerateJitConfigAsync(
                registration.Owner, registration.Repository,
                runnerName, labels, cancellationToken);

            // Create job state
            var jobState = new RunnerJobState
            {
                Registration = registration,
                RunId = run.Id,
                WorkflowJobId = job.Id,
                ContainerName = dispatchInfo.ContainerName,
                Branch = run.HeadBranch,
                StartedAt = DateTime.UtcNow,
                Status = RunnerJobStatus.Running
            };

            lock (_lock)
            {
                _activeJobs.Add(jobState);
                _dispatchedJobIds.Add(job.Id);
            }

            // Raise JobStarted event
            JobStarted?.Invoke(this, jobState);
            OnStatusChanged($"Job started for run {run.Id} on {repoKey}{containerLabel}");

            // Fire-and-forget with tracking
            var task = ExecuteAndTrackJob(
                dispatchInfo, accessToken, jitConfig.EncodedJitConfig, jobState, cancellationToken);

            lock (_lock)
            {
                _runningTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch job {JobId} for run {RunId}", job.Id, run.Id);
            OnStatusChanged($"Failed to dispatch job for run {run.Id}: {ex.Message}");
        }
    }

    private async Task<RunnerJobState> ExecuteAndTrackJob(
        JobDispatchInfo dispatchInfo,
        string accessToken,
        string encodedJitConfig,
        RunnerJobState jobState,
        CancellationToken cancellationToken)
    {
        var run = dispatchInfo.Run;
        var job = dispatchInfo.Job;

        try
        {
            RunnerJobState result;

            if (dispatchInfo.ContainerName != null)
            {
                result = await ExecuteNamedContainerJob(
                    dispatchInfo, accessToken, encodedJitConfig, cancellationToken);
            }
            else
            {
                result = await _containerService.ExecuteJobAsync(
                    dispatchInfo.Registration, run.Id, run.HeadBranch,
                    accessToken, encodedJitConfig,
                    progress => OnStatusChanged($"Run {run.Id}: {progress}"),
                    cancellationToken);
            }

            jobState.Status = result.Status;
            jobState.ContainerId = result.ContainerId;
            jobState.ClonePath = result.ClonePath;

            lock (_lock)
            {
                _activeJobs.Remove(jobState);
                _dispatchedJobIds.Remove(job.Id);
                if (result.Status == RunnerJobStatus.Failed)
                    _totalJobsFailed++;
                else
                    _totalJobsCompleted++;
            }

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged($"Job completed for run {run.Id}: {result.Status}");

            return jobState;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job failed for run {RunId}", run.Id);
            jobState.Status = RunnerJobStatus.Failed;

            lock (_lock)
            {
                _activeJobs.Remove(jobState);
                _dispatchedJobIds.Remove(job.Id);
                _totalJobsFailed++;
            }

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged($"Job failed for run {run.Id}: {ex.Message}");

            return jobState;
        }
    }

    private async Task<RunnerJobState> ExecuteNamedContainerJob(
        JobDispatchInfo dispatchInfo,
        string accessToken,
        string encodedJitConfig,
        CancellationToken cancellationToken)
    {
        var containerName = dispatchInfo.ContainerName!;
        var run = dispatchInfo.Run;
        var job = dispatchInfo.Job;
        var registration = dispatchInfo.Registration;

        // Acquire exclusive access to this named container
        using var containerLock = await _containerPool.AcquireAsync(containerName, cancellationToken);

        var existing = _containerPool.TryGet(containerName);

        if (existing != null)
        {
            // Verify the container is still alive
            var isAlive = await _containerService.IsContainerRunningAsync(existing.ContainerId, cancellationToken);

            if (isAlive)
            {
                OnStatusChanged($"Run {run.Id}: Reusing named container '{containerName}' ({existing.ContainerId[..Math.Min(12, existing.ContainerId.Length)]})");

                return await _containerService.ExecuteJobInExistingContainerAsync(
                    registration, run.Id, job.Id, run.HeadBranch,
                    existing.ContainerId, existing.ClonePath, containerName,
                    encodedJitConfig,
                    progress => OnStatusChanged($"Run {run.Id}: {progress}"),
                    cancellationToken);
            }

            // Container is dead — remove from pool and create fresh
            _logger.LogWarning("Named container '{Name}' ({ContainerId}) is no longer running, creating fresh",
                containerName, existing.ContainerId);
            _containerPool.Remove(containerName);
            OnStatusChanged($"Run {run.Id}: Named container '{containerName}' was dead, creating fresh");
        }
        else
        {
            OnStatusChanged($"Run {run.Id}: Creating named container '{containerName}'");
        }

        // Create a new container with the name
        var result = await _containerService.ExecuteJobAsync(
            registration, run.Id, run.HeadBranch,
            accessToken, encodedJitConfig,
            progress => OnStatusChanged($"Run {run.Id}: {progress}"),
            cancellationToken,
            containerName: containerName);

        // Register in pool (labels were already set via --id-label during devcontainer up)
        if (!string.IsNullOrEmpty(result.ContainerId))
        {
            _containerPool.Register(new NamedContainerEntry
            {
                Name = containerName,
                ContainerId = result.ContainerId,
                ClonePath = result.ClonePath,
                Owner = registration.Owner,
                Repository = registration.Repository,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = DateTime.UtcNow
            });
        }

        return result;
    }

    /// <summary>
    /// Extract the container demand name from job labels.
    /// Any label that's not a reserved label (self-hosted, devcontainer-runner) is treated as a container name.
    /// </summary>
    private static string? ExtractContainerName(List<string> labels)
    {
        return labels.FirstOrDefault(l => !ReservedLabels.Contains(l));
    }

    private void CollectCompletedJobs()
    {
        lock (_lock)
        {
            _runningTasks.RemoveAll(t => t.IsCompleted);
        }
    }

    private async Task WaitForActiveJobs()
    {
        List<Task<RunnerJobState>> tasksToWait;
        lock (_lock)
        {
            tasksToWait = new List<Task<RunnerJobState>>(_runningTasks);
        }

        if (tasksToWait.Count > 0)
        {
            _logger.LogInformation("Waiting for {Count} active job(s) to complete", tasksToWait.Count);
            OnStatusChanged($"Waiting for {tasksToWait.Count} active job(s) to complete");

            try
            {
                await Task.WhenAll(tasksToWait);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for active jobs to complete");
            }
        }
    }

    private void OnStatusChanged(string message)
    {
        _logger.LogDebug("{Status}", message);
        StatusChanged?.Invoke(this, message);
    }
}
