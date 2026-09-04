using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;

namespace PKS.Infrastructure.Services.Runner;

public class RunnerDaemonService : IRunnerDaemonService
{
    private readonly IRunnerConfigurationService _configService;
    private readonly IGitHubActionsService _actionsService;
    private readonly IRunnerContainerService _containerService;
    private readonly IGitHubAuthenticationService _authService;
    private readonly IGitHubApiClient _apiClient;
    private readonly INamedContainerPool _containerPool;
    private readonly ICoolifyTokenStore _coolifyTokenStore;
    private readonly ILogger<RunnerDaemonService> _logger;

    private static readonly HashSet<string> ReservedLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "self-hosted", "devcontainer-runner"
    };

    /// <summary>
    /// Label prefixes that identify a GitHub-hosted runner image. A job carrying one of these and
    /// no <c>self-hosted</c> label is run by GitHub on its own fleet.
    /// </summary>
    private static readonly string[] HostedRunnerLabelPrefixes = { "ubuntu-", "windows-", "macos-" };

    /// <summary>
    /// How long a dispatch may sit with its job still queued on GitHub before the daemon gives up
    /// on it.
    /// </summary>
    /// <remarks>
    /// The clock runs from <see cref="RunnerJobState.ClaimWaitStartedAt"/>, not from dispatch, so it
    /// covers only the window between a runner that is up and idling and GitHub handing it work —
    /// normally seconds. Everything before that is deliberately outside it: waiting on the named
    /// container pool behind a sibling job, a cold <c>devcontainer up</c> and the runner install can
    /// each take longer than this on their own, and none of them is evidence of a stranded dispatch.
    /// It is emphatically not a cap on how long a job may run either: see
    /// <see cref="ClassifyStrandedDispatch"/> for why it can only ever fire while GitHub still
    /// reports the job as queued.
    /// </remarks>
    internal static readonly TimeSpan ClaimDeadline = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The progress line <c>IRunnerContainerService</c> emits immediately before it blocks on
    /// <c>./run.sh</c> — the moment the runner starts waiting for an assignment. Matching on the
    /// message is the only signal the daemon gets through the <c>onProgress</c> callback; if the
    /// wording in <c>RunnerContainerService</c> ever changes, the claim clock simply never starts
    /// and the deadline goes dormant, which is the safe direction to fail in.
    /// </summary>
    internal const string ClaimWaitProgressMarker = "Starting runner with JIT config";

    // State tracking
    private bool _isRunning;
    private bool _shutdownRequested;
    private DateTime? _startedAt;
    private int _totalJobsCompleted;
    private int _totalJobsFailed;
    private readonly List<RunnerJobState> _activeJobs = new();
    /// <summary>One cancellation source per dispatch, so the watchdog can end a single stranded
    /// dispatch without touching the daemon or its siblings. Keyed by <see cref="RunnerJobState.JobId"/>.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _jobCts = new();
    private readonly Dictionary<string, DateTime> _lastPollTimes = new();
    private readonly List<Task<RunnerJobState>> _runningTasks = new();
    private readonly HashSet<long> _dispatchedJobIds = new();
    /// <summary>Dispatches per job that no runner has claimed yet. Reset the moment one does.</summary>
    private readonly Dictionary<long, int> _dispatchAttempts = new();
    /// <summary>Jobs the daemon has stopped dispatching, so it stops minting runners for them.</summary>
    private readonly HashSet<long> _abandonedJobIds = new();
    private readonly Dictionary<long, string> _lastRunSummaries = new();
    private readonly HashSet<string> _reportedRunnerSwaps = new();
    private readonly object _lock = new();
    private int _consecutiveAuthFailures;
    private int _consecutiveRateLimitFailures;

    /// <summary>
    /// How many times a job may be dispatched without any runner claiming it before the daemon
    /// gives up on it.
    /// </summary>
    /// <remarks>
    /// There was no ceiling at all until 2026-08-26, and the absence was expensive. Every terminal
    /// path in <see cref="ExecuteAndTrackJob"/> removes the job from <see cref="_dispatchedJobIds"/>,
    /// so a job that fails to start is queued again on GitHub, seen again by the next poll thirty
    /// seconds later, and dispatched again — forever, until a human cancels the run. Each attempt
    /// mints a JIT registration, and those are only auto-deleted by GitHub when the runner finishes
    /// a job, which by definition never happened. Four iOS jobs looping like that for one hour on
    /// 2026-08-25 left 86 dead registrations on a single repository.
    /// </remarks>
    internal const int MaxDispatchAttempts = 3;

    /// <summary>Prefix every JIT runner this daemon mints carries, so it can recognise its own.</summary>
    internal const string RunnerNamePrefix = "pks-runner-";

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
        ICoolifyTokenStore coolifyTokenStore,
        ILogger<RunnerDaemonService> logger)
    {
        _configService = configService;
        _actionsService = actionsService;
        _containerService = containerService;
        _authService = authService;
        _apiClient = apiClient;
        _containerPool = containerPool;
        _coolifyTokenStore = coolifyTokenStore;
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

    private string? _credentialSocketPath;

    public async Task RunAsync(CancellationToken cancellationToken = default, string? credentialSocketPath = null)
    {
        _credentialSocketPath = credentialSocketPath;
        if (!string.IsNullOrEmpty(credentialSocketPath))
        {
            _logger.LogInformation("Credential socket path configured: {SocketPath}", credentialSocketPath);
        }

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
        _logger.LogInformation("Initial token loaded, expires at {ExpiresAt}", storedToken.ExpiresAt);

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
        SecretValue accessToken,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_shutdownRequested)
        {
            try
            {
                // Collect completed tasks
                CollectCompletedJobs();

                // Proactive token refresh: refresh if within 5 minutes of expiry,
                // if ExpiresAt is unknown (null), or if the token is older than 4 hours.
                var storedToken = await _authService.GetStoredTokenAsync();
                var needsRefresh = storedToken != null && (
                    storedToken.ExpiresAt == null
                    || storedToken.ExpiresAt.Value <= DateTime.UtcNow.AddMinutes(5)
                    || storedToken.CreatedAt <= DateTime.UtcNow.AddHours(-4));

                if (needsRefresh)
                {
                    var reason = storedToken!.ExpiresAt == null
                        ? "unknown expiry"
                        : storedToken.ExpiresAt.Value <= DateTime.UtcNow.AddMinutes(5)
                            ? $"expires at {storedToken.ExpiresAt}"
                            : $"token age > 4h (created {storedToken.CreatedAt:HH:mm:ss})";
                    _logger.LogInformation("Proactively refreshing token ({Reason})...", reason);
                    OnStatusChanged($"Proactively refreshing token ({reason})...");
                    var newToken = await _authService.RefreshTokenAsync();
                    if (newToken != null)
                    {
                        accessToken = newToken.AccessToken;
                        _apiClient.SetAuthenticationToken(accessToken);
                        _logger.LogInformation("Proactive refresh succeeded, new token expires at {ExpiresAt}", newToken.ExpiresAt);
                        OnStatusChanged($"Token refreshed, expires at {newToken.ExpiresAt:HH:mm:ss}");
                    }
                    else
                    {
                        _logger.LogWarning("Proactive token refresh failed, will continue with current token");
                    }
                }

                // Poll each registration
                foreach (var registration in registrations)
                {
                    if (cancellationToken.IsCancellationRequested || _shutdownRequested)
                        break;

                    await PollRegistration(registration, config, accessToken, cancellationToken);
                }

                // Free the slots held by dispatches that can no longer produce anything.
                await ReconcileActiveJobsAsync(cancellationToken);

                // Polling succeeded — reset auth failure counter
                if (_consecutiveAuthFailures > 0)
                {
                    _logger.LogInformation("Polling succeeded after {Count} auth failure(s), resetting counter", _consecutiveAuthFailures);
                    _consecutiveAuthFailures = 0;
                }
                _consecutiveRateLimitFailures = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (GitHubApiException ex) when (IsGitHubRateLimit(ex))
            {
                _consecutiveRateLimitFailures++;
                var now = DateTime.UtcNow;
                var delay = CalculateRateLimitBackoff(
                    now,
                    ex.RateLimitResetAt,
                    ex.RetryAfter,
                    _consecutiveRateLimitFailures,
                    config.PollingIntervalSeconds,
                    jitter: TimeSpan.FromSeconds(Random.Shared.Next(1, 6)));
                var resumeAt = now + delay;

                _logger.LogWarning(ex,
                    "GitHub rate limit reached. Pausing all polling for {Delay} until {ResumeAt:u}",
                    delay, resumeAt);
                OnStatusChanged(
                    $"GitHub rate limit reached — polling paused until {resumeAt:HH:mm:ss} UTC " +
                    $"({FormatBackoff(delay)})");

                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            catch (Exception ex) when (ex.Message.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase)
                                     || ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                _consecutiveAuthFailures++;

                _logger.LogWarning("Token expired (attempt {Count}), attempting refresh...", _consecutiveAuthFailures);
                OnStatusChanged($"Token expired (attempt {_consecutiveAuthFailures}), refreshing...");

                var newToken = await _authService.RefreshTokenAsync();
                if (newToken != null)
                {
                    accessToken = newToken.AccessToken;
                    _apiClient.SetAuthenticationToken(accessToken);
                    _consecutiveAuthFailures = 0;
                    _logger.LogInformation("Token refreshed, new expiry: {ExpiresAt}, expires_in: {ExpiresIn}s",
                        newToken.ExpiresAt, newToken.ExpiresAt.HasValue ? (newToken.ExpiresAt.Value - DateTime.UtcNow).TotalSeconds : -1);
                    OnStatusChanged($"Token refreshed (expires {newToken.ExpiresAt:HH:mm:ss})");
                }
                else if (_consecutiveAuthFailures >= 3)
                {
                    // Check if a refresh token even exists — if not, stop the daemon
                    var storedToken = await _authService.GetStoredTokenAsync();
                    if (storedToken == null || !storedToken.RefreshToken.HasValue)
                    {
                        _logger.LogError(
                            "No refresh token available and access token expired. Stopping daemon — " +
                            "re-authenticate with 'pks github runner register'.");
                        OnStatusChanged("Auth failed: no refresh token available, stopping daemon — re-authenticate with 'pks github runner register'");
                        break;
                    }

                    _logger.LogError("Token refresh failed {Count} consecutive times. Re-run 'pks github runner register' to re-authenticate.",
                        _consecutiveAuthFailures);
                    OnStatusChanged($"Auth failing repeatedly ({_consecutiveAuthFailures}x) — re-authenticate with 'pks github runner register'");

                    // Wait longer before retrying to avoid hammering the API (10x the polling interval)
                    var backoffSeconds = config.PollingIntervalSeconds * 10;
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
                else
                {
                    _logger.LogError("Token refresh failed (attempt {Count}). Will retry.", _consecutiveAuthFailures);
                    OnStatusChanged($"Token refresh failed (attempt {_consecutiveAuthFailures}), will retry");
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

    internal static bool IsGitHubRateLimit(GitHubApiException exception) =>
        exception.IsRateLimit
        || exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests
        || exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    internal static TimeSpan CalculateRateLimitBackoff(
        DateTime now,
        DateTime? resetAt,
        TimeSpan? retryAfter,
        int consecutiveFailures,
        int pollingIntervalSeconds,
        TimeSpan jitter)
    {
        var authoritative = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
            ? retryAfter.Value
            : resetAt.HasValue && resetAt.Value > now
                ? resetAt.Value - now
                : TimeSpan.Zero;

        TimeSpan delay;
        if (authoritative > TimeSpan.Zero)
        {
            delay = authoritative;
        }
        else
        {
            var baseSeconds = Math.Max(30, pollingIntervalSeconds);
            var exponent = Math.Clamp(consecutiveFailures - 1, 0, 10);
            delay = TimeSpan.FromSeconds(baseSeconds * Math.Pow(2, exponent));
        }

        var maximum = TimeSpan.FromHours(1);
        if (delay > maximum) delay = maximum;
        if (jitter > TimeSpan.Zero) delay += jitter;
        return delay;
    }

    private static string FormatBackoff(TimeSpan delay) =>
        delay.TotalMinutes >= 1
            ? $"{Math.Ceiling(delay.TotalMinutes):0} min"
            : $"{Math.Ceiling(delay.TotalSeconds):0} sec";

    private async Task PollRegistration(
        RunnerRegistration registration,
        RunnerConfiguration config,
        SecretValue accessToken,
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

            // One line per run, and only when the picture actually changed. The old code logged
            // every queued job on every poll, which reprinted the same line every 30 seconds and
            // pushed everything else out of the 12-entry activity window.
            ReportRunState(repoKey, run, jobs);

            var queuedJobs = jobs.Where(j =>
                string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)
                && IsServableJob(j)).ToList();

            foreach (var hosted in jobs.Where(j =>
                string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)
                && !IsServableJob(j)))
            {
                _logger.LogDebug(
                    "Ignoring GitHub-hosted job {JobId} '{Name}' in run {RunId} (labels: {Labels})",
                    hosted.Id, hosted.Name, run.Id, string.Join(",", hosted.Labels));
            }

            foreach (var job in queuedJobs)
            {
                if (_shutdownRequested)
                    break;

                // Skip if already dispatched
                lock (_lock)
                {
                    if (_dispatchedJobIds.Contains(job.Id) || _abandonedJobIds.Contains(job.Id))
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
    /// Emits one activity line per run describing every job in it — but only when that description
    /// changed since the last poll, so a run that sits still stays silent instead of reprinting.
    /// Also reconciles which job GitHub actually gave each of our JIT runners.
    /// </summary>
    private void ReportRunState(string repoKey, QueuedWorkflowRun run, List<WorkflowJob> jobs)
    {
        if (jobs.Count == 0)
            return;

        // Jobs that finish while the daemon is watching drop their bookkeeping here, so a daemon
        // running for weeks does not accumulate an entry per job it ever saw. It is not airtight —
        // a run cancelled outright leaves the queued list without any of its jobs ever being polled
        // as completed — but the leftovers are a few bytes each, job IDs never come round again,
        // and a restart clears them.
        lock (_lock)
        {
            foreach (var finished in jobs.Where(j =>
                string.Equals(j.Status, "completed", StringComparison.OrdinalIgnoreCase)))
            {
                _dispatchAttempts.Remove(finished.Id);
                _abandonedJobIds.Remove(finished.Id);
            }
        }

        ReconcileRunnerAssignments(repoKey, jobs);

        var parts = jobs.Select(j =>
        {
            var age = j.StartedAt.HasValue
                ? $" {FormatAge(DateTime.UtcNow - j.StartedAt.Value.ToUniversalTime())}"
                : "";
            var state = string.Equals(j.Status, "completed", StringComparison.OrdinalIgnoreCase)
                ? j.Conclusion ?? "completed"
                : j.Status;
            var on = string.IsNullOrEmpty(j.RunnerName) ? "" : $" on {j.RunnerName}";
            var env = string.IsNullOrEmpty(j.Environment) ? "" : $" env={j.Environment}";
            return $"{j.Name} {state}{age}{on}{env}";
        });

        var summary = $"{repoKey} #{run.Id}: {string.Join(" | ", parts)}";

        lock (_lock)
        {
            if (_lastRunSummaries.TryGetValue(run.Id, out var previous) && previous == summary)
                return;

            // Bounded: a daemon that runs for weeks must not accumulate a summary per run forever.
            if (_lastRunSummaries.Count > 200)
                _lastRunSummaries.Clear();

            _lastRunSummaries[run.Id] = summary;
        }

        OnStatusChanged(summary);
    }

    /// <summary>
    /// A JIT runner is not bound to the job it was generated for: GitHub hands it the first queued
    /// job whose labels are a subset of the runner's. When that happens the daemon was tracking the
    /// wrong job and left the real one marked as dispatched, so it starved. Detect the swap, retarget
    /// the tracked state, and release the job that never got a runner so it can be dispatched again.
    /// </summary>
    private void ReconcileRunnerAssignments(string repoKey, List<WorkflowJob> jobs)
    {
        foreach (var job in jobs)
        {
            if (string.IsNullOrEmpty(job.RunnerName))
                continue;

            // A runner took it, so whatever attempts came before were not wasted after all.
            lock (_lock)
            {
                _dispatchAttempts.Remove(job.Id);
            }

            string? note = null;

            lock (_lock)
            {
                var tracked = _activeJobs.FirstOrDefault(a =>
                    string.Equals(a.RunnerName, job.RunnerName, StringComparison.OrdinalIgnoreCase));

                if (tracked == null || tracked.WorkflowJobId == job.Id)
                    continue;

                var strandedJobId = tracked.WorkflowJobId;
                var strandedName = tracked.WorkflowJobName;

                tracked.WorkflowJobId = job.Id;
                tracked.WorkflowJobName = job.Name;

                // The job we thought this runner would take never got one — let it be dispatched again.
                if (strandedJobId.HasValue)
                {
                    _dispatchedJobIds.Remove(strandedJobId.Value);

                    // And do not charge it for the attempt. That dispatch produced a working runner,
                    // it just went to a sibling job; counting it would let three swaps in a row
                    // abandon a job the daemon is handling correctly.
                    _dispatchAttempts.Remove(strandedJobId.Value);
                }
                _dispatchedJobIds.Add(job.Id);

                if (_reportedRunnerSwaps.Add(job.RunnerName))
                {
                    note = $"{repoKey}: GitHub gave runner {job.RunnerName} to '{job.Name}', " +
                           $"not '{strandedName}' — requeuing '{strandedName}' for a new runner";
                }
            }

            if (note != null)
            {
                _logger.LogInformation("{Note}", note);
                OnStatusChanged(note);
            }
        }
    }

    /// <summary>
    /// Whether this daemon should mint a runner for a queued job at all.
    /// </summary>
    /// <remarks>
    /// There was no such check until 2026-08-29, and every queued job in a registered repository got
    /// a runner — including the <c>ubuntu-latest</c> and <c>windows-latest</c> jobs GitHub runs on
    /// its own fleet. Such a dispatch can never be claimed, because GitHub has already given the job
    /// to a hosted runner, so <c>run.sh</c> waits for work that never arrives and the dispatch never
    /// returns. Three of those held every concurrency slot for thirteen hours on 2026-08-28 while
    /// real self-hosted jobs queued behind them.
    /// A job carrying a custom label and no <c>self-hosted</c> is still ours to take: GitHub matches
    /// a job to any runner whose labels are a superset of its own, and our JIT runners are minted
    /// with the job's own labels.
    /// </remarks>
    internal static bool IsServableJob(WorkflowJob job)
    {
        if (job.Labels.Any(l => string.Equals(l, "self-hosted", StringComparison.OrdinalIgnoreCase)))
            return true;

        return !job.Labels.Any(l =>
            HostedRunnerLabelPrefixes.Any(p => l.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Asks GitHub what became of every job we believe we are running, and abandons the dispatches
    /// that can no longer produce anything.
    /// </summary>
    /// <remarks>
    /// Nothing else does this. <see cref="ReportRunState"/> only ever sees runs that are still
    /// queued, so a dispatch whose run has left that list is never looked at again — and the process
    /// it is waiting on, <c>./run.sh</c> inside the container, blocks forever when no job is ever
    /// assigned to it. <see cref="_activeJobs"/> is memory only, so without this pass the slot is
    /// gone until someone restarts the daemon.
    /// </remarks>
    private async Task ReconcileActiveJobsAsync(CancellationToken cancellationToken)
    {
        List<RunnerJobState> tracked;
        lock (_lock)
        {
            tracked = _activeJobs.Where(j => j.WorkflowJobId.HasValue).ToList();
        }

        if (tracked.Count == 0)
            return;

        foreach (var group in tracked.GroupBy(j =>
            (j.Registration.Owner, j.Registration.Repository, j.RunId)))
        {
            if (cancellationToken.IsCancellationRequested || _shutdownRequested)
                return;

            List<WorkflowJob> jobs;
            try
            {
                jobs = await _actionsService.GetJobsForRunAsync(
                    group.Key.Owner, group.Key.Repository, group.Key.RunId, cancellationToken);
            }
            catch (Exception ex)
            {
                // An API hiccup must never read as "the job is gone" — leave the dispatch standing.
                _logger.LogDebug(ex,
                    "Could not reconcile active jobs for run {RunId}", group.Key.RunId);
                continue;
            }

            var repoKey = $"{group.Key.Owner}/{group.Key.Repository}";

            // Both retargets have to happen here, not only in the poller. GetQueuedRunsAsync asks
            // GitHub for runs with status=queued, and a run leaves that list the moment its first
            // job starts — which is exactly the moment a swap becomes visible. So ReportRunState
            // never sees the run again and the swap detection it calls could not fire for the case
            // it was written for. This pass is driven by our own tracked dispatches instead, so it
            // still sees the run.
            ReconcileRunnerAssignments(repoKey, jobs);
            RetargetIdleDispatches(repoKey, group.ToList(), jobs);

            foreach (var state in group)
            {
                var reason = ClassifyStrandedDispatch(state, jobs, DateTime.UtcNow, ClaimDeadline);
                if (reason != null)
                    CancelDispatch(state, reason);
            }
        }
    }

    /// <summary>
    /// Gives a dispatch whose job was taken by one of our own runners something else to do, instead
    /// of letting it be abandoned as "another runner claimed it".
    /// </summary>
    /// <remarks>
    /// A JIT runner cannot be bound to a job — <c>generate-jitconfig</c> takes labels and nothing
    /// else — so when two jobs in a run carry the same labels, GitHub is free to give runner A's job
    /// to runner B. <see cref="ReconcileRunnerAssignments"/> fixes up the runner that won the race;
    /// this fixes up the one that lost it. Without this the loser's container is thrown away while
    /// its sibling job is still queued, and the daemon pays for a whole new dispatch — on
    /// 2026-08-30 that cost the 'fast' job five minutes and a second container for nothing.
    /// <para>
    /// The subset check is load-bearing: retargeting a runner to a job whose labels it does not
    /// carry would leave the job waiting for a runner GitHub will never give it, which is a quieter
    /// version of the starvation this method exists to end.
    /// </para>
    /// </remarks>
    private void RetargetIdleDispatches(
        string repoKey, List<RunnerJobState> tracked, List<WorkflowJob> jobs)
    {
        var notes = new List<string>();

        lock (_lock)
        {
            var unavailable = new HashSet<long>(_dispatchedJobIds);
            unavailable.UnionWith(_abandonedJobIds);

            foreach (var state in tracked)
            {
                var candidate = FindRetargetCandidate(state, jobs, unavailable);
                if (candidate == null)
                    continue;

                var mine = jobs.First(j => j.Id == state.WorkflowJobId);
                var stranded = state.WorkflowJobName;
                state.WorkflowJobId = candidate.Id;
                state.WorkflowJobName = candidate.Name;
                _dispatchedJobIds.Add(candidate.Id);

                // The dispatch is alive and about to serve this job, so it must not carry a strike
                // for the job it was originally minted for.
                _dispatchAttempts.Remove(candidate.Id);

                // Two dispatches in the same pass must not both be given the same job.
                unavailable.Add(candidate.Id);

                notes.Add(
                    $"{repoKey}: '{stranded}' went to {mine.RunnerName}, so runner {state.RunnerName} " +
                    $"takes '{candidate.Name}' instead — no new container needed");
            }
        }

        foreach (var note in notes)
        {
            _logger.LogInformation("{Note}", note);
            OnStatusChanged(note);
        }
    }

    /// <summary>
    /// The job an idle dispatch should be retargeted to, or null when it should be left alone.
    /// </summary>
    /// <param name="unavailableJobIds">
    /// Jobs another dispatch is already tracking, or that have been given up on.
    /// </param>
    internal static WorkflowJob? FindRetargetCandidate(
        RunnerJobState state, List<WorkflowJob> jobs, ISet<long> unavailableJobIds)
    {
        if (string.IsNullOrEmpty(state.RunnerName))
            return null;

        // Our runner is busy somewhere in this run — leave it alone.
        if (jobs.Any(j => string.Equals(j.RunnerName, state.RunnerName, StringComparison.OrdinalIgnoreCase)))
            return null;

        // Only a job lost to one of our own runners frees this dispatch up. A job claimed by
        // anything else — a hosted runner, someone else's box — says nothing about what our idle
        // runner may take, and the existing stranding rules should decide that case.
        var mine = jobs.FirstOrDefault(j => j.Id == state.WorkflowJobId);
        if (mine == null || !IsOurRunnerName(mine.RunnerName))
            return null;

        return jobs.FirstOrDefault(j =>
            string.Equals(j.Status, "queued", StringComparison.OrdinalIgnoreCase)
            && IsServableJob(j)
            && !unavailableJobIds.Contains(j.Id)
            && CanServe(state.RunnerLabels, j.Labels));
    }

    /// <summary>Whether a runner name is one this daemon minted.</summary>
    internal static bool IsOurRunnerName(string? runnerName) =>
        !string.IsNullOrEmpty(runnerName)
        && runnerName.StartsWith(RunnerNamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GitHub's own matching rule: a runner may take a job when it carries every label the job asks
    /// for. Extra labels on the runner do not disqualify it.
    /// </summary>
    internal static bool CanServe(IReadOnlyList<string> runnerLabels, IReadOnlyList<string> jobLabels)
    {
        if (jobLabels.Count == 0)
            return false;

        return jobLabels.All(needed =>
            runnerLabels.Any(have => string.Equals(have, needed, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Decides whether a dispatch has been stranded, given what GitHub says about the jobs in its
    /// run. Returns null when the dispatch is healthy or the answer is not yet knowable.
    /// </summary>
    internal static string? ClassifyStrandedDispatch(
        RunnerJobState state, List<WorkflowJob> jobs, DateTime utcNow, TimeSpan claimDeadline)
    {
        // A JIT runner is not bound to the job it was minted for, so before reading anything into
        // that job's state, check whether our runner is off running a sibling in the same run. If it
        // is, the container is doing real work and must not be touched however long it takes — this
        // is what keeps every branch below from being able to kill a live job.
        if (!string.IsNullOrEmpty(state.RunnerName) && jobs.Any(j =>
                string.Equals(j.RunnerName, state.RunnerName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var job = jobs.FirstOrDefault(j => j.Id == state.WorkflowJobId);
        if (job == null)
            return null;

        if (string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return $"GitHub already finished it ({job.Conclusion ?? "completed"})";

        if (string.Equals(job.Status, "in_progress", StringComparison.OrdinalIgnoreCase))
        {
            // Claimed by something that is not one of our runners for this run.
            return string.IsNullOrEmpty(job.RunnerName)
                ? null
                : $"another runner claimed it ({job.RunnerName})";
        }

        // Still queued, and the check above has ruled out our runner working anywhere in this run.
        // This is the only state in which a clock is allowed to end a dispatch — and only once our
        // runner is actually online and idle. While the dispatch is still queuing for the container
        // pool or building, there is nothing that could have claimed the job yet, so there is no
        // deadline to have missed.
        if (state.ClaimWaitStartedAt is not { } waitingSince)
            return null;

        return utcNow - waitingSince > claimDeadline
            ? $"no runner claimed it within {claimDeadline.TotalMinutes:0} min of coming online"
            : null;
    }

    /// <summary>
    /// Ends one stranded dispatch. Cancelling unblocks the <c>docker exec ./run.sh</c> await, which
    /// lands in the catch in <see cref="ExecuteAndTrackJob"/>: the job leaves
    /// <see cref="_activeJobs"/>, the slot is freed and the unclaimed JIT registration is deleted.
    /// The container is left to the reaper, which finds it by its <c>pks.runner.name</c> label.
    /// </summary>
    private void CancelDispatch(RunnerJobState state, string reason)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _jobCts.TryGetValue(state.JobId, out cts);
        }

        if (cts == null || cts.IsCancellationRequested)
            return;

        var note = $"Run {state.RunId} '{state.WorkflowJobName}': abandoning dispatch — {reason}";
        _logger.LogWarning("{Note}", note);
        OnStatusChanged(note);

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced a dispatch that finished on its own; nothing to abandon.
        }
    }

    private void DisposeDispatchCancellation(RunnerJobState state)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_jobCts.Remove(state.JobId, out cts))
                return;
        }

        cts?.Dispose();
    }

    /// <summary>
    /// Removes a JIT runner registration that nothing else will ever remove.
    /// </summary>
    /// <remarks>
    /// GitHub deletes an ephemeral runner once it finishes a job, which is why a dispatch that works
    /// needs no cleanup at all. A registration whose runner never claimed a job is never deleted and
    /// sits in the repository's runner list as an offline entry indefinitely.
    /// <para>
    /// Best-effort on purpose. Every caller is already on a failure path, and failing to tidy up
    /// must not replace the error that got us there.
    /// </para>
    /// </remarks>
    private async Task TryDeleteRunnerAsync(RunnerRegistration registration, int runnerId)
    {
        try
        {
            // Not the caller's token: it may already be cancelled, and this is one short call.
            var deleted = await _actionsService.DeleteRunnerAsync(
                registration.Owner, registration.Repository, runnerId, CancellationToken.None);

            // Checked rather than assumed, because the API client reports a refusal by returning
            // false rather than by throwing — and a 404 is the ordinary answer when the runner did
            // claim a job after all and GitHub has already reaped it.
            if (deleted)
            {
                _logger.LogInformation(
                    "Removed unclaimed JIT runner {RunnerId} from {Owner}/{Repo}",
                    runnerId, registration.Owner, registration.Repository);
            }
            else
            {
                _logger.LogDebug(
                    "JIT runner {RunnerId} was not removed from {Owner}/{Repo}; most likely already gone",
                    runnerId, registration.Owner, registration.Repository);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Could not remove unclaimed JIT runner {RunnerId} from {Owner}/{Repo}",
                runnerId, registration.Owner, registration.Repository);
        }
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        return age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h{age.Minutes:D2}m"
            : $"{(int)age.TotalMinutes}m";
    }

    /// <summary>
    /// Fallback for when the Jobs API is unavailable — dispatches at the run level as ephemeral.
    /// </summary>
    private async Task DispatchRunLevelFallback(
        RunnerRegistration registration,
        QueuedWorkflowRun run,
        RunnerConfiguration config,
        SecretValue accessToken,
        CancellationToken cancellationToken)
    {
        // Use a synthetic job ID based on run ID to avoid conflicts
        lock (_lock)
        {
            if (_dispatchedJobIds.Contains(run.Id) || _abandonedJobIds.Contains(run.Id))
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
        SecretValue accessToken,
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

        int attempt;
        lock (_lock)
        {
            if (_abandonedJobIds.Contains(job.Id))
                return;

            attempt = _dispatchAttempts.TryGetValue(job.Id, out var previous) ? previous + 1 : 1;
            _dispatchAttempts[job.Id] = attempt;

            if (attempt > MaxDispatchAttempts)
                _abandonedJobIds.Add(job.Id);
        }

        if (attempt > MaxDispatchAttempts)
        {
            _logger.LogError(
                "Giving up on job {JobId} for run {RunId} on {Repo}: {Attempts} dispatches and no runner claimed it",
                job.Id, run.Id, repoKey, MaxDispatchAttempts);
            OnStatusChanged(
                $"Gave up on '{job.Name}' ({repoKey} #{run.Id}) after {MaxDispatchAttempts} dispatches " +
                "no runner claimed. Fix the runner or cancel the run; the daemon will not retry it.");
            return;
        }

        // Held so a dispatch that throws after GitHub minted the registration can take it back out
        // again. Nothing else ever will: see MaxDispatchAttempts.
        int? jitRunnerId = null;

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

            var runnerName = $"{RunnerNamePrefix}{job.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var jitConfig = await _actionsService.GenerateJitConfigAsync(
                registration.Owner, registration.Repository,
                runnerName, labels, cancellationToken);

            jitRunnerId = jitConfig.RunnerId;

            // Create job state
            var jobState = new RunnerJobState
            {
                Registration = registration,
                RunId = run.Id,
                WorkflowJobId = job.Id,
                WorkflowJobName = job.Name,
                RunnerName = runnerName,
                JitRunnerId = jitConfig.RunnerId,
                ContainerName = dispatchInfo.ContainerName,
                RunnerLabels = labels,
                Branch = run.HeadBranch,
                StartedAt = DateTime.UtcNow,
                Status = RunnerJobStatus.Running
            };

            var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_lock)
            {
                _activeJobs.Add(jobState);
                _dispatchedJobIds.Add(job.Id);
                _jobCts[jobState.JobId] = dispatchCts;
            }

            // Raise JobStarted event
            JobStarted?.Invoke(this, jobState);
            OnStatusChanged(
                $"Dispatched runner {runnerName} for '{job.Name}' " +
                $"({repoKey} #{run.Id}, labels=[{string.Join(",", labels)}]{containerLabel})");

            // Fire-and-forget with tracking
            var task = ExecuteAndTrackJob(
                dispatchInfo, accessToken, jitConfig.EncodedJitConfig, jobState, dispatchCts.Token);

            lock (_lock)
            {
                _runningTasks.Add(task);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch job {JobId} for run {RunId}", job.Id, run.Id);
            OnStatusChanged($"Failed to dispatch job for run {run.Id}: {ex.Message}");

            if (jitRunnerId.HasValue)
                await TryDeleteRunnerAsync(registration, jitRunnerId.Value);
        }
    }

    /// <summary>
    /// Records one progress line from the container service against the dispatch it belongs to and
    /// forwards it to the status feed. Starts <see cref="RunnerJobState.ClaimWaitStartedAt"/> the
    /// first time the runner reports that it is up and waiting for work.
    /// </summary>
    private void NoteProgress(RunnerJobState jobState, string progress, string note)
    {
        jobState.Detail = progress;

        if (jobState.ClaimWaitStartedAt == null
            && progress.Contains(ClaimWaitProgressMarker, StringComparison.OrdinalIgnoreCase))
        {
            jobState.ClaimWaitStartedAt = DateTime.UtcNow;
        }

        OnStatusChanged(note);
    }

    private async Task<RunnerJobState> ExecuteAndTrackJob(
        JobDispatchInfo dispatchInfo,
        SecretValue accessToken,
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
                    dispatchInfo, accessToken, encodedJitConfig, jobState, cancellationToken);
            }
            else
            {
                result = await _containerService.ExecuteJobAsync(
                    dispatchInfo.Registration, run.Id, run.HeadBranch,
                    // Revealed here because IRunnerContainerService puts the token into the
                    // container's git credential environment; the daemon itself never holds a string.
                    accessToken.Reveal()!, encodedJitConfig,
                    progress => NoteProgress(
                        jobState, progress,
                        $"Run {run.Id} '{jobState.WorkflowJobName}': {progress}"),
                    cancellationToken,
                    credentialSocketPath: _credentialSocketPath,
                    environment: job.Environment,
                    lookupBranch: run.GetCoolifyLookupBranch());
            }

            jobState.ContainerId = result.ContainerId;
            jobState.ClonePath = result.ClonePath;
            jobState.Detail = null;

            // The container exiting cleanly only means the runner process shut down — the job itself
            // may still have failed on GitHub. Ask GitHub for the conclusion before counting it.
            var (finalStatus, conclusionNote) = await ResolveFinalStatusAsync(
                dispatchInfo.Registration, run.Id, jobState, result.Status, cancellationToken);

            jobState.Status = finalStatus;

            lock (_lock)
            {
                _activeJobs.Remove(jobState);
                _dispatchedJobIds.Remove(job.Id);
                if (jobState.WorkflowJobId.HasValue)
                    _dispatchedJobIds.Remove(jobState.WorkflowJobId.Value);

                // Only a run that ended well clears the attempt counter. A container that starts and
                // exits without the runner ever claiming the job comes back here, not through the
                // catch block, and the job is still queued on GitHub — so clearing the counter on
                // every outcome would leave the same unbounded loop the ceiling exists to stop,
                // just reached through the other door. A job that did run and genuinely failed is
                // completed on GitHub, never polled again, and has its counter dropped by the
                // finished-job sweep in PollRegistration instead.
                if (finalStatus == RunnerJobStatus.Failed)
                {
                    _totalJobsFailed++;
                }
                else
                {
                    _totalJobsCompleted++;
                    _dispatchAttempts.Remove(job.Id);
                    if (jobState.WorkflowJobId.HasValue)
                        _dispatchAttempts.Remove(jobState.WorkflowJobId.Value);
                }
            }

            // Same reasoning for the registration: GitHub reaps an ephemeral runner itself once it
            // finishes a job, so this only has anything to delete when no runner ever claimed one.
            if (finalStatus == RunnerJobStatus.Failed && jobState.JitRunnerId.HasValue)
                await TryDeleteRunnerAsync(dispatchInfo.Registration, jobState.JitRunnerId.Value);

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged(
                $"Run {run.Id} '{jobState.WorkflowJobName}' finished: {finalStatus}{conclusionNote}");

            // Clean up token store entries for completed job
            _coolifyTokenStore?.Remove(job.Id.ToString());

            return jobState;
        }
        catch (OperationCanceledException) when (!_shutdownRequested)
        {
            // CancelDispatch got here first: the dispatch was called off before it ran anything,
            // because its job went to another of our runners or GitHub had already finished it.
            // Nothing was attempted and nothing broke, so this must not read as a failed job — it
            // used to print "Job Failed: <repo> run #<id>" for a run that was green on GitHub, and
            // cost a morning of diagnosis on 2026-08-30.
            jobState.Status = RunnerJobStatus.Abandoned;

            lock (_lock)
            {
                _activeJobs.Remove(jobState);
                _dispatchedJobIds.Remove(job.Id);
                if (jobState.WorkflowJobId.HasValue)
                    _dispatchedJobIds.Remove(jobState.WorkflowJobId.Value);
            }

            if (jobState.JitRunnerId.HasValue)
                await TryDeleteRunnerAsync(dispatchInfo.Registration, jobState.JitRunnerId.Value);

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged(
                $"Run {run.Id} '{jobState.WorkflowJobName}': dispatch abandoned — the GitHub job " +
                "itself is unaffected");

            _coolifyTokenStore?.Remove(job.Id.ToString());

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
                if (jobState.WorkflowJobId.HasValue)
                    _dispatchedJobIds.Remove(jobState.WorkflowJobId.Value);
                _totalJobsFailed++;
            }

            // The counter is deliberately left standing: this is exactly the attempt
            // MaxDispatchAttempts is counting.
            if (jobState.JitRunnerId.HasValue)
                await TryDeleteRunnerAsync(dispatchInfo.Registration, jobState.JitRunnerId.Value);

            JobCompleted?.Invoke(this, jobState);
            OnStatusChanged($"Run {run.Id} '{jobState.WorkflowJobName}' failed: {ex.Message}");

            // Clean up token store entries for failed job
            _coolifyTokenStore?.Remove(job.Id.ToString());

            return jobState;
        }
        finally
        {
            DisposeDispatchCancellation(jobState);
        }
    }

    /// <summary>
    /// Asks GitHub what the job actually concluded. The container exit code only tells us the runner
    /// process shut down, so a red job on GitHub used to be counted as Done. Falls back to the
    /// container result when GitHub has not settled the job yet or the API call fails.
    /// </summary>
    private async Task<(RunnerJobStatus Status, string Note)> ResolveFinalStatusAsync(
        RunnerRegistration registration,
        long runId,
        RunnerJobState jobState,
        RunnerJobStatus containerStatus,
        CancellationToken cancellationToken)
    {
        if (!jobState.WorkflowJobId.HasValue)
            return (containerStatus, "");

        try
        {
            var jobs = await _actionsService.GetJobsForRunAsync(
                registration.Owner, registration.Repository, runId, cancellationToken);

            var job = jobs.FirstOrDefault(j => j.Id == jobState.WorkflowJobId.Value);
            if (job == null || string.IsNullOrEmpty(job.Conclusion))
                return (containerStatus, "");

            var status = job.Conclusion.ToLowerInvariant() switch
            {
                "failure" or "timed_out" or "startup_failure" => RunnerJobStatus.Failed,
                _ => RunnerJobStatus.Completed
            };

            return (status, $" (GitHub: {job.Conclusion})");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read GitHub conclusion for job {JobId}", jobState.WorkflowJobId);
            return (containerStatus, "");
        }
    }

    private async Task<RunnerJobState> ExecuteNamedContainerJob(
        JobDispatchInfo dispatchInfo,
        SecretValue accessToken,
        string encodedJitConfig,
        RunnerJobState jobState,
        CancellationToken cancellationToken)
    {
        var containerName = dispatchInfo.ContainerName!;
        var run = dispatchInfo.Run;
        var job = dispatchInfo.Job;
        var registration = dispatchInfo.Registration;

        // Acquire exclusive access to this named container. Scoped by repository: the name is
        // just the runner label from `runs-on`, and several repositories reuse the same label,
        // so a name-only pool lends one repository's devcontainer out to another.
        using var containerLock = await _containerPool.AcquireAsync(
            registration.Owner, registration.Repository, containerName, cancellationToken);

        var existing = _containerPool.TryGet(registration.Owner, registration.Repository, containerName);

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
                    progress => NoteProgress(jobState, progress, $"Run {run.Id}: {progress}"),
                    cancellationToken,
                    credentialSocketPath: _credentialSocketPath,
                    lookupBranch: run.GetCoolifyLookupBranch());
            }

            // Container is dead — remove from pool and create fresh
            _logger.LogWarning("Named container '{Name}' ({ContainerId}) is no longer running, creating fresh",
                containerName, existing.ContainerId);
            _containerPool.Remove(registration.Owner, registration.Repository, containerName);
            OnStatusChanged($"Run {run.Id}: Named container '{containerName}' was dead, creating fresh");
        }
        else
        {
            OnStatusChanged($"Run {run.Id}: Creating named container '{containerName}'");
        }

        // Create a new container with the name
        var result = await _containerService.ExecuteJobAsync(
            registration, run.Id, run.HeadBranch,
            accessToken.Reveal()!, encodedJitConfig,
            progress => NoteProgress(jobState, progress, $"Run {run.Id}: {progress}"),
            cancellationToken,
            containerName: containerName,
            credentialSocketPath: _credentialSocketPath,
            environment: job.Environment,
            lookupBranch: run.GetCoolifyLookupBranch());

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
