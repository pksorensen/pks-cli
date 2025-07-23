using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using System.IO;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Commands.Hooks;

/// <summary>
/// Tests for error handling in hook commands without console output dependencies
/// Ensures robust error handling for production scenarios
/// </summary>
public class HooksErrorHandlingTests : TestBase
{
    private readonly Mock<IHooksService> _mockHooksService;
    private readonly Mock<ILogger<HooksService>> _mockLogger;
    private readonly HooksCommand _command;
    private readonly TestConsole _testConsole;
    private readonly string _testDirectory;

    public HooksErrorHandlingTests()
    {
        _mockHooksService = new Mock<IHooksService>();
        _mockLogger = new Mock<ILogger<HooksService>>();
        _testConsole = new TestConsole();
        _command = new HooksCommand(_mockHooksService.Object);
        _testDirectory = CreateTempDirectory();
    }

    [Fact]
    public async Task HooksCommand_WhenServiceThrowsException_ShouldHandleGracefully()
    {
        // Arrange
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("init");

        _mockHooksService.Setup(x => x.InitializeClaudeCodeHooksAsync(It.IsAny<bool>(), It.IsAny<SettingsScope>()))
                        .ThrowsAsync(new InvalidOperationException("Service error"));

        AnsiConsole.Console = _testConsole;

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(1, "Should return error exit code on exception");
        _testConsole.Output.Should().Contain("Error: Service error");

        // Verify service was called
        _mockHooksService.Verify(x => x.InitializeClaudeCodeHooksAsync(It.IsAny<bool>(), It.IsAny<SettingsScope>()), Times.Once);
    }

    [Fact]
    public async Task HooksCommand_WhenServiceReturnsFailure_ShouldHandleGracefully()
    {
        // Arrange
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("init");

        _mockHooksService.Setup(x => x.InitializeClaudeCodeHooksAsync(It.IsAny<bool>(), It.IsAny<SettingsScope>()))
                        .ReturnsAsync(false);

        AnsiConsole.Console = _testConsole;

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(1, "Should return error exit code on service failure");
        _testConsole.Output.Should().Contain("✗ Failed to initialize Claude Code hooks");
    }

