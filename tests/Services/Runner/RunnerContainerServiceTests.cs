using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

public class RunnerContainerServiceTests : IDisposable
{
    private readonly Mock<IProcessRunner> _mockProcessRunner;
    private readonly Mock<ILogger<RunnerContainerService>> _mockLogger;
    private readonly RunnerContainerService _service;
    private readonly RunnerRegistration _testRegistration;

    public RunnerContainerServiceTests()
    {
        _mockProcessRunner = new Mock<IProcessRunner>();
        _mockLogger = new Mock<ILogger<RunnerContainerService>>();
        _service = new RunnerContainerService(_mockProcessRunner.Object, _mockLogger.Object);
        _testRegistration = new RunnerRegistration
        {
            Owner = "testowner",
            Repository = "testrepo",
            Labels = "devcontainer-runner"
        };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    #region CheckPrerequisitesAsync

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenBothAvailable_ReturnsSuccess()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", "version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Docker version 24.0.0", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", "--version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "0.62.0", ""));

        // Act
        var (dockerAvailable, devcontainerAvailable, error) = await _service.CheckPrerequisitesAsync();

        // Assert
        dockerAvailable.Should().BeTrue();
        devcontainerAvailable.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenDockerMissing_ReturnsError()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", "version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "docker: command not found"));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", "--version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "0.62.0", ""));

        // Act
        var (dockerAvailable, devcontainerAvailable, error) = await _service.CheckPrerequisitesAsync();

        // Assert
        dockerAvailable.Should().BeFalse();
        devcontainerAvailable.Should().BeTrue();
        error.Should().NotBeNullOrEmpty();
        error.Should().Contain("Docker");
    }

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenDevcontainerCliMissing_ReturnsError()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", "version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Docker version 24.0.0", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", "--version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "devcontainer: command not found"));

        // Act
        var (dockerAvailable, devcontainerAvailable, error) = await _service.CheckPrerequisitesAsync();

        // Assert
        dockerAvailable.Should().BeTrue();
        devcontainerAvailable.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        error.Should().Contain("devcontainer CLI");
    }

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenDockerThrowsException_ReturnsError()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", "version", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Process not found"));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", "--version", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "0.62.0", ""));

        // Act
        var (dockerAvailable, devcontainerAvailable, error) = await _service.CheckPrerequisitesAsync();

        // Assert
        dockerAvailable.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region ExecuteJobAsync

    [Fact]
    public async Task ExecuteJobAsync_CompletesFullLifecycle()
    {
        // Arrange
        var progressMessages = new List<string>();
        var devcontainerUpJson = """{"outcome":"success","containerId":"abc123","remoteUser":"vscode"}""";

        // git clone
        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Cloning into...", ""));

        // devcontainer up
        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", It.Is<string>(a => a.Contains("up")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, devcontainerUpJson, ""));

        // docker exec (runner install)
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("mkdir")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // docker exec (runner run)
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("run.sh")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner completed", ""));

        // Act
        var result = await _service.ExecuteJobAsync(
            _testRegistration,
            12345,
            "main",
            "ghp_test_token",
            "encoded_jit_config",
            msg => progressMessages.Add(msg));

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RunnerJobStatus.Completed);
        result.RunId.Should().Be(12345);
        result.Branch.Should().Be("main");
        result.Registration.Should().Be(_testRegistration);
        progressMessages.Should().NotBeEmpty();
        progressMessages.Should().Contain(m => m.Contains("Cloning"));
        progressMessages.Should().Contain(m => m.Contains("devcontainer"));
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenCloneFails_ReturnsFailedState()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(128, "", "fatal: repository not found"));

        // Act
        var result = await _service.ExecuteJobAsync(
            _testRegistration,
            12345,
            "main",
            "ghp_test_token",
            "encoded_jit_config");

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RunnerJobStatus.Failed);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenDevcontainerUpFails_ReturnsFailedState()
    {
        // Arrange
        // git clone succeeds
        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Cloning into...", ""));

        // devcontainer up fails
        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", It.Is<string>(a => a.Contains("up")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "Error: Docker daemon not responding"));

        // cleanup docker rm (may be called)
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        var result = await _service.ExecuteJobAsync(
            _testRegistration,
            12345,
            "main",
            "ghp_test_token",
            "encoded_jit_config");

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RunnerJobStatus.Failed);
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenCancelled_CleansUp()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .Returns(async (string cmd, string args, string? wd, CancellationToken ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return new ProcessResult(0, "", "");
            });

        // cleanup docker rm (should be attempted)
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        var result = await _service.ExecuteJobAsync(
            _testRegistration,
            12345,
            "main",
            "ghp_test_token",
            "encoded_jit_config",
            cancellationToken: cts.Token);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(RunnerJobStatus.Failed);
    }

    [Fact]
    public async Task ExecuteJobAsync_SetsClonePathOnJobState()
    {
        // Arrange
        var devcontainerUpJson = """{"outcome":"success","containerId":"container123","remoteUser":"vscode"}""";

        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", It.Is<string>(a => a.Contains("up")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, devcontainerUpJson, ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        var result = await _service.ExecuteJobAsync(
            _testRegistration,
            99,
            "feature-branch",
            "token",
            "jit_config");

        // Assert
        result.ClonePath.Should().NotBeNullOrEmpty();
        result.ClonePath.Should().Contain("pks-runner-");
        result.ContainerId.Should().Be("container123");
    }

    #endregion

    #region CleanupJobAsync

    [Fact]
    public async Task CleanupJobAsync_RemovesContainerAndCloneDirectory()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"pks-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var job = new RunnerJobState
        {
            ContainerId = "test-container-id",
            ClonePath = tempDir,
            Status = RunnerJobStatus.Completed
        };

        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm -f test-container-id")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        await _service.CleanupJobAsync(job);

        // Assert
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm -f test-container-id")), null, It.IsAny<CancellationToken>()),
            Times.Once);

        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Fact]
    public async Task CleanupJobAsync_IgnoresDockerRemoveErrors()
    {
        // Arrange
        var job = new RunnerJobState
        {
            ContainerId = "nonexistent-container",
            ClonePath = "/nonexistent/path",
            Status = RunnerJobStatus.Completed
        };

        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "Error: No such container"));

        // Act & Assert - should not throw
        await _service.Invoking(s => s.CleanupJobAsync(job))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task CleanupJobAsync_HandlesEmptyContainerId()
    {
        // Arrange
        var job = new RunnerJobState
        {
            ContainerId = "",
            ClonePath = "/nonexistent/path",
            Status = RunnerJobStatus.Completed
        };

        // Act & Assert - should not throw
        await _service.Invoking(s => s.CleanupJobAsync(job))
            .Should().NotThrowAsync();

        // docker rm should not be called for empty container ID
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm")), null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CleanupJobAsync_WhenNamedContainer_SkipsCleanup()
    {
        // Arrange
        var job = new RunnerJobState
        {
            ContainerId = "some-container-id",
            ContainerName = "my-named-container",
            ClonePath = "/some/path",
            Status = RunnerJobStatus.Completed
        };

        // Act
        await _service.CleanupJobAsync(job);

        // Assert - docker rm should NOT be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains("rm")), null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region ExecuteJobAsync with Container Labels

    [Fact]
    public async Task ExecuteJobAsync_WhenNamedContainer_AddsIdLabelsToDevcontainerUp()
    {
        // Arrange
        var capturedArgs = new List<string>();
        var devcontainerUpJson = """{"outcome":"success","containerId":"labeled-container","remoteUser":"vscode"}""";

        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((cmd, args, wd, ct) => capturedArgs.Add(args))
            .ReturnsAsync(new ProcessResult(0, devcontainerUpJson, ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner completed", ""));

        // Act
        await _service.ExecuteJobAsync(
            _testRegistration, 12345, "main", "token", "jit",
            null, CancellationToken.None, containerName: "my-app");

        // Assert - devcontainer up should include --id-label flags
        var devcontainerArgs = capturedArgs.First(a => a.Contains("up"));
        devcontainerArgs.Should().Contain("--id-label pks.runner.name=my-app");
        devcontainerArgs.Should().Contain("--id-label pks.runner.owner=testowner");
        devcontainerArgs.Should().Contain("--id-label pks.runner.repo=testrepo");
        devcontainerArgs.Should().NotContain("--remove-existing-container");
        devcontainerArgs.Should().Contain("--remote-env PKS_RUNNER=true");
    }

    [Fact]
    public async Task ExecuteJobAsync_WhenEphemeral_DoesNotAddIdLabels()
    {
        // Arrange
        var capturedArgs = new List<string>();
        var devcontainerUpJson = """{"outcome":"success","containerId":"ephemeral-container","remoteUser":"vscode"}""";

        _mockProcessRunner
            .Setup(r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("devcontainer", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((cmd, args, wd, ct) => capturedArgs.Add(args))
            .ReturnsAsync(new ProcessResult(0, devcontainerUpJson, ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner completed", ""));

        // Act
        await _service.ExecuteJobAsync(
            _testRegistration, 12345, "main", "token", "jit");

        // Assert - should use --remove-existing-container, NOT --id-label
        var devcontainerArgs = capturedArgs.First(a => a.Contains("up"));
        devcontainerArgs.Should().Contain("--remove-existing-container");
        devcontainerArgs.Should().NotContain("--id-label");
        devcontainerArgs.Should().Contain("--remote-env PKS_RUNNER=true");
    }

    #endregion

    #region DiscoverNamedContainersAsync

    [Fact]
    public async Task DiscoverNamedContainersAsync_FindsLabeledContainers()
    {
        // Arrange - docker ps with label filter returns one container
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("ps") && a.Contains("label=pks.runner.name")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "container1\ncontainer2", ""));

        // container1 inspect returns labels
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("inspect") && a.Contains("container1")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "my-container|testowner|testrepo", ""));

        // container2 inspect returns labels
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("inspect") && a.Contains("container2")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "other-app|otherowner|otherrepo", ""));

        // Act
        var result = await _service.DiscoverNamedContainersAsync();

        // Assert
        result.Should().HaveCount(2);
        result[0].Name.Should().Be("my-container");
        result[0].Owner.Should().Be("testowner");
        result[0].Repository.Should().Be("testrepo");
        result[0].ContainerId.Should().Be("container1");
        result[1].Name.Should().Be("other-app");
        result[1].ContainerId.Should().Be("container2");
    }

    [Fact]
    public async Task DiscoverNamedContainersAsync_WhenNoContainersRunning_ReturnsEmptyList()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("ps")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        var result = await _service.DiscoverNamedContainersAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverNamedContainersAsync_WhenDockerPsFails_ReturnsEmptyList()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("ps")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "Cannot connect to Docker daemon"));

        // Act
        var result = await _service.DiscoverNamedContainersAsync();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverNamedContainersAsync_SkipsContainersWithFailedInspect()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("ps") && a.Contains("label=pks.runner.name")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "container1", ""));

        // inspect fails
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.Is<string>(a => a.Contains("inspect") && a.Contains("container1")), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "Error"));

        // Act
        var result = await _service.DiscoverNamedContainersAsync();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region ExecuteJobInExistingContainerAsync

    [Fact]
    public async Task ExecuteJobInExistingContainerAsync_SkipsCloneAndDevcontainerUp()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner.Listener completed", ""));

        // Act
        var result = await _service.ExecuteJobInExistingContainerAsync(
            _testRegistration,
            runId: 100,
            jobId: 200,
            branch: "main",
            containerId: "existing-container-abc",
            clonePath: "/workspace/repo",
            containerName: "my-container",
            encodedJitConfig: "encoded_jit");

        // Assert - git clone should NOT be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("git", It.Is<string>(a => a.Contains("clone")), null, It.IsAny<CancellationToken>()),
            Times.Never);

        // devcontainer up should NOT be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("devcontainer", It.IsAny<string>(), null, It.IsAny<CancellationToken>()),
            Times.Never);

        // docker exec (runner install) SHOULD be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains("mkdir")), null, It.IsAny<CancellationToken>()),
            Times.Once);

        // docker exec (run.sh) SHOULD be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains("run.sh")), null, It.IsAny<CancellationToken>()),
            Times.Once);

        result.Status.Should().Be(RunnerJobStatus.Completed);
    }

    [Fact]
    public async Task ExecuteJobInExistingContainerAsync_UsesUniqueRunnerPath()
    {
        // Arrange
        var capturedArgs = new List<string>();
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((cmd, args, wd, ct) => capturedArgs.Add(args))
            .ReturnsAsync(new ProcessResult(0, "Runner.Listener completed", ""));

        long jobId = 42;

        // Act
        await _service.ExecuteJobInExistingContainerAsync(
            _testRegistration,
            runId: 100,
            jobId: jobId,
            branch: "main",
            containerId: "container-xyz",
            clonePath: "/workspace/repo",
            containerName: "my-container",
            encodedJitConfig: "encoded_jit");

        // Assert - the runner install path should contain the jobId
        capturedArgs.Should().Contain(a => a.Contains($"/tmp/actions-runner-{jobId}"));
    }

    [Fact]
    public async Task ExecuteJobInExistingContainerAsync_DoesNotCleanupContainer()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner.Listener completed", ""));

        // Act
        await _service.ExecuteJobInExistingContainerAsync(
            _testRegistration,
            runId: 100,
            jobId: 200,
            branch: "main",
            containerId: "container-xyz",
            clonePath: "/workspace/repo",
            containerName: "my-container",
            encodedJitConfig: "encoded_jit");

        // Assert - docker rm should NOT be called (container stays alive)
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.StartsWith("rm")), null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteJobInExistingContainerAsync_CleansUpRunnerDirectory()
    {
        // Arrange
        long jobId = 555;
        _mockProcessRunner
            .Setup(r => r.RunAsync("docker", It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Runner.Listener completed", ""));

        // Act
        await _service.ExecuteJobInExistingContainerAsync(
            _testRegistration,
            runId: 100,
            jobId: jobId,
            branch: "main",
            containerId: "container-xyz",
            clonePath: "/workspace/repo",
            containerName: "my-container",
            encodedJitConfig: "encoded_jit");

        // Assert - docker exec rm -rf for the runner directory SHOULD be called
        _mockProcessRunner.Verify(
            r => r.RunAsync("docker", It.Is<string>(a => a.Contains($"rm -rf /tmp/actions-runner-{jobId}")), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion
}
