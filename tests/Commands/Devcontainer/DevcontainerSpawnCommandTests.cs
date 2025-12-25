using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Commands.Devcontainer;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace PKS.CLI.Tests.Commands.Devcontainer;

/// <summary>
/// Tests for DevcontainerSpawnCommand
/// </summary>
public class DevcontainerSpawnCommandTests : TestBase
{
    private Mock<IDevcontainerSpawnerService> _mockSpawnerService = null!;
    private TestConsole _console = null!;
    private string _testProjectPath = null!;

    public DevcontainerSpawnCommandTests()
    {
        InitializeMocks();
    }

    private void InitializeMocks()
    {
        _mockSpawnerService = new Mock<IDevcontainerSpawnerService>();
        _console = new TestConsole();
        _testProjectPath = Path.Combine(Path.GetTempPath(), "test-project-" + Guid.NewGuid().ToString()[..8]);

        // Setup default successful responses
        _mockSpawnerService.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult
            {
                IsAvailable = true,
                IsRunning = true,
                Version = "24.0.0",
                Message = "Docker is available and running"
            });

        _mockSpawnerService.Setup(x => x.IsDevcontainerCliInstalledAsync())
            .ReturnsAsync(true);

        _mockSpawnerService.Setup(x => x.GenerateVolumeName(It.IsAny<string>()))
            .Returns<string>(projectName => $"pks-{projectName}-{DateTime.UtcNow:yyyyMMddHHmmss}");

        _mockSpawnerService.Setup(x => x.FindExistingContainerAsync(It.IsAny<string>()))
            .ReturnsAsync((ExistingDevcontainerInfo?)null);

