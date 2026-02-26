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
    private readonly ILogger<RunnerDaemonService> _logger;

    // State tracking
    private bool _isRunning;
    private bool _shutdownRequested;
    private DateTime? _startedAt;
    private int _totalJobsCompleted;
    private int _totalJobsFailed;
    private readonly List<RunnerJobState> _activeJobs = new();
    private readonly Dictionary<string, DateTime> _lastPollTimes = new();
    private readonly List<Task<RunnerJobState>> _runningTasks = new();
    private readonly HashSet<long> _dispatchedRunIds = new();
    private readonly object _lock = new();

    public event EventHandler<RunnerJobState>? JobStarted;
    public event EventHandler<RunnerJobState>? JobCompleted;
    public event EventHandler<string>? StatusChanged;

    public RunnerDaemonService(
        IRunnerConfigurationService configService,
        IGitHubActionsService actionsService,
        IRunnerContainerService containerService,
        IGitHubAuthenticationService authService,
        IGitHubApiClient apiClient,
        ILogger<RunnerDaemonService> logger)
    {
        _configService = configService;
        _actionsService = actionsService;
        _containerService = containerService;
        _authService = authService;
        _apiClient = apiClient;
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
                TotalJobsFailed = _totalJobsFailed
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
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

        // All queued runs for this registered repo are candidates.
        // Label matching happens at the GitHub Actions level (runs-on), so we dispatch
        // any queued run and let the JIT runner pick up only matching jobs.
        var matchingRuns = queuedRuns
            .Where(r => !_dispatchedRunIds.Contains(r.Id))
            .ToList();

        foreach (var run in matchingRuns)
        {
            if (_shutdownRequested)
                break;

            // Check concurrency limit
            int activeCount;
            lock (_lock)
            {
                activeCount = _activeJobs.Count;
            }

            if (activeCount >= config.MaxConcurrentJobs)
            {
                _logger.LogDebug(
                    "Max concurrent jobs ({Max}) reached, skipping run {RunId}",
                    config.MaxConcurrentJobs, run.Id);
                OnStatusChanged($"Max concurrent jobs reached, skipping run {run.Id}");
                break;
            }

            await DispatchJob(registration, run, accessToken, cancellationToken);
        }
    }

    private async Task DispatchJob(
        RunnerRegistration registration,
        QueuedWorkflowRun run,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var repoKey = $"{registration.Owner}/{registration.Repository}";
        _logger.LogInformation("Dispatching job for run {RunId} on {Repo}", run.Id, repoKey);

        try
        {
            // Generate JIT config — always include "self-hosted" label since
            // workflows use runs-on: [self-hosted, devcontainer-runner]
            var userLabels = registration.Labels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var labels = new[] { "self-hosted" }
                .Concat(userLabels)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var runnerName = $"pks-runner-{run.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var jitConfig = await _actionsService.GenerateJitConfigAsync(
                registration.Owner, registration.Repository,
                runnerName, labels, cancellationToken);

            // Create job state
            var jobState = new RunnerJobState
            {
                Registration = registration,
                RunId = run.Id,
                Branch = run.HeadBranch,
                StartedAt = DateTime.UtcNow,
                Status = RunnerJobStatus.Running
            };

            lock (_lock)
            {
                _activeJobs.Add(jobState);
                _dispatchedRunIds.Add(run.Id);
            }

            // Raise JobStarted event
            JobStarted?.Invoke(this, jobState);
            OnStatusChanged($"Job started for run {run.Id} on {repoKey}");

            // Fire-and-forget with tracking
            var task = ExecuteAndTrackJob(
                registration, run, accessToken, jitConfig.EncodedJitConfig, jobState, cancellationToken);

            lock (_lock)
            {
                _runningTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch job for run {RunId}", run.Id);
            OnStatusChanged($"Failed to dispatch job for run {run.Id}: {ex.Message}");
        }
    }

    private async Task<RunnerJobState> ExecuteAndTrackJob(
        RunnerRegistration registration,
        QueuedWorkflowRun run,
        string accessToken,
        string encodedJitConfig,
        RunnerJobState jobState,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _containerService.ExecuteJobAsync(
                registration, run.Id, run.HeadBranch,
                accessToken, encodedJitConfig,
                progress => OnStatusChanged($"Run {run.Id}: {progress}"),
                cancellationToken);

            jobState.Status = result.Status;
            jobState.ContainerId = result.ContainerId;
            jobState.ClonePath = result.ClonePath;

            lock (_lock)
            {
                _activeJobs.Remove(jobState);
                // Remove from dispatched so the same run can be re-dispatched
                // if it has more queued jobs (e.g. multi-job workflows)
                _dispatchedRunIds.Remove(run.Id);
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
                _dispatchedRunIds.Remove(run.Id);
                _totalJobsFailed++;
            }

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged($"Job failed for run {run.Id}: {ex.Message}");

            return jobState;
        }
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
