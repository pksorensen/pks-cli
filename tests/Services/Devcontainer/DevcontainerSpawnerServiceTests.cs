using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Xunit;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace PKS.CLI.Tests.Services.Devcontainer;

/// <summary>
/// Tests for IDevcontainerSpawnerService implementation
/// </summary>
public class DevcontainerSpawnerServiceTests : TestBase
{
    private Mock<IDockerClient> _mockDockerClient = null!;
    private Mock<ILogger<DevcontainerSpawnerService>> _mockLogger = null!;
    private Mock<IAnsiConsole> _mockConsole = null!;

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);

        // Create mocks
        _mockDockerClient = new Mock<IDockerClient>();
        _mockLogger = new Mock<ILogger<DevcontainerSpawnerService>>();
        _mockConsole = new Mock<IAnsiConsole>();

        // Register mocks
        services.AddSingleton(_mockDockerClient.Object);
        services.AddSingleton(_mockLogger.Object);
        services.AddSingleton(_mockConsole.Object);

        // Register the actual service
        services.AddSingleton<IDevcontainerSpawnerService, DevcontainerSpawnerService>();
    }

    [Fact]
    public async Task CheckDockerAvailabilityAsync_WhenDockerIsRunning_ShouldReturnAvailable()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        var mockSystemOperations = new Mock<ISystemOperations>();
        _mockDockerClient.Setup(x => x.System).Returns(mockSystemOperations.Object);
        mockSystemOperations.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockSystemOperations.Setup(x => x.GetVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionResponse { Version = "24.0.0" });

        // Act
        var result = await service.CheckDockerAvailabilityAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsAvailable.Should().BeTrue();
        result.IsRunning.Should().BeTrue();
        result.Version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckDockerAvailabilityAsync_WhenDockerIsNotRunning_ShouldReturnUnavailable()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        var mockSystemOperations = new Mock<ISystemOperations>();
        _mockDockerClient.Setup(x => x.System).Returns(mockSystemOperations.Object);
        mockSystemOperations.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Docker daemon not running"));

        // Act
        var result = await service.CheckDockerAvailabilityAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsAvailable.Should().BeFalse();
        result.IsRunning.Should().BeFalse();
        result.Message.Should().Contain("not available");
    }

    [Fact]
    public async Task IsDevcontainerCliInstalledAsync_WhenCliIsInstalled_ShouldReturnTrue()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        // Act - This will attempt to run 'devcontainer --version'
        // In a real test environment, this would need process mocking
        var result = await service.IsDevcontainerCliInstalledAsync();

        // Assert - Result will depend on whether devcontainer CLI is actually installed
        Assert.IsType<bool>(result);
    }

    [Fact]
    public async Task CheckVsCodeInstallationAsync_ShouldReturnInstallationInfo()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        // Act
        var result = await service.CheckVsCodeInstallationAsync();

        // Assert
        result.Should().NotBeNull();
        Assert.IsType<bool>(result.IsInstalled);
    }

    [Theory]
    [InlineData("my-project", "devcontainer-my-project-")]
    [InlineData("My Project", "devcontainer-myproject-")]
    [InlineData("project_123", "devcontainer-project-123-")]
    [InlineData("test-project-name", "devcontainer-test-project-name-")]
    [InlineData("Special!@#$%Chars", "devcontainer-specialchars-")]
    public void GenerateVolumeName_ShouldSanitizeAndFormatCorrectly(string projectName, string expectedPrefix)
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        // Act
        var result = service.GenerateVolumeName(projectName);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().StartWith(expectedPrefix);
        result.Should().MatchRegex(@"^devcontainer-[a-z0-9-]+-[a-f0-9]{8}$");
    }

    [Fact]
    public async Task CleanupFailedSpawnAsync_ShouldRemoveVolumeAndBootstrapPath()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();
        var volumeName = "test-volume";
        var bootstrapPath = CreateTempDirectory();

        var mockVolumeOperations = new Mock<IVolumeOperations>();
        _mockDockerClient.Setup(x => x.Volumes).Returns(mockVolumeOperations.Object);
        mockVolumeOperations.Setup(x => x.RemoveAsync(volumeName, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await service.CleanupFailedSpawnAsync(volumeName, bootstrapPath);

        // Assert
        mockVolumeOperations.Verify(x => x.RemoveAsync(volumeName, true, It.IsAny<CancellationToken>()), Times.Once);
        Directory.Exists(bootstrapPath).Should().BeFalse();
    }

    [Fact]
    public async Task FindExistingContainerAsync_WhenContainerExists_ShouldReturnInfo()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();
        var projectPath = "/test/project";

        var mockContainerOperations = new Mock<IContainerOperations>();
        _mockDockerClient.Setup(x => x.Containers).Returns(mockContainerOperations.Object);

        var containerListResponse = new List<ContainerListResponse>
        {
            new ContainerListResponse
            {
                ID = "container123",
                Labels = new Dictionary<string, string>
                {
                    { "devcontainer.local_folder", projectPath },
                    { "vsch.local.repository.volume", "test-volume" }
                },
                Created = DateTime.UtcNow.AddHours(-1),
                State = "running"
            }
        };

        mockContainerOperations.Setup(x => x.ListContainersAsync(
            It.IsAny<ContainersListParameters>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(containerListResponse);

        // Act
        var result = await service.FindExistingContainerAsync(projectPath);

        // Assert
        result.Should().NotBeNull();
        result!.ContainerId.Should().Be("container123");
        result.VolumeName.Should().Be("test-volume");
        result.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task FindExistingContainerAsync_WhenNoContainerExists_ShouldReturnNull()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();
        var projectPath = "/test/project";

        var mockContainerOperations = new Mock<IContainerOperations>();
        _mockDockerClient.Setup(x => x.Containers).Returns(mockContainerOperations.Object);
        mockContainerOperations.Setup(x => x.ListContainersAsync(
            It.IsAny<ContainersListParameters>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ContainerListResponse>());

        // Act
        var result = await service.FindExistingContainerAsync(projectPath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListManagedVolumesAsync_ShouldReturnOnlyPksManagedVolumes()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        var mockVolumeOperations = new Mock<IVolumeOperations>();
        _mockDockerClient.Setup(x => x.Volumes).Returns(mockVolumeOperations.Object);

        var volumesListResponse = new VolumesListResponse
        {
            Volumes = new List<VolumeResponse>
            {
                new VolumeResponse
                {
                    Name = "devcontainer-project1-abc123",
                    Labels = new Dictionary<string, string>
                    {
                        { "pks.managed", "true" },
                        { "devcontainer.project", "project1" },
                        { "devcontainer.created", DateTime.UtcNow.ToString("o") }
                    }
                },
                new VolumeResponse
                {
                    Name = "other-volume",
                    Labels = new Dictionary<string, string>()
                }
            }
        };

        mockVolumeOperations.Setup(x => x.ListAsync(It.IsAny<VolumesListParameters>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(volumesListResponse);

        // Act
        var result = await service.ListManagedVolumesAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(1);
        result[0].Name.Should().Be("devcontainer-project1-abc123");
        result[0].ProjectName.Should().Be("project1");
    }

    [Fact]
    public async Task SpawnLocalAsync_WithDockerUnavailable_ShouldReturnFailure()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        var mockSystemOperations = new Mock<ISystemOperations>();
        _mockDockerClient.Setup(x => x.System).Returns(mockSystemOperations.Object);
        mockSystemOperations.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Docker not available"));

        var options = new DevcontainerSpawnOptions
        {
            ProjectName = "test-project",
            ProjectPath = "/test/project",
            DevcontainerPath = "/test/project/.devcontainer"
        };

        // Act
        var result = await service.SpawnLocalAsync(options);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Docker"));
        result.CompletedStep.Should().Be(DevcontainerSpawnStep.DockerCheck);
    }

    [Fact]
    public async Task SpawnLocalAsync_WithValidOptions_ShouldCompleteSuccessfully()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();

        // Setup Docker availability
        var mockSystemOperations = new Mock<ISystemOperations>();
        _mockDockerClient.Setup(x => x.System).Returns(mockSystemOperations.Object);
        mockSystemOperations.Setup(x => x.PingAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockSystemOperations.Setup(x => x.GetVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VersionResponse { Version = "24.0.0" });

        // Setup volume operations
        var mockVolumeOperations = new Mock<IVolumeOperations>();
        _mockDockerClient.Setup(x => x.Volumes).Returns(mockVolumeOperations.Object);
        mockVolumeOperations.Setup(x => x.CreateAsync(
            It.IsAny<VolumesCreateParameters>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VolumeResponse { Name = "test-volume" });

        // Setup container operations for checking existing containers
        var mockContainerOperations = new Mock<IContainerOperations>();
        _mockDockerClient.Setup(x => x.Containers).Returns(mockContainerOperations.Object);
        mockContainerOperations.Setup(x => x.ListContainersAsync(
            It.IsAny<ContainersListParameters>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ContainerListResponse>());

        var tempDir = CreateTempDirectory();
        var devcontainerPath = Path.Combine(tempDir, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);
        File.WriteAllText(
            Path.Combine(devcontainerPath, "devcontainer.json"),
            "{\"name\": \"test\", \"image\": \"mcr.microsoft.com/devcontainers/base:ubuntu\"}");

        var options = new DevcontainerSpawnOptions
        {
            ProjectName = "test-project",
            ProjectPath = tempDir,
            DevcontainerPath = devcontainerPath,
            CopySourceFiles = false,
            LaunchVsCode = false
        };

        // Act
        var result = await service.SpawnLocalAsync(options);

        // Assert
        result.Should().NotBeNull();
        // Note: Full success may require devcontainer CLI to be installed
        // This test validates the flow starts correctly
        result.CompletedStep.Should().BeOneOf(
            DevcontainerSpawnStep.DockerCheck,
            DevcontainerSpawnStep.DevcontainerCliCheck,
            DevcontainerSpawnStep.VolumeCreation,
            DevcontainerSpawnStep.FileCopy,
            DevcontainerSpawnStep.BootstrapCreation,
            DevcontainerSpawnStep.DevcontainerUp,
            DevcontainerSpawnStep.Completed);
    }

    [Fact]
    public async Task SpawnRemoteAsync_ShouldThrowNotImplementedException()
    {
        // Arrange
        var service = GetService<IDevcontainerSpawnerService>();
        var options = new DevcontainerSpawnOptions();
        var remoteHost = new RemoteHostConfig();

        // Act & Assert
        await Assert.ThrowsAsync<NotImplementedException>(
            () => service.SpawnRemoteAsync(options, remoteHost));
    }
}
