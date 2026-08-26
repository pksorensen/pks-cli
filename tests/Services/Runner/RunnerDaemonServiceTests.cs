using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;
using PKS.Infrastructure.Services.Security;
using System.Net;

namespace PKS.CLI.Tests.Services.Runner;

public class RunnerDaemonServiceTests : IDisposable
{
    private readonly Mock<IRunnerConfigurationService> _mockConfigService;
    private readonly Mock<IGitHubActionsService> _mockActionsService;
    private readonly Mock<IRunnerContainerService> _mockContainerService;
    private readonly Mock<IGitHubAuthenticationService> _mockAuthService;
    private readonly Mock<IGitHubApiClient> _mockApiClient;
    private readonly Mock<INamedContainerPool> _mockContainerPool;
    private readonly Mock<ICoolifyTokenStore> _mockCoolifyTokenStore;
    private readonly Mock<ILogger<RunnerDaemonService>> _mockLogger;
    private readonly RunnerDaemonService _service;

    private readonly RunnerConfiguration _defaultConfig;
    private readonly RunnerRegistration _testRegistration;
    private readonly GitHubStoredToken _testToken;

    public RunnerDaemonServiceTests()
    {
        _mockConfigService = new Mock<IRunnerConfigurationService>();
        _mockActionsService = new Mock<IGitHubActionsService>();
        _mockContainerService = new Mock<IRunnerContainerService>();
        _mockAuthService = new Mock<IGitHubAuthenticationService>();
        _mockApiClient = new Mock<IGitHubApiClient>();
        _mockContainerPool = new Mock<INamedContainerPool>();
        _mockLogger = new Mock<ILogger<RunnerDaemonService>>();

        _testRegistration = new RunnerRegistration
        {
            Id = "reg-1",
            Owner = "testowner",
            Repository = "testrepo",
            Labels = "devcontainer-runner",
            Enabled = true
        };

        _defaultConfig = new RunnerConfiguration
        {
            Registrations = new List<RunnerRegistration> { _testRegistration },
            PollingIntervalSeconds = 1,
            MaxConcurrentJobs = 2
        };

        _testToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_test123"),
            IsValid = true,
            Scopes = new[] { "repo", "admin:org" }
        };

        _mockConfigService
            .Setup(c => c.LoadAsync())
            .ReturnsAsync(_defaultConfig);

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(_testToken);

        _mockContainerPool
            .Setup(p => p.GetAll())
            .Returns(new List<NamedContainerEntry>().AsReadOnly());

        _mockCoolifyTokenStore = new Mock<ICoolifyTokenStore>();