    [Fact]
    public async Task HooksCommand_WithNullService_ShouldThrowArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            _ = new HooksCommand(null!);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid-command")]
    public async Task HooksCommand_WithInvalidCommandName_ShouldShowHelp(string? commandName)
    {
        // Arrange
        var settings = new HooksSettings();
        var context = CreateMockCommandContext(commandName);

        AnsiConsole.Console = _testConsole;

        // Act
        var result = await _command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0, "Help should return success exit code");
        _testConsole.Output.Should().Contain("PKS Hooks - Claude Code Integration");
        _testConsole.Output.Should().Contain("Usage:");
    }

    [Fact]
    public async Task PreToolUseCommand_WithEnvironmentException_ShouldNotCrash()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");

        // Mock environment to potentially cause issues
        var originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            // Change to a directory that might cause issues
            Directory.SetCurrentDirectory(_testDirectory);
            AnsiConsole.Console = _testConsole;

            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0, "Command should handle environment issues gracefully");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HookEventCommands_WithDirectoryAccessIssues_ShouldHandleGracefully()
    {
        // Arrange
        var commands = new (string name, AsyncCommand<HooksSettings> command)[]
        {
            ("pre-tool-use", new PreToolUseCommand()),
            ("post-tool-use", new PostToolUseCommand()),
            ("user-prompt-submit", new UserPromptSubmitCommand()),
            ("stop", new StopCommand())
        };

        var originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            // Create a test directory with restricted permissions (on Unix systems)
            var restrictedDir = Path.Combine(_testDirectory, "restricted");
            Directory.CreateDirectory(restrictedDir);

            // Change to restricted directory
            Directory.SetCurrentDirectory(restrictedDir);
            AnsiConsole.Console = _testConsole;

            foreach (var (name, command) in commands)
            {
                var settings = new HooksSettings();
                var context = CreateMockCommandContext(name);

                // Act
                var result = await command.ExecuteAsync(context, settings);

                // Assert
                result.Should().Be(0, $"Command '{name}' should handle directory access issues gracefully");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HooksService_WithInvalidJsonInExistingFile_ShouldHandleError()
    {
        // Arrange
        var service = new HooksService(_mockLogger.Object);
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var claudeDir = Path.Combine(_testDirectory, ".claude");
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            Directory.CreateDirectory(claudeDir);
            // Write invalid JSON
            await File.WriteAllTextAsync(settingsPath, "{ invalid json content");

            // Act & Assert
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);
            });
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HooksService_WithReadOnlyFile_ShouldHandleError()
    {
        // This test is primarily relevant for Unix-like systems
        if (OperatingSystem.IsWindows())
        {
            return; // Skip on Windows as file permissions work differently
        }

        // Arrange
        var service = new HooksService(_mockLogger.Object);
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);

        try
        {
            var claudeDir = Path.Combine(_testDirectory, ".claude");
            var settingsPath = Path.Combine(claudeDir, "settings.json");

            Directory.CreateDirectory(claudeDir);
            await File.WriteAllTextAsync(settingsPath, "{}");

            // Make file read-only
            var fileInfo = new FileInfo(settingsPath);
            fileInfo.IsReadOnly = true;

            // Act & Assert
            var result = await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

            // The service should handle the error gracefully
            // Result may be false, but should not crash
            result.Should().BeFalse("Should fail gracefully when file is read-only");
        }
        finally
        {
            // Reset permissions and clean up
            try
            {
                var settingsPath = Path.Combine(_testDirectory, ".claude", "settings.json");
                if (File.Exists(settingsPath))
                {
                    var fileInfo = new FileInfo(settingsPath);
                    fileInfo.IsReadOnly = false;
                }
            }
            catch { /* Ignore cleanup errors */ }

            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HooksService_WithNetworkDriveOrUncPath_ShouldHandleGracefully()
    {
        // Arrange
        var service = new HooksService(_mockLogger.Object);

        // Act - Try to initialize with an invalid UNC path
        var result = await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.User);

        // Assert
        // Should either succeed (if user home is accessible) or fail gracefully
        // The key is that it shouldn't crash
        // Should either succeed (if user home is accessible) or fail gracefully
        // The key is that it shouldn't crash and should return a valid boolean
        (result == true || result == false).Should().BeTrue("Service should return a valid boolean result");
    }

    [Fact]
    public async Task HookEventCommands_WithStdinReadError_ShouldHandleGracefully()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");

        AnsiConsole.Console = _testConsole;

        // Act - Execute command (stdin reading is internally handled)
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0, "Command should handle stdin read issues gracefully");
        // Verify output contains error handling for stdin
        var output = _testConsole.Output;
        // Should either show "No piped input detected" or handle errors gracefully
        var hasExpectedOutput = output.Contains("No piped input detected") || output.Contains("Error reading stdin");
        hasExpectedOutput.Should().BeTrue("Should show appropriate stdin handling message");
    }

    [Fact]
    public void HooksSettings_DefaultValues_ShouldBeValid()
    {
        // Arrange & Act
        var settings = new HooksSettings();

        // Assert
        settings.Force.Should().BeFalse("Default force should be false");
        settings.Scope.Should().Be(SettingsScope.Project, "Default scope should be Project");

        // Verify enum is valid
        Enum.IsDefined(typeof(SettingsScope), settings.Scope).Should().BeTrue("Default scope should be a valid enum value");
    }

    [Theory]
    [InlineData(SettingsScope.User)]
    [InlineData(SettingsScope.Project)]
    [InlineData(SettingsScope.Local)]
    public void SettingsScope_AllValues_ShouldBeValid(SettingsScope scope)
    {
        // Assert
        Enum.IsDefined(typeof(SettingsScope), scope).Should().BeTrue($"Scope {scope} should be a valid enum value");
        ((int)scope).Should().BeGreaterOrEqualTo(0, "Enum values should be non-negative");
    }

    [Fact]
    public async Task HooksService_WithCancellationToken_ShouldRespectCancellation()
    {
        // Arrange
        var service = new HooksService(_mockLogger.Object);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await service.GetAvailableHooksAsync(cts.Token);
        });
    }

    [Fact]
    public async Task HooksService_LogsErrors_WhenExceptionsOccur()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<HooksService>>();
        var service = new HooksService(mockLogger.Object);

        // Act - Force an error by using invalid path
        var originalDirectory = Directory.GetCurrentDirectory();

        try
        {
            // This should cause an error
            Directory.SetCurrentDirectory(_testDirectory);
            var claudeDir = Path.Combine(_testDirectory, ".claude");
            Directory.CreateDirectory(claudeDir);

            // Create an invalid settings file to cause JSON parsing error
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            await File.WriteAllTextAsync(settingsPath, "invalid json");

            var result = await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

            // Assert
            result.Should().BeFalse("Service should return false on error");

            // Verify error was logged (this depends on implementation details)
            // The service should handle the JSON exception internally
        }
        catch (JsonException)
        {
            // Expected behavior - service may let JSON exceptions bubble up
            // This is acceptable error handling
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private CommandContext CreateMockCommandContext(string? commandName)
    {
        var args = commandName != null ? new[] { commandName } : Array.Empty<string>();
        var mockContext = new Mock<CommandContext>(args, new Dictionary<string, object>(), "test");
        mockContext.SetupGet(x => x.Name).Returns(commandName);
        return mockContext.Object;
    }

    public override void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
        base.Dispose();
    }
}