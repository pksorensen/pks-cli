using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Infrastructure.Services.Runner;
using Xunit;

using ProcessResult = PKS.Infrastructure.Services.Runner.ProcessResult;

namespace PKS.CLI.Tests.Services.Firecracker;

/// <summary>
/// Tests for FirecrackerService covering prerequisite checks, rootfs preparation,
/// VM cleanup, and SSH command execution. Uses mocked IProcessRunner for shell
/// command isolation.
/// </summary>
public class FirecrackerServiceTests : TestBase
{
    private readonly Mock<IProcessRunner> _mockProcessRunner;
    private readonly Mock<ILogger<FirecrackerService>> _mockLogger;
    private readonly FirecrackerService _service;
    private readonly string _testDirectory;

    public FirecrackerServiceTests()
    {
        _mockProcessRunner = new Mock<IProcessRunner>();
        _mockLogger = new Mock<ILogger<FirecrackerService>>();
        _service = new FirecrackerService(_mockProcessRunner.Object, _mockLogger.Object);
        _testDirectory = CreateTempDirectory();
    }

    #region CheckPrerequisitesAsync

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenKvmAndFirecrackerAvailable_ReturnsTrue()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "test",
                "-w /dev/kvm",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "firecracker",
                "--version",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Firecracker v1.6.0", ""));

        // Act
        var (kvmAvailable, firecrackerInstalled, firecrackerVersion) =
            await _service.CheckPrerequisitesAsync();

        // Assert
        kvmAvailable.Should().BeTrue();
        firecrackerInstalled.Should().BeTrue();
        firecrackerVersion.Should().Be("Firecracker v1.6.0");
    }

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenKvmMissing_ReturnsFalse()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "test",
                "-w /dev/kvm",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "firecracker",
                "--version",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "Firecracker v1.6.0", ""));

        // Act
        var (kvmAvailable, _, _) = await _service.CheckPrerequisitesAsync();

        // Assert
        kvmAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task CheckPrerequisitesAsync_WhenFirecrackerMissing_ReturnsFalse()
    {
        // Arrange
        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "test",
                "-w /dev/kvm",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "firecracker",
                "--version",
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "firecracker: command not found"));

        // Act
        var (kvmAvailable, firecrackerInstalled, firecrackerVersion) =
            await _service.CheckPrerequisitesAsync();

        // Assert
        kvmAvailable.Should().BeTrue();
        firecrackerInstalled.Should().BeFalse();
        firecrackerVersion.Should().BeEmpty();
    }

    #endregion

    #region PrepareRootfsAsync

    [Fact]
    public async Task PrepareRootfsAsync_CopiesBaseImage()
    {
        // Arrange
        var baseRootfsPath = Path.Combine(_testDirectory, "base-rootfs.ext4");
        await File.WriteAllTextAsync(baseRootfsPath, "fake rootfs content");
        var vmId = "test-vm-001";
        var expectedVmDir = Path.Combine(_testDirectory, "vms", vmId);
        var expectedRootfsPath = Path.Combine(expectedVmDir, "rootfs.ext4");

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "mkdir",
                It.Is<string>(s => s.Contains(expectedVmDir)),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "cp",
                It.Is<string>(s => s.Contains("--reflink=auto")
                    && s.Contains(baseRootfsPath)
                    && s.Contains(expectedRootfsPath)),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        var result = await _service.PrepareRootfsAsync(baseRootfsPath, vmId, _testDirectory);

        // Assert
        result.Should().Be(expectedRootfsPath);

        _mockProcessRunner.Verify(r => r.RunAsync(
            "cp",
            It.Is<string>(s => s.Contains("--reflink=auto")
                && s.Contains(baseRootfsPath)
                && s.Contains(expectedRootfsPath)),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrepareRootfsAsync_CreatesVmDirectory()
    {
        // Arrange
        var baseRootfsPath = Path.Combine(_testDirectory, "base-rootfs.ext4");
        var vmId = "test-vm-002";
        var expectedVmDir = Path.Combine(_testDirectory, "vms", vmId);

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "mkdir",
                It.Is<string>(s => s.Contains($"-p {expectedVmDir}")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "cp",
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        await _service.PrepareRootfsAsync(baseRootfsPath, vmId, _testDirectory);

        // Assert
        _mockProcessRunner.Verify(r => r.RunAsync(
            "mkdir",
            It.Is<string>(s => s.Contains($"-p {expectedVmDir}")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region CleanupVmAsync

    [Fact]
    public async Task CleanupVmAsync_RemovesVmDirectory()
    {
        // Arrange
        var vmId = "test-vm-cleanup";
        var expectedVmDir = Path.Combine(_testDirectory, "vms", vmId);

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "rm",
                It.Is<string>(s => s.Contains($"-rf {expectedVmDir}")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "", ""));

        // Act
        await _service.CleanupVmAsync(vmId, _testDirectory);

        // Assert
        _mockProcessRunner.Verify(r => r.RunAsync(
            "rm",
            It.Is<string>(s => s.Contains($"-rf {expectedVmDir}")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region ExecuteInVmAsync

    [Fact]
    public async Task ExecuteInVmAsync_RunsSshCommand()
    {
        // Arrange
        var vmIp = "172.16.0.2";
        var sshKeyPath = "/home/testuser/.ssh/id_rsa";
        var command = "echo hello";

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "ssh",
                It.Is<string>(s =>
                    s.Contains("-o StrictHostKeyChecking=no") &&
                    s.Contains("-o UserKnownHostsFile=/dev/null") &&
                    s.Contains("-o ConnectTimeout=10") &&
                    s.Contains($"-i {sshKeyPath}") &&
                    s.Contains($"root@{vmIp}") &&
                    s.Contains($"'{command}'")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(0, "hello", ""));

        // Act
        var result = await _service.ExecuteInVmAsync(vmIp, sshKeyPath, command);

        // Assert
        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Be("hello");
        result.StandardError.Should().BeEmpty();

        _mockProcessRunner.Verify(r => r.RunAsync(
            "ssh",
            It.Is<string>(s =>
                s.Contains("-o StrictHostKeyChecking=no") &&
                s.Contains("-o UserKnownHostsFile=/dev/null") &&
                s.Contains("-o ConnectTimeout=10") &&
                s.Contains($"-i {sshKeyPath}") &&
                s.Contains($"root@{vmIp}") &&
                s.Contains($"'{command}'")),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteInVmAsync_WhenCommandFails_ReturnsNonZeroExitCode()
    {
        // Arrange
        var vmIp = "172.16.0.2";
        var sshKeyPath = "/home/testuser/.ssh/id_rsa";
        var command = "exit 1";

        _mockProcessRunner
            .Setup(r => r.RunAsync(
                "ssh",
                It.Is<string>(s => s.Contains($"root@{vmIp}")),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(1, "", "error"));

        // Act
        var result = await _service.ExecuteInVmAsync(vmIp, sshKeyPath, command);

        // Assert
        result.ExitCode.Should().Be(1);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Be("error");
    }

    #endregion

    public override void Dispose()
    {
        _service.Dispose();

        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in test disposal
        }

        base.Dispose();
    }
}