        _mockSpawnerService.Setup(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()))
            .ReturnsAsync(new DevcontainerSpawnResult
            {
                Success = true,
                Message = "Container spawned successfully",
                ContainerId = "abc123def456",
                VolumeName = "pks-test-project-vol",
                Duration = TimeSpan.FromSeconds(30),
                CompletedStep = DevcontainerSpawnStep.Completed
            });
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);

        if (_mockSpawnerService == null)
        {
            InitializeMocks();
        }

        services.AddSingleton<IDevcontainerSpawnerService>(_mockSpawnerService.Object);
        services.AddSingleton<IAnsiConsole>(_console);
        services.AddTransient<DevcontainerSpawnCommand>();
    }

    [Fact]
    public async Task ExecuteAsync_WithValidProject_ShouldSpawnSuccessfully()
    {
        // Arrange
        SetupTestProject();
        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            VolumeName = "test-volume",
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.Is<DevcontainerSpawnOptions>(
            opts => opts.ProjectPath == _testProjectPath &&
                   opts.VolumeName == "test-volume" &&
                   opts.LaunchVsCode == false
        )), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithDefaultCurrentDirectory_ShouldUseCurrentDirectory()
    {
        // Arrange
        SetupTestProject();
        var originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectPath);

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.Is<DevcontainerSpawnOptions>(
            opts => opts.ProjectPath == _testProjectPath
        )), Times.Once);

        Directory.SetCurrentDirectory(originalDir);
        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithNonExistentPath_ShouldReturnError()
    {
        // Arrange
        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = "/path/that/does/not/exist"
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutDevcontainerJson_ShouldReturnError()
    {
        // Arrange
        var projectPath = Path.Combine(Path.GetTempPath(), "no-devcontainer-" + Guid.NewGuid().ToString()[..8]);
        Directory.CreateDirectory(projectPath);

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = projectPath
        };

        try
        {
            // Act
            var result = await ExecuteCommandAsync(command, settings);

            // Assert
            result.Should().Be(1);
            _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Never);
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithExistingContainer_ShouldPromptForConfirmation()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.FindExistingContainerAsync(It.IsAny<string>()))
            .ReturnsAsync(new ExistingDevcontainerInfo
            {
                ContainerId = "existing123",
                VolumeName = "existing-volume",
                Created = DateTime.UtcNow.AddDays(-1),
                IsRunning = true
            });

        // Setup console to simulate user declining
        _console.Profile.Capabilities.Interactive = true;
        _console.Input.PushTextWithEnter("n"); // User says "no"

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0); // Returns 0 for cancelled operation
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Never);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithForceFlag_ShouldSkipExistingContainerCheck()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.FindExistingContainerAsync(It.IsAny<string>()))
            .ReturnsAsync(new ExistingDevcontainerInfo
            {
                ContainerId = "existing123",
                VolumeName = "existing-volume",
                Created = DateTime.UtcNow.AddDays(-1),
                IsRunning = true
            });

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            Force = true,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.FindExistingContainerAsync(It.IsAny<string>()), Times.Never);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithDockerNotAvailable_ShouldReturnError()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult
            {
                IsAvailable = false,
                IsRunning = false,
                Message = "Docker is not installed or not running"
            });

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Never);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithDevcontainerCliNotInstalled_ShouldReturnError()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.IsDevcontainerCliInstalledAsync())
            .ReturnsAsync(false);

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Never);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithSpawnFailure_ShouldReturnErrorAndDisplayMessage()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()))
            .ReturnsAsync(new DevcontainerSpawnResult
            {
                Success = false,
                Message = "Failed to create Docker volume",
                Errors = new List<string> { "Volume already exists", "Permission denied" },
                CompletedStep = DevcontainerSpawnStep.VolumeCreation
            });

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithNoCopySourceFlag_ShouldSetCopySourceFilesToFalse()
    {
        // Arrange
        SetupTestProject();
        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoCopySource = true,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.Is<DevcontainerSpawnOptions>(
            opts => opts.CopySourceFiles == false
        )), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithCustomVolumeName_ShouldUseProvidedVolumeName()
    {
        // Arrange
        SetupTestProject();
        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            VolumeName = "my-custom-volume",
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.Is<DevcontainerSpawnOptions>(
            opts => opts.VolumeName == "my-custom-volume"
        )), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithLaunchVsCode_ShouldSetLaunchVsCodeToTrue()
    {
        // Arrange
        SetupTestProject();
        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = false // Explicitly enable VS Code launch
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(0);
        _mockSpawnerService.Verify(x => x.SpawnLocalAsync(It.Is<DevcontainerSpawnOptions>(
            opts => opts.LaunchVsCode == true
        )), Times.Once);

        CleanupTestProject();
    }

    [Fact]
    public async Task ExecuteAsync_WithException_ShouldReturnErrorAndDisplayException()
    {
        // Arrange
        SetupTestProject();
        _mockSpawnerService.Setup(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error occurred"));

        var command = GetService<DevcontainerSpawnCommand>();
        var settings = new DevcontainerSpawnCommand.Settings
        {
            ProjectPath = _testProjectPath,
            NoLaunchVsCode = true
        };

        // Act
        var result = await ExecuteCommandAsync(command, settings);

        // Assert
        result.Should().Be(1);

        CleanupTestProject();
    }

    private void SetupTestProject()
    {
        // Create test project structure
        Directory.CreateDirectory(_testProjectPath);
        var devcontainerPath = Path.Combine(_testProjectPath, ".devcontainer");
        Directory.CreateDirectory(devcontainerPath);

        // Create minimal devcontainer.json
        var devcontainerJson = Path.Combine(devcontainerPath, "devcontainer.json");
        File.WriteAllText(devcontainerJson, @"{
  ""name"": ""Test Container"",
  ""image"": ""mcr.microsoft.com/dotnet/sdk:8.0""
}");
    }

    private void CleanupTestProject()
    {
        try
        {
            if (Directory.Exists(_testProjectPath))
                Directory.Delete(_testProjectPath, true);
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private async Task<int> ExecuteCommandAsync(DevcontainerSpawnCommand command, DevcontainerSpawnCommand.Settings settings)
    {
        var context = new CommandContext(Mock.Of<IRemainingArguments>(), "devcontainer", null);
        return await command.ExecuteAsync(context, settings);
    }
}
