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
    private readonly Mock<INamedContainerPool> _mockContainerPool;
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

        _mockContainerPool
            .Setup(p => p.GetAll())
            .Returns(new List<NamedContainerEntry>().AsReadOnly());

        _service = new RunnerDaemonService(
            _mockConfigService.Object,
            _mockActionsService.Object,
            _mockContainerService.Object,
            _mockAuthService.Object,
            _mockApiClient.Object,
            _mockContainerPool.Object,
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
                It.IsAny<CancellationToken>()))
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

        _mockActionsService.Verify(a => a.GetJobsForRunAsync(
            "testowner", "testrepo", 12345L, It.IsAny<CancellationToken>()), Times.Once);

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
                "my-app-dev"))
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
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunnerJobState { RunId = 12345, Status = RunnerJobStatus.Completed });

        await _service.RunAsync(cts.Token);
        await Task.Delay(300);

        _mockContainerService.Verify(c => c.ExecuteJobInExistingContainerAsync(
            It.IsAny<RunnerRegistration>(), 12345L, 99001L, "main",
            "existing-container-123", "/tmp/existing-clone", "my-app",
            "jit",
            It.IsAny<Action<string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockContainerService.Verify(c => c.ExecuteJobAsync(
            It.IsAny<RunnerRegistration>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<Action<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
                It.IsAny<CancellationToken>()))
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
                It.IsAny<Action<string>?>(), It.IsAny<CancellationToken>()),
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
                It.IsAny<CancellationToken>()))
            .Returns(blockingTcs.Task);

        await _service.RunAsync(cts.Token);

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
}
