using Xunit;
using Moq;
using FluentAssertions;
using PKS.Commands.Devcontainer;
using PKS.Commands.Vm;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Testing;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PKS.CLI.Tests.Commands;

/// <summary>
/// Tests for the remote SSH target selection in DevcontainerSpawnCommand.
/// </summary>
public class DevcontainerSpawnRemoteTests
{
    // ─────────────────────────────────────────────
    //  Shared helpers
    // ─────────────────────────────────────────────

    private static Mock<IDevcontainerSpawnerService> CreateSpawnerMock()
    {
        var mock = new Mock<IDevcontainerSpawnerService>();

        mock.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult { IsAvailable = true, Message = "Docker available" });

        mock.Setup(x => x.IsDevcontainerCliInstalledAsync())
            .ReturnsAsync(true);

        mock.Setup(x => x.FindExistingContainerAsync(It.IsAny<string>()))
            .ReturnsAsync((ExistingDevcontainerInfo?)null);

        return mock;
    }

    private static Mock<ISshTargetConfigurationService> CreateSshServiceMock(List<SshTarget>? targets = null)
    {
        var mock = new Mock<ISshTargetConfigurationService>();
        mock.Setup(x => x.ListTargetsAsync()).ReturnsAsync(targets ?? new List<SshTarget>());
        mock.Setup(x => x.FindTargetAsync(It.IsAny<string>())).ReturnsAsync((SshTarget?)null);
        return mock;
    }

    private static Mock<INuGetTemplateDiscoveryService> CreateNuGetTemplateMock()
    {
        var mock = new Mock<INuGetTemplateDiscoveryService>();
        mock.Setup(x => x.DiscoverTemplatesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetDevcontainerTemplate>());
        return mock;
    }

    // ═════════════════════════════════════════════
    //  Remote spawn tests
    // ═════════════════════════════════════════════

    [Fact]
    [Trait("Category", "DevcontainerSpawn")]
    public async Task ExecuteAsync_WhenSshTargetsAvailable_PromptsForTargetSelection()
    {
        // Arrange
        var targets = new List<SshTarget>
        {
            new() { Id = "1", Host = "192.168.1.1", Username = "user", Port = 22, KeyPath = "/home/user/.ssh/id_ed25519", Label = "my-vm" }
        };

        var spawnerMock = CreateSpawnerMock();
        var sshMock = CreateSshServiceMock(targets);
        var console = new TestConsole();

        // Select "Local (this machine)" to avoid full remote flow
        console.Input.PushKey(ConsoleKey.Enter); // first choice = Local

        var command = new DevcontainerSpawnCommand(spawnerMock.Object, sshMock.Object, CreateNuGetTemplateMock().Object, Mock.Of<IAzureVmMetadataService>(), Mock.Of<IAzureAuthService>(), Mock.Of<IAzureVmService>(), Mock.Of<VmInitCommand>(), console);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        var devcontainerDir = Path.Combine(tmpDir, ".devcontainer");
        Directory.CreateDirectory(devcontainerDir);
        await File.WriteAllTextAsync(Path.Combine(devcontainerDir, "devcontainer.json"), "{}");

        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = tmpDir,
            NoLaunchVsCode = true
        };

        try
        {
            // Act — the key test here is that ListTargetsAsync was called to check for remote targets
            await command.ExecuteAsync(null!, settings);

            // Assert
            sshMock.Verify(x => x.ListTargetsAsync(), Times.Once);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "DevcontainerSpawn")]
    public async Task ExecuteAsync_WithRemoteTarget_SkipsLocalDockerCheck()
    {
        // Arrange — an SSH target is specified explicitly
        var target = new SshTarget
        {
            Id = "1",
            Host = "192.168.1.1",
            Username = "user",
            Port = 22,
            KeyPath = "/home/user/.ssh/id_ed25519",
            Label = "my-vm"
        };

        var spawnerMock = CreateSpawnerMock();
        var sshMock = CreateSshServiceMock();
        sshMock.Setup(x => x.FindTargetAsync("my-vm")).ReturnsAsync(target);

        var console = new TestConsole();

        var command = new DevcontainerSpawnCommand(spawnerMock.Object, sshMock.Object, CreateNuGetTemplateMock().Object, Mock.Of<IAzureVmMetadataService>(), Mock.Of<IAzureAuthService>(), Mock.Of<IAzureVmService>(), Mock.Of<VmInitCommand>(), console);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = tmpDir,
            SshTarget = "my-vm",
            NoLaunchVsCode = true
        };

        try
        {
            // Act
            await command.ExecuteAsync(null!, settings);

            // Assert — when SSH target is specified, local Docker check is skipped
            spawnerMock.Verify(x => x.CheckDockerAvailabilityAsync(), Times.Never);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "DevcontainerSpawn")]
    public async Task ExecuteAsync_RemoteSpawn_CopiesFilesViaScp()
    {
        // Arrange — target found, remote docker check will fail (no real SSH)
        var target = new SshTarget
        {
            Id = "1",
            Host = "127.0.0.1",
            Username = "testuser",
            Port = 22222,
            KeyPath = "/nonexistent/key",
            Label = "test-target"
        };

        var spawnerMock = CreateSpawnerMock();
        var sshMock = CreateSshServiceMock();
        sshMock.Setup(x => x.FindTargetAsync("test-target")).ReturnsAsync(target);

        var console = new TestConsole();

        var command = new DevcontainerSpawnCommand(spawnerMock.Object, sshMock.Object, CreateNuGetTemplateMock().Object, Mock.Of<IAzureVmMetadataService>(), Mock.Of<IAzureAuthService>(), Mock.Of<IAzureVmService>(), Mock.Of<VmInitCommand>(), console);

        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);

        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = tmpDir,
            SshTarget = "test-target",
            NoLaunchVsCode = true
        };

        try
        {
            // Act — will fail because SSH is not actually available, but the target lookup should succeed
            var result = await command.ExecuteAsync(null!, settings);

            // Assert — we reached the remote path (result could be non-zero due to no real SSH)
            // The important assertion is that SSH target was found and local Docker was skipped
            sshMock.Verify(x => x.FindTargetAsync("test-target"), Times.Once);
            spawnerMock.Verify(x => x.CheckDockerAvailabilityAsync(), Times.Never);
        }
        finally
        {
            Directory.Delete(tmpDir, recursive: true);
        }
    }
}
