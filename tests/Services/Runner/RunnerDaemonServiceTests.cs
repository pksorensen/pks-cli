using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

public class RunnerDaemonServiceTests : IDisposable
{
    private readonly Mock<IRunnerConfigurationService> _mockConfigService;
    private readonly Mock<IGitHubActionsService> _mockActionsService;
    private readonly Mock<IRunnerContainerService> _mockContainerService;
    private readonly Mock<IGitHubAuthenticationService> _mockAuthService;
    private readonly Mock<IGitHubApiClient> _mockApiClient;
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
            PollingIntervalSeconds = 1, // Short interval for tests
            MaxConcurrentJobs = 2
        };

        _testToken = new GitHubStoredToken
        {
            AccessToken = "ghp_test123",
            IsValid = true,
            Scopes = new[] { "repo", "admin:org" }
        };

        _mockConfigService
            .Setup(c => c.LoadAsync())
            .ReturnsAsync(_defaultConfig);

        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync(_testToken);

        _service = new RunnerDaemonService(
            _mockConfigService.Object,
            _mockActionsService.Object,
            _mockContainerService.Object,
            _mockAuthService.Object,
            _mockApiClient.Object,
            _mockLogger.Object);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    #region GetStatus

    [Fact]
    public void GetStatus_WhenNotStarted_ReturnsNotRunning()
    {
        // Act
        var status = _service.GetStatus();

        // Assert
        status.IsRunning.Should().BeFalse();
        status.StartedAt.Should().BeNull();
        status.ActiveJobs.Should().BeEmpty();
        status.TotalJobsCompleted.Should().Be(0);
        status.TotalJobsFailed.Should().Be(0);
    }

    #endregion

    #region RunAsync - Lifecycle

    [Fact]
    public async Task RunAsync_StartsPollingLoop_ReportsRunning()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        string? lastStatus = null;
        _service.StatusChanged += (_, msg) => lastStatus = msg;

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() => cts.Cancel()); // Cancel after first poll

        // Act
        await _service.RunAsync(cts.Token);

        // Assert
        var status = _service.GetStatus();
        // After RunAsync returns, the daemon is no longer running
        status.IsRunning.Should().BeFalse();
        // But we should have seen a status change indicating it was running
        lastStatus.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsGracefully()
    {
        // Arrange
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

        // Act
        await _service.RunAsync(cts.Token);

        // Assert - should complete without throwing
        pollCount.Should().BeGreaterThanOrEqualTo(2);
        _service.GetStatus().IsRunning.Should().BeFalse();
    }

    #endregion

    #region RunAsync - Job Dispatch

    [Fact]
    public async Task RunAsync_WhenQueuedRunFound_DispatchesJob()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var queuedRun = new QueuedWorkflowRun
        {
            Id = 12345,
            Name = "CI Build",
            Status = "queued",
            HeadBranch = "main",
            HeadSha = "abc123",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { queuedRun })
            .Callback(() => cts.Cancel()); // Cancel after dispatch

        var jitConfig = new GitHubJitRunnerConfig
        {
            RunnerId = 42,
            EncodedJitConfig = "base64encodedconfig"
        };

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                "testowner", "testrepo",
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jitConfig);

        var completedJob = new RunnerJobState
        {
            RunId = 12345,
            Registration = _testRegistration,
            Status = RunnerJobStatus.Completed,
            StartedAt = DateTime.UtcNow
        };

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                _testRegistration, 12345L, "main",
                "ghp_test123", "base64encodedconfig",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedJob);

        // Act
        await _service.RunAsync(cts.Token);

        // Assert
        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            "testowner", "testrepo",
            It.IsAny<string>(), It.Is<string[]>(l => l.Contains("devcontainer-runner")),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockContainerService.Verify(c => c.ExecuteJobAsync(
            _testRegistration, 12345L, "main",
            "ghp_test123", "base64encodedconfig",
            It.IsAny<Action<string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenNoQueuedRuns_ContinuesPolling()
    {
        // Arrange
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

        // Act
        await _service.RunAsync(cts.Token);

        // Assert
        pollCount.Should().BeGreaterThanOrEqualTo(3);
        _mockContainerService.Verify(
            c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region RequestShutdown

    [Fact]
    public async Task RequestShutdown_StopsAcceptingNewJobs()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        var pollCount = 0;

        var queuedRun = new QueuedWorkflowRun
        {
            Id = 99999,
            Name = "Post-Shutdown Run",
            Status = "queued",
            HeadBranch = "main",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { queuedRun })
            .Callback(() =>
            {
                pollCount++;
                if (pollCount == 1)
                {
                    // Request shutdown on first poll before job dispatch
                    _service.RequestShutdown();
                }
                if (pollCount >= 2) cts.Cancel();
            });

        // Act
        await _service.RunAsync(cts.Token);

        // Assert - should NOT dispatch jobs after shutdown requested
        // The first poll triggers shutdown, so no JIT config should be generated
        // after that point
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
        // Arrange
        var cts = new CancellationTokenSource();
        RunnerJobState? startedJob = null;
        _service.JobStarted += (_, job) => startedJob = job;

        var queuedRun = new QueuedWorkflowRun
        {
            Id = 555,
            Name = "Event Test",
            Status = "queued",
            HeadBranch = "feature",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { queuedRun })
            .Callback(() => cts.Cancel());

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = "jit" });

        var jobState = new RunnerJobState
        {
            RunId = 555,
            Status = RunnerJobStatus.Completed,
            StartedAt = DateTime.UtcNow
        };

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 555L, "feature",
                "ghp_test123", "jit",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobState);

        // Act
        await _service.RunAsync(cts.Token);

        // Assert
        startedJob.Should().NotBeNull();
        startedJob!.RunId.Should().Be(555);
    }

    [Fact]
    public async Task RunAsync_RaisesJobCompletedEvent()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        RunnerJobState? completedJob = null;
        _service.JobCompleted += (_, job) => completedJob = job;

        var queuedRun = new QueuedWorkflowRun
        {
            Id = 777,
            Name = "Complete Test",
            Status = "queued",
            HeadBranch = "main",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun> { queuedRun })
            .Callback(() => cts.Cancel());

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 2, EncodedJitConfig = "jit2" });

        var jobState = new RunnerJobState
        {
            RunId = 777,
            Status = RunnerJobStatus.Completed,
            StartedAt = DateTime.UtcNow
        };

        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 777L, "main",
                "ghp_test123", "jit2",
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobState);

        // Act
        await _service.RunAsync(cts.Token);

        // Allow time for async job completion tracking
        await Task.Delay(200);

        // Assert
        completedJob.Should().NotBeNull();
        completedJob!.RunId.Should().Be(777);
        completedJob.Status.Should().Be(RunnerJobStatus.Completed);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task RunAsync_WhenMaxConcurrentJobsReached_SkipsNewJobs()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _defaultConfig.MaxConcurrentJobs = 1;
        var pollCount = 0;

        // Create a job that blocks until we release it
        var blockingTcs = new TaskCompletionSource<RunnerJobState>();

        var queuedRun1 = new QueuedWorkflowRun
        {
            Id = 100,
            Name = "Job 1",
            Status = "queued",
            HeadBranch = "main",
            Labels = new List<string> { "devcontainer-runner" }
        };

        var queuedRun2 = new QueuedWorkflowRun
        {
            Id = 200,
            Name = "Job 2",
            Status = "queued",
            HeadBranch = "main",
            Labels = new List<string> { "devcontainer-runner" }
        };

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                if (pollCount == 1) return new List<QueuedWorkflowRun> { queuedRun1 };
                if (pollCount == 2) return new List<QueuedWorkflowRun> { queuedRun2 };
                // On third poll, complete the blocking job and cancel
                blockingTcs.TrySetResult(new RunnerJobState { RunId = 100, Status = RunnerJobStatus.Completed });
                cts.Cancel();
                return new List<QueuedWorkflowRun>();
            });

        _mockActionsService
            .Setup(a => a.GenerateJitConfigAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubJitRunnerConfig { RunnerId = 1, EncodedJitConfig = "jit" });

        // First job blocks until released
        _mockContainerService
            .Setup(c => c.ExecuteJobAsync(
                It.IsAny<RunnerRegistration>(), 100L, "main",
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Action<string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(blockingTcs.Task);

        // Act
        await _service.RunAsync(cts.Token);

        // Assert - JIT config should only be generated once (for job 1)
        // Job 2 should be skipped because max concurrent is 1
        _mockActionsService.Verify(a => a.GenerateJitConfigAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Auth & Config Errors

    [Fact]
    public async Task RunAsync_WhenNoToken_ThrowsInvalidOperationException()
    {
        // Arrange
        _mockAuthService
            .Setup(a => a.GetStoredTokenAsync(null))
            .ReturnsAsync((GitHubStoredToken?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_WhenNoRegistrations_ExitsImmediately()
    {
        // Arrange
        _defaultConfig.Registrations.Clear();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        string? lastStatus = null;
        _service.StatusChanged += (_, msg) => lastStatus = msg;

        // Act
        await _service.RunAsync(cts.Token);

        // Assert
        _mockActionsService.Verify(
            a => a.GetQueuedRunsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_SkipsDisabledRegistrations()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        _testRegistration.Enabled = false;
        var pollCount = 0;

        // Add an enabled registration to keep the loop going
        var enabledReg = new RunnerRegistration
        {
            Owner = "other",
            Repository = "repo",
            Labels = "devcontainer-runner",
            Enabled = true
        };
        _defaultConfig.Registrations.Add(enabledReg);

        _mockActionsService
            .Setup(a => a.GetQueuedRunsAsync("other", "repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QueuedWorkflowRun>())
            .Callback(() =>
            {
                pollCount++;
                if (pollCount >= 1) cts.Cancel();
            });

        // Act
        await _service.RunAsync(cts.Token);

        // Assert - should NOT poll the disabled registration
        _mockActionsService.Verify(
            a => a.GetQueuedRunsAsync("testowner", "testrepo", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion
}