        _service = new RunnerDaemonService(
            _mockConfigService.Object,
            _mockActionsService.Object,
            _mockContainerService.Object,
            _mockAuthService.Object,
            _mockApiClient.Object,
            _mockContainerPool.Object,
            _mockCoolifyTokenStore.Object,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private void SetupJobsForRun(long runId, params (long jobId, List<string> labels)[] jobs)
    {
        var workflowJobs = jobs.Select(j => new WorkflowJob
        {
            Id = j.jobId,
            RunId = runId,
            Name = $"Job {j.jobId}",
            Status = "queued",
            Labels = j.labels
        }).ToList();

        _mockActionsService
            .Setup(a => a.GetJobsForRunAsync("testowner", "testrepo", runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflowJobs);
    }

    /// <summary>
    /// Helper: setup a standard ephemeral dispatch flow and cancel after JIT config is generated
    /// </summary>
    private void SetupEphemeralDispatch(
        CancellationTokenSource cts,
        QueuedWorkflowRun run,
        long jobId,
        string jitConfig = "jit-config")
    {
        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        SetupJobsForRun(run.Id, (jobId, new List<string> { "devcontainer-runner" }));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = jitConfig })
            .Callback(() => cts.Cancel()); // Cancel AFTER JIT is generated

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), run.Id, run.HeadBranch,
                "ghp_test123", jitConfig,
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new RunnerJobState { RunId = run.Id, Status = RunnerJobStatus.Completed });
    }

    #region GetStatus

    [Fact]
    public void GetStatus_WhenNotStarted_ReturnsNotRunning()
    {
        var status = _service.GetStatus();
        status.IsRunning.Should().BeFalse();
        status.StartedAt.Should().BeNull();
        status.ActiveJobs.Should().BeEmpty();
        status.TotalJobsCompleted.Should().Be(0);
        status.TotalJobsFailed.Should().Be(0);
    }

    #endregion

    #region GitHub rate limiting

    [Fact]
    public void CalculateRateLimitBackoff_UsesGitHubResetTime()
    {
        var now = new DateTime(2026, 8, 17, 19, 0, 0, DateTimeKind.Utc);

        var delay = RunnerDaemonService.CalculateRateLimitBackoff(
            now,
            resetAt: now.AddMinutes(42),
            retryAfter: null,
            consecutiveFailures: 1,
            pollingIntervalSeconds: 30,
            jitter: TimeSpan.FromSeconds(3));

        delay.Should().Be(TimeSpan.FromMinutes(42) + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CalculateRateLimitBackoff_UsesExponentialFallbackAndCapsIt()
    {
        var now = DateTime.UtcNow;

        RunnerDaemonService.CalculateRateLimitBackoff(
                now, null, null, consecutiveFailures: 1, pollingIntervalSeconds: 30, jitter: TimeSpan.Zero)
            .Should().Be(TimeSpan.FromSeconds(30));
        RunnerDaemonService.CalculateRateLimitBackoff(
                now, null, null, consecutiveFailures: 4, pollingIntervalSeconds: 30, jitter: TimeSpan.Zero)
            .Should().Be(TimeSpan.FromMinutes(4));
        RunnerDaemonService.CalculateRateLimitBackoff(
                now, null, null, consecutiveFailures: 20, pollingIntervalSeconds: 30, jitter: TimeSpan.Zero)
            .Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void IsGitHubRateLimit_RecognizesPrimaryAndSecondaryLimits()
    {
        RunnerDaemonService.IsGitHubRateLimit(new GitHubApiException(
                "forbidden", HttpStatusCode.Forbidden, isRateLimit: true))
            .Should().BeTrue();
        RunnerDaemonService.IsGitHubRateLimit(new GitHubApiException(
                "GitHub API error: secondary rate limit exceeded", HttpStatusCode.Forbidden))
            .Should().BeTrue();
    }

    #endregion

    #region RunAsync - Lifecycle

    [Fact]
    public async Task RunAsync_StartsPollingLoop_ReportsRunning()
    {
        var cts = new CancellationTokenSource();
        string? lastStatus = null;
        _service.StatusChanged += (_, msg) => lastStatus = msg;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _service.GetStatus().IsRunning.Should().BeFalse();
        lastStatus.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsGracefully()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() =>
            {
                pollCount++;
                if (pollCount >= 2) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        pollCount.Should().BeGreaterThanOrEqualTo(2);
        _service.GetStatus().IsRunning.Should().BeFalse();
    }

    #endregion

    #region RunAsync - Job Dispatch (Job-Level)

    [Fact]
    public async Task RunAsync_WhenQueuedRunFound_FetchesJobsAndDispatches()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        SetupEphemeralDispatch(cts, run, jobId: 99001, jitConfig: "base64encodedconfig");

        await _service.RunAsync(cts.Token);

        // Once to find the queued jobs, and again after the container exits to read the job's
        // conclusion from GitHub rather than trusting the runner process exit code.
        _mockActionsService.Verify(a => a.GetJobsForRunAsync(
            "testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            "testowner", "testrepo",
            It.IsAny<string>(), It.Is<string[]>(l => l.Contains("devcontainer-runner")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenJobHasNamedLabel_DispatchesWithContainerName()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        SetupJobsForRun(12345, (jobId: 99001, labels: new List<string> { "devcontainer-runner", "my-app-dev" }));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 42, EncodedJitConfig = "jit" })
            .Callback(() => cts.Cancel());

        _mockContainerPool.Setup(p => p.TryGet("my-app-dev")).Returns((NamedContainerEntry?)null);
        _mockContainerPool
            .Setup(p => p.AcquireAsync("my-app-dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 12345L, "main",
                "ghp_test123", "jit",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                "my-app-dev",
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new RunnerJobState
            {
                RunId = 12345,
                Status = RunnerJobStatus.Completed,
                ContainerId = "container-abc",
                ClonePath = "/tmp/clone",
                ContainerName = "my-app-dev"
            });

        await _service.RunAsync(cts.Token);
        await Task.Delay(300);

        _mockContainerPool.Verify(p => p.Register(It.Is<NamedContainerEntry>(
            e => e.Name == "my-app-dev" && e.ContainerId == "container-abc")), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenNamedContainerExists_ReusesIt()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        SetupJobsForRun(12345, (jobId: 99001, labels: new List<string> { "devcontainer-runner", "my-app" }));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 42, EncodedJitConfig = "jit" })
            .Callback(() => cts.Cancel());

        var existingEntry = new NamedContainerEntry
        {
            Name = "my-app",
            ContainerId = "existing-container-123",
            ClonePath = "/tmp/existing-clone",
            Owner = "testowner",
            Repository = "testrepo"
        };
        _mockContainerPool.Setup(p => p.TryGet("my-app")).Returns(existingEntry);
        _mockContainerPool
            .Setup(p => p.AcquireAsync("my-app", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Container is still alive
        _mockContainerService
            .Setup(c => c.IsContainerRunningAsync("existing-container-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockContainerService
            .Setup(c => c.ExecuteJobInExistingContainerAsync(
                It.IsAny<RunnerRegistration>(), 12345L, 99001L, "main",
                "existing-container-123", "/tmp/existing-clone", "my-app",
                "jit",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()))
            .ReturnsAsync(new RunnerJobState { RunId = 12345, Status = RunnerJobStatus.Completed });

        await _service.RunAsync(cts.Token);
        await Task.Delay(300);

        _mockContainerService.Verify(c => c.ExecuteJobInExistingContainerAsync(
            It.IsAny<RunnerRegistration>(), 12345L, 99001L, "main",
            "existing-container-123", "/tmp/existing-clone", "my-app",
            "jit",
            It.IsAny<Action<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>(),
            It.IsAny<string?>()), Times.Once);

        _mockContainerService.Verify(c => c.ExecuteJobAsync(
            It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenJobsApiFails_FallsBackToRunLevel()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        _mockActionsService
            .Setup(a => a.GetJobsForRunAsync("testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("API error"));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 42, EncodedJitConfig = "jit" })
            .Callback(() => cts.Cancel());

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 12345L, "main",
                "ghp_test123", "jit",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new RunnerJobState { RunId = 12345, Status = RunnerJobStatus.Completed });

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            "testowner", "testrepo",
            It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenNoQueuedRuns_ContinuesPolling()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() =>
            {
                pollCount++;
                if (pollCount >= 3) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        pollCount.Should().BeGreaterThanOrEqualTo(3);
        _mockContainerService.Verify(
            c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    #endregion

    #region RequestShutdown

    [Fact]
    public async Task RequestShutdown_StopsAcceptingNewJobs()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { new() { Id = 99999, Name = "Run", Status = "queued", HeadBranch = "main" } })
            .Callback(() =>
            {
                pollCount++;
                if (pollCount == 1) _service.RequestShutdown();
                if (pollCount >= 2) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Events

    [Fact]
    public async Task RunAsync_RaisesJobStartedEvent()
    {
        var cts = new CancellationTokenSource();
        RunnerJobState? startedJob = null;
        _service.JobStarted += (_, job) => startedJob = job;

        var run = new QueuedWorkflowRun { Id = 555, Name = "Event Test", Status = "queued", HeadBranch = "feature" };
        SetupEphemeralDispatch(cts, run, jobId: 99001, jitConfig: "jit");

        await _service.RunAsync(cts.Token);

        startedJob.Should().NotBeNull();
        startedJob!.RunId.Should().Be(555);
    }

    [Fact]
    public async Task RunAsync_RaisesJobCompletedEvent()
    {
        var cts = new CancellationTokenSource();
        RunnerJobState? completedJob = null;
        _service.JobCompleted += (_, job) => completedJob = job;

        var run = new QueuedWorkflowRun { Id = 777, Name = "Complete Test", Status = "queued", HeadBranch = "main" };
        SetupEphemeralDispatch(cts, run, jobId: 99001, jitConfig: "jit2");

        await _service.RunAsync(cts.Token);
        await Task.Delay(300);

        completedJob.Should().NotBeNull();
        completedJob!.RunId.Should().Be(777);
        completedJob.Status.Should().Be(RunnerJobStatus.Completed);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task RunAsync_WhenMaxConcurrentJobsReached_SkipsNewJobs()
    {
        var cts = new CancellationTokenSource();
        _defaultConfig.MaxConcurrentJobs = 1;
        var pollCount = 0;

        var blockingTcs = new TaskCompletionSource<RunnerJobState>();

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                if (pollCount == 1) return new List<QueuedWorkflowRun> { new() { Id = 100, Name = "Job 1", Status = "queued", HeadBranch = "main" } };
                if (pollCount == 2) return new List<QueuedWorkflowRun> { new() { Id = 200, Name = "Job 2", Status = "queued", HeadBranch = "main" } };
                blockingTcs.TrySetResult(new RunnerJobState { RunId = 100, Status = RunnerJobStatus.Completed });
                cts.Cancel();
                return new List<QueuedWorkflowRun>();
            });

        SetupJobsForRun(100, (jobId: 1001, labels: new List<string> { "devcontainer-runner" }));
        SetupJobsForRun(200, (jobId: 2001, labels: new List<string> { "devcontainer-runner" }));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = "jit" });

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 100L, "main",
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(blockingTcs.Task);

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Proactive Token Refresh

    [Fact]
    public async Task RunAsync_WhenTokenNearExpiry_ProactivelyRefreshes()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        // Initial token expires in 2 minutes (within the 5-minute threshold)
        var nearExpiryToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_old_token"),
            RefreshToken = SecretValue.From("ghr_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            ExpiresAt = DateTime.UtcNow.AddMinutes(2)
        };

        var refreshedToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_refreshed_token"),
            RefreshToken = SecretValue.From("ghr_new_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(nearExpiryToken);

        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync(refreshedToken);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() =>
            {
                pollCount++;
                if (pollCount >= 1) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        // RefreshTokenAsync should have been called proactively (not from a "Bad credentials" error)
        _mockAuthService.Verify(a => a.RefreshTokenAsync(null), Times.AtLeastOnce);
        // The new token should have been set on the API client
        _mockApiClient.Verify(a => a.SetAuthenticationToken(It.Is<SecretValue>(t => t.Reveal() == "ghp_refreshed_token")), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenTokenExpiresAtIsNull_ProactivelyRefreshes()
    {
        var cts = new CancellationTokenSource();

        // Token has null ExpiresAt (GitHub omitted expires_in)
        var nullExpiryToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_null_expiry"),
            RefreshToken = SecretValue.From("ghr_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = null
        };

        var refreshedToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_refreshed_from_null"),
            RefreshToken = SecretValue.From("ghr_new_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(nullExpiryToken);

        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync(refreshedToken);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _mockAuthService.Verify(a => a.RefreshTokenAsync(null), Times.AtLeastOnce);
        _mockApiClient.Verify(a => a.SetAuthenticationToken(It.Is<SecretValue>(t => t.Reveal() == "ghp_refreshed_from_null")), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenTokenOlderThan4Hours_ProactivelyRefreshes()
    {
        var cts = new CancellationTokenSource();

        // Token was created 5 hours ago but claims to expire in 8 hours
        var oldToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_old_token"),
            RefreshToken = SecretValue.From("ghr_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            CreatedAt = DateTime.UtcNow.AddHours(-5),
            ExpiresAt = DateTime.UtcNow.AddHours(3)
        };

        var refreshedToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_refreshed_old"),
            RefreshToken = SecretValue.From("ghr_new_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            ExpiresAt = DateTime.UtcNow.AddHours(8)
        };

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(oldToken);

        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync(refreshedToken);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _mockAuthService.Verify(a => a.RefreshTokenAsync(null), Times.AtLeastOnce);
        _mockApiClient.Verify(a => a.SetAuthenticationToken(It.Is<SecretValue>(t => t.Reveal() == "ghp_refreshed_old")), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenTokenNotNearExpiry_DoesNotProactivelyRefresh()
    {
        var cts = new CancellationTokenSource();

        // Token expires in 30 minutes (well outside the 5-minute threshold)
        // CreatedAt is recent so the max-age safety net doesn't trigger
        var validToken = new GitHubStoredToken
        {
            AccessToken = SecretValue.From("ghp_valid_token"),
            RefreshToken = SecretValue.From("ghr_refresh"),
            IsValid = true,
            Scopes = new[] { "repo" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(validToken);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        // RefreshTokenAsync should NOT have been called since token is not near expiry
        _mockAuthService.Verify(a => a.RefreshTokenAsync(null), Times.Never);
    }

    #endregion

    #region Auth & Config Errors

    [Fact]
    public async Task RunAsync_WhenNoToken_ThrowsInvalidOperationException()
    {
        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync((GitHubStoredToken?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenNoRegistrations_ExitsImmediately()
    {
        _defaultConfig.Registrations.Clear();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(
            a => a.GetQueuedRunsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_SkipsDisabledRegistrations()
    {
        var cts = new CancellationTokenSource();
        _testRegistration.Enabled = false;

        var enabledReg = new RunnerRegistration { Owner = "other", Repository = "repo", Labels = "devcontainer-runner", Enabled = true };
        _defaultConfig.Registrations.Add(enabledReg);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("other", "repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(
            a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region Container Discovery on Startup

    [Fact]
    public async Task RunAsync_OnStartup_DiscoversExistingContainersAndRegistersInPool()
    {
        var cts = new CancellationTokenSource();

        var discoveredEntries = new List<NamedContainerEntry>
        {
            new() { Name = "app-1", ContainerId = "container-aaa", ClonePath = "/tmp/clone1", Owner = "testowner", Repository = "testrepo" },
            new() { Name = "app-2", ContainerId = "container-bbb", ClonePath = "/tmp/clone2", Owner = "testowner", Repository = "testrepo" }
        };

        _mockContainerService
            .Setup(c => c.DiscoverNamedContainersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(discoveredEntries);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _mockContainerPool.Verify(p => p.Register(It.Is<NamedContainerEntry>(e => e.Name == "app-1")), Times.Once);
        _mockContainerPool.Verify(p => p.Register(It.Is<NamedContainerEntry>(e => e.Name == "app-2")), Times.Once);
        _mockContainerService.Verify(c => c.DiscoverNamedContainersAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenDiscoveryFails_ContinuesNormally()
    {
        var cts = new CancellationTokenSource();

        _mockContainerService
            .Setup(c => c.DiscoverNamedContainersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Docker not responding"));

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel());

        await _service.RunAsync(cts.Token);

        _mockActionsService.Verify(
            a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Auth Token Refresh

    [Fact]
    public async Task RunAsync_WhenAuthFails_AttemptsRefreshWithinFirst3Failures()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Bad credentials"));

        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync(new GitHubStoredToken
            {
                AccessToken = SecretValue.From("ghp_refreshed"),
                IsValid = true,
                Scopes = new[] { "repo" }
            })
            .Callback(() =>
            {
                pollCount++;
                if (pollCount >= 2) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        _mockAuthService.Verify(a => a.RefreshTokenAsync(null), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenAuthFailsRepeatedly_StillAttemptsRefreshAfter3Failures()
    {
        var cts = new CancellationTokenSource();
        var refreshAttempts = 0;

        // Polling always throws auth error
        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Bad credentials"));

        // Stored token has a refresh token, so daemon won't stop
        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(new GitHubStoredToken
            {
                AccessToken = SecretValue.From("ghp_test123"),
                RefreshToken = SecretValue.From("ghr_refresh_token"),
                IsValid = true,
                Scopes = new[] { "repo" }
            });

        // Refresh always fails (returns null) — but should still be attempted after >3 failures
        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync((GitHubStoredToken?)null)
            .Callback(() =>
            {
                refreshAttempts++;
                // Cancel after we see the 5th refresh attempt (proves it retries beyond 3)
                if (refreshAttempts >= 5) cts.Cancel();
            });

        await _service.RunAsync(cts.Token);

        // Should have attempted refresh more than 3 times (the old code stopped after 3)
        refreshAttempts.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task RunAsync_WhenRefreshTokenIsNull_StopsDaemonGracefully()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var statusMessages = new List<string>();
        _service.StatusChanged += (_, msg) => statusMessages.Add(msg);

        // Polling throws auth error
        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Bad credentials"));

        // Stored token has no refresh token
        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(new GitHubStoredToken
            {
                AccessToken = SecretValue.From("ghp_test123"),
                RefreshToken = SecretValue.From(null),
                IsValid = true
            });

        // RefreshTokenAsync returns null (no refresh token available)
        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .ReturnsAsync((GitHubStoredToken?)null);

        await _service.RunAsync(cts.Token);

        // Daemon should have stopped (not running) without hitting the timeout
        _service.GetStatus().IsRunning.Should().BeFalse();
        statusMessages.Should().Contain(m => m.Contains("no refresh token") || m.Contains("re-authenticate") || m.Contains("stopping"));
    }

    [Fact]
    public async Task RunAsync_WhenRefreshSucceedsAfterFailures_ResetsCounterAndResumesPolling()
    {
        var cts = new CancellationTokenSource();
        var pollCount = 0;
        var refreshCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                pollCount++;
                if (pollCount <= 4)
                    throw new Exception("Bad credentials");
                // After refresh succeeds, polling works again
                cts.Cancel();
                return Task.FromResult(new List<QueuedWorkflowRun>());
            });

        _mockAuthService
            .Setup(a => a.RefreshTokenAsync(null))
            .Returns(() =>
            {
                refreshCount++;
                if (refreshCount < 3)
                    return Task.FromResult<GitHubStoredToken?>(null);
                // 3rd refresh attempt succeeds
                return Task.FromResult<GitHubStoredToken?>(new GitHubStoredToken
                {
                    AccessToken = SecretValue.From("ghp_new_token"),
                    RefreshToken = SecretValue.From("ghr_new_refresh"),
                    IsValid = true
                });
            });

        await _service.RunAsync(cts.Token);

        // Polling should have resumed after the successful refresh
        pollCount.Should().BeGreaterThanOrEqualTo(5);
        _mockApiClient.Verify(a => a.SetAuthenticationToken(It.Is<SecretValue>(t => t.Reveal() == "ghp_new_token")), Times.AtLeastOnce);
    }

    #endregion

    #region Activity reporting

    [Fact]
    public async Task RunAsync_WhenRunStateUnchanged_ReportsItOnlyOnce()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };
        var pollCount = 0;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                pollCount++;
                if (pollCount >= 3) cts.Cancel();
                return Task.FromResult(new List<QueuedWorkflowRun> { run });
            });

        // Already running elsewhere, so nothing gets dispatched and the state never changes.
        _mockActionsService
            .Setup(a => a.GetJobsForRunAsync("testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WorkflowJob>
            {
                new()
                {
                    Id = 99001, RunId = 12345, Name = "build-push", Status = "in_progress",
                    Labels = new List<string> { "devcontainer-runner" }, RunnerName = "other-runner"
                }
            });

        var runLines = new List<string>();
        _service.StatusChanged += (_, msg) => { if (msg.Contains("#12345:")) runLines.Add(msg); };

        await _service.RunAsync(cts.Token);

        pollCount.Should().BeGreaterThanOrEqualTo(3);
        runLines.Should().ContainSingle("an unchanged run must not reprint on every poll");
        runLines[0].Should().Contain("build-push").And.Contain("in_progress");
    }

    [Fact]
    public async Task RunAsync_WhenGitHubConcludesFailure_CountsJobAsFailed()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        var job = new WorkflowJob
        {
            Id = 99001, RunId = 12345, Name = "build-push", Status = "queued",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        _mockActionsService
            .Setup(a => a.GetJobsForRunAsync("testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new List<WorkflowJob> { job }));

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo", It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = "jit-config" })
            .Callback(() => cts.Cancel());

        // The runner process exits cleanly, but the job itself went red on GitHub.
        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), run.Id, run.HeadBranch,
                "ghp_test123", "jit-config",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(() =>
            {
                job.Status = "completed";
                job.Conclusion = "failure";
                return new RunnerJobState { RunId = run.Id, Status = RunnerJobStatus.Completed };
            });

        await _service.RunAsync(cts.Token);

        var status = _service.GetStatus();
        status.TotalJobsFailed.Should().Be(1, "the GitHub conclusion decides, not the container exit code");
        status.TotalJobsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_WhenGitHubGivesRunnerAnotherJob_RequeuesTheStrandedJob()
    {
        var cts = new CancellationTokenSource();
        var run = new QueuedWorkflowRun { Id = 12345, Name = "CI Build", Status = "queued", HeadBranch = "main" };

        // Two parallel jobs with identical labels — GitHub may hand our runner either one.
        var deploy = new WorkflowJob
        {
            Id = 99001, RunId = 12345, Name = "deploy-production", Status = "queued",
            Labels = new List<string> { "devcontainer-runner" }
        };
        var build = new WorkflowJob
        {
            Id = 99002, RunId = 12345, Name = "build-push", Status = "queued",
            Labels = new List<string> { "devcontainer-runner" }
        };

        var pollCount = 0;
        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                pollCount++;
                if (pollCount >= 4) cts.Cancel();
                return Task.FromResult(new List<QueuedWorkflowRun> { run });
            });

        _mockActionsService
            .Setup(a => a.GetJobsForRunAsync("testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(new List<WorkflowJob> { deploy, build }));

        var generatedRunnerNames = new List<string>();
        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo", It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = "jit-config" })
            .Callback((string _, string _, string name, string[] _, CancellationToken _) =>
            {
                generatedRunnerNames.Add(name);

                // GitHub hands the first runner to build-push, not to the job it was named after.
                if (generatedRunnerNames.Count == 1)
                {
                    build.Status = "in_progress";
                    build.RunnerName = name;
                }
            });

        // Keep the container busy so the daemon can poll while the job "runs".
        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), run.Id, run.HeadBranch,
                "ghp_test123", "jit-config",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(async (RunnerRegistration _, long runId, string? _, string _, string _,
                            Action<string>? _, CancellationToken ct, string? _, string? _, string? _) =>
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { }
                return new RunnerJobState { RunId = runId, Status = RunnerJobStatus.Completed };
            });

        var notices = new List<string>();
        _service.StatusChanged += (_, msg) => { if (msg.Contains("GitHub gave runner")) notices.Add(msg); };

        await _service.RunAsync(cts.Token);

        notices.Should().ContainSingle("the swap is reported once, not on every poll");
        notices[0].Should().Contain("build-push").And.Contain("deploy-production");

        // deploy-production was released, so a second runner was generated for it.
        generatedRunnerNames.Should().HaveCountGreaterThanOrEqualTo(2,
            "the stranded job must get its own runner instead of waiting for the first one to finish");
    }

    #endregion

    #region Dispatch ceiling

    /// <summary>
    /// A job no runner ever claims must stop being dispatched, and every registration it minted
    /// must be taken back off GitHub.
    /// </summary>
    /// <remarks>
    /// Regression for the loop that put 86 offline runners on one repository. Every terminal path
    /// in ExecuteAndTrackJob releases the job from _dispatchedJobIds, so a job that fails to start
    /// is still queued on GitHub, seen by the next poll, and dispatched again. Without a ceiling
    /// that repeats until a human cancels the run, and each pass leaves a JIT registration behind
    /// that GitHub never reaps -- ephemeral runners are auto-deleted on job completion, and this
    /// one never completed a job.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenDispatchAlwaysFails_StopsAtMaxAttemptsAndRemovesEveryRunner()
    {
        using var cts = new CancellationTokenSource();

        var run = new QueuedWorkflowRun { Id = 500, Name = "CI", HeadBranch = "main" };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        // Stays "queued" on every poll: this is a job that never gets picked up.
        SetupJobsForRun(run.Id, (700L, new List<string> { "devcontainer-runner" }));

        var nextRunnerId = 0;
        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GitHubJitRunnerConfig
            {
                RunnerId = Interlocked.Increment(ref nextRunnerId),
                EncodedJitConfig = "jit-config"
            });

        _mockActionsService
            .Setup(a => a.DeleteRunnerAsync(
                "testowner", "testrepo", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("container refused to start"));

        var gaveUp = new List<string>();
        _service.StatusChanged += (_, msg) => { if (msg.Contains("Gave up on")) gaveUp.Add(msg); };

        var daemon = _service.RunAsync(cts.Token);

        // Wait for the ceiling to be reached rather than for a fixed number of polls, so the
        // assertion below is about the ceiling and not about how fast the loop happens to run.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (gaveUp.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100, CancellationToken.None);

        gaveUp.Should().NotBeEmpty("the daemon must say out loud that it has stopped retrying");

        // Keep polling past the ceiling: the count must not move.
        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

        cts.Cancel();
        await daemon;

        _mockActionsService.Verify(
            a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()),
            Times.Exactly(RunnerDaemonService.MaxDispatchAttempts),
            "the job is dispatched at most MaxDispatchAttempts times, however long the daemon runs");

        _mockActionsService.Verify(
            a => a.DeleteRunnerAsync(
                "testowner", "testrepo", It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(RunnerDaemonService.MaxDispatchAttempts),
            "every registration minted for a dispatch that failed is removed again");
    }

    /// <summary>
    /// The same ceiling, reached through the door that does not throw.
    /// </summary>
    /// <remarks>
    /// The container starting and exiting again without the runner ever claiming the job is not an
    /// exception -- ExecuteJobAsync returns normally with a Failed status, and the job is still
    /// queued on GitHub. That path is at least as likely as a throw for the incident this ceiling
    /// was written for: registrations arriving roughly forty seconds apart look much more like a
    /// container that starts and gives up than like one that refuses to start at all.
    /// </remarks>
    [Fact]
    public async Task RunAsync_WhenTheContainerExitsWithoutClaimingTheJob_StopsAtMaxAttemptsAndRemovesEveryRunner()
    {
        using var cts = new CancellationTokenSource();

        var run = new QueuedWorkflowRun { Id = 501, Name = "CI", HeadBranch = "main" };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { run });

        // Still "queued" on every poll, and with no conclusion -- so ResolveFinalStatusAsync keeps
        // the container's own Failed verdict rather than overriding it.
        SetupJobsForRun(run.Id, (701L, new List<string> { "devcontainer-runner" }));

        var nextRunnerId = 0;
        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GitHubJitRunnerConfig
            {
                RunnerId = Interlocked.Increment(ref nextRunnerId),
                EncodedJitConfig = "jit-config"
            });

        _mockActionsService
            .Setup(a => a.DeleteRunnerAsync(
                "testowner", "testrepo", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Returns rather than throws: the whole point of this test.
        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(() => new RunnerJobState { RunId = run.Id, Status = RunnerJobStatus.Failed });

        var gaveUp = new List<string>();
        _service.StatusChanged += (_, msg) => { if (msg.Contains("Gave up on")) gaveUp.Add(msg); };

        var daemon = _service.RunAsync(cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (gaveUp.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100, CancellationToken.None);

        gaveUp.Should().NotBeEmpty("a container that exits without claiming the job must reach the ceiling too");

        await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

        cts.Cancel();
        await daemon;

        _mockActionsService.Verify(
            a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()),
            Times.Exactly(RunnerDaemonService.MaxDispatchAttempts),
            "a dispatch that fails without throwing still counts against the ceiling");

        _mockActionsService.Verify(
            a => a.DeleteRunnerAsync(
                "testowner", "testrepo", It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(RunnerDaemonService.MaxDispatchAttempts),
            "and its registration is removed on that path as well");
    }

    #endregion
}
