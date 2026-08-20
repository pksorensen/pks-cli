using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Services;

/// <summary>
/// Comprehensive tests for the HooksService functionality
/// Tests hook configuration generation, settings merging, and Claude Code compatibility
/// </summary>
public class HooksServiceTests : TestBase
{
    private readonly Mock<ILogger<HooksService>> _mockLogger;
    private readonly HooksService _service;
    private readonly string _testDirectory;

    public HooksServiceTests()
    {
        _mockLogger = new Mock<ILogger<HooksService>>();
        _testDirectory = CreateTempDirectory();
        _service = new HooksService(
            _mockLogger.Object,
            () => _testDirectory,
            () => _testDirectory,
            () => "pks");
    }

    [Fact]
    public void GetPksCommandPrefix_WhenEntryAssemblyIsTesthost_ShouldFallBackToPathCommand()
    {
        var result = HooksService.GetPksCommandPrefix(
            "/usr/share/dotnet/dotnet",
            ["/workspace/tests/bin/Debug/net10.0/testhost.dll"]);

        result.Should().Be("pks");
    }

    [Fact]
    public void GetPksCommandPrefix_WhenEntryAssemblyIsPksCli_ShouldUseDotnetExec()
    {
        var result = HooksService.GetPksCommandPrefix(
            "/usr/share/dotnet/dotnet",
            ["/workspace/pks-cli.dll"]);

        result.Should().Be("dotnet exec /workspace/pks-cli.dll");
    }

    [Fact]
    public void IsTestHostProcess_WhenTesthostIsNativeExecutable_ShouldReturnTrue()
    {
        var result = HooksService.IsTestHostProcess(
            "/workspace/tests/bin/Debug/net10.0/testhost",
            []);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeClaudeCodeHooksAsync_WithDefaultServiceUnderTesthost_ShouldRefuseWrite()
    {
        var service = new HooksService(_mockLogger.Object);

        var result = await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task InitializeClaudeCodeHooksAsync_WithExplicitRoots_ShouldNotUseProcessCurrentDirectory()
    {
        var originalDirectory = Directory.GetCurrentDirectory();

        var result = await _service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

        result.Should().BeTrue();
        Directory.GetCurrentDirectory().Should().Be(originalDirectory);
        File.Exists(Path.Combine(_testDirectory, ".claude", "settings.json")).Should().BeTrue();
    }

    [Theory]
    [InlineData(SettingsScope.Project)]
    [InlineData(SettingsScope.Local)]
    public async Task InitializeClaudeCodeHooksAsync_WithNewFile_ShouldCreateCorrectConfiguration(SettingsScope scope)
    {
        // Act
        var result = await _service.InitializeClaudeCodeHooksAsync(false, scope);

        // Assert
        result.Should().BeTrue();

        var settingsPath = Path.Combine(_testDirectory, ".claude", "settings.json");
        File.Exists(settingsPath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonDocument.Parse(json);

        VerifyHookConfiguration(settings, "PreToolUse", "pks hooks pre-tool-use");
        VerifyHookConfiguration(settings, "PostToolUse", "pks hooks post-tool-use");
        VerifyHookConfiguration(settings, "UserPromptSubmit", "pks hooks user-prompt-submit");
        VerifyHookConfiguration(settings, "Notification", "pks hooks notification");
        VerifyHookConfiguration(settings, "Stop", "pks hooks stop");
        VerifyHookConfiguration(settings, "SubagentStop", "pks hooks subagent-stop");
        VerifyHookConfiguration(settings, "PreCompact", "pks hooks pre-compact");
    }

    [Fact]
    public async Task InitializeClaudeCodeHooksAsync_WithUserScope_ShouldCreateInUserDirectory()
    {
        var userClaudeDir = Path.Combine(_testDirectory, ".claude");
        var settingsPath = Path.Combine(userClaudeDir, "settings.json");

        var result = await _service.InitializeClaudeCodeHooksAsync(false, SettingsScope.User);

        result.Should().BeTrue();
        Directory.Exists(userClaudeDir).Should().BeTrue();
        File.Exists(settingsPath).Should().BeTrue();

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonDocument.Parse(json);
        settings.RootElement.TryGetProperty("hooks", out _).Should().BeTrue();
    }

    [Fact]
    public async Task InitializeClaudeCodeHooksAsync_WithForceFlag_ShouldOverwriteExisting()
    {
        var claudeDir = Path.Combine(_testDirectory, ".claude");
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(settingsPath, @"{
                ""hooks"": {
                    ""preToolUse"": [
                        {
                            ""matcher"": ""Existing"",
                            ""hooks"": [
                                {
                                    ""type"": ""command"",
                                    ""command"": ""existing-command""
                                }
                            ]
                        }
                    ]
                }
            }");

        var result = await _service.InitializeClaudeCodeHooksAsync(true, SettingsScope.Project);

        result.Should().BeTrue();

        var json = await File.ReadAllTextAsync(settingsPath);
        var settings = JsonDocument.Parse(json);

        VerifyHookConfiguration(settings, "PreToolUse", "pks hooks pre-tool-use");
    }

    [Fact]
    public async Task InitializeClaudeCodeHooksAsync_WithInvalidDirectory_ShouldHandleError()
    {
        var invalidRoot = Path.Combine(_testDirectory, "not-a-directory");
        await File.WriteAllTextAsync(invalidRoot, "file blocks directory creation");
        var service = new HooksService(
            _mockLogger.Object,
            () => invalidRoot,
            () => _testDirectory,
            () => "pks");

        var result = await service.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetAvailableHooksAsync_ShouldReturnHookDefinitions()
    {
        // Act
        var hooks = await _service.GetAvailableHooksAsync();

        // Assert
        hooks.Should().NotBeNull();
        hooks.Should().HaveCountGreaterThan(0);

        foreach (var hook in hooks)
        {
            hook.Name.Should().NotBeNullOrEmpty();
            hook.Description.Should().NotBeNullOrEmpty();
            hook.EventType.Should().NotBeNullOrEmpty();
        }
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task ExecuteHookAsync_ShouldReturnSuccessfulResult()
    {
        // Arrange
        var hookName = "test-hook";
        var context = new HookContext
        {
            Parameters = new Dictionary<string, object> { ["param1"] = "value1" },
            WorkingDirectory = _testDirectory,
            Command = "test-command",
            EventType = "test-event"
        };

        // Act
        var result = await _service.ExecuteHookAsync(hookName, context);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Contain(hookName);
        result.ExitCode.Should().Be(0);
        result.Output.Should().ContainKey("result");
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task InstallHookAsync_ShouldReturnInstallationResult()
    {
        // Arrange
        var hookSource = "https://example.com/hook.sh";

        // Act
        var result = await _service.InstallHookAsync(hookSource);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.HookName.Should().NotBeNullOrEmpty();
        result.Message.Should().Contain(hookSource);
        result.InstalledPath.Should().NotBeNullOrEmpty();
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task RemoveHookAsync_ShouldReturnTrue()
    {
        // Arrange
        var hookName = "test-hook";

        // Act
        var result = await _service.RemoveHookAsync(hookName);

        // Assert
        result.Should().BeTrue();
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task InstallHooksAsync_ShouldInstallMultipleHooks()
    {
        // Arrange
        var configuration = new HooksConfiguration
        {
            HookTypes = new List<string> { "pre-commit", "pre-push", "commit-msg" },
            Force = true,
            CommitValidation = true,
            PrePushChecks = true
        };

        // Act
        var result = await _service.InstallHooksAsync(configuration);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Contain($"{configuration.HookTypes.Count} hook types");
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task UninstallHooksAsync_ShouldUninstallHooks()
    {
        // Arrange
        var configuration = new HooksUninstallConfiguration
        {
            HookTypes = new List<string> { "pre-commit", "pre-push" },
            KeepBackup = true,
            BackupLocation = "/backup/location"
        };

        // Act
        var result = await _service.UninstallHooksAsync(configuration);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Contain($"{configuration.HookTypes.Count} hook types");
        result.BackupPath.Should().NotBeNullOrEmpty();
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task UpdateHooksAsync_ShouldUpdateHooks()
    {
        // Arrange
        var configuration = new HooksUpdateConfiguration
        {
            HookTypes = new List<string> { "pre-commit", "pre-push" },
            PreserveCustomizations = true
        };

        // Act
        var result = await _service.UpdateHooksAsync(configuration);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Contain($"{configuration.HookTypes.Count} hook types");
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task GetInstalledHooksAsync_ShouldReturnInstalledHooks()
    {
        // Act
        var hooks = await _service.GetInstalledHooksAsync();

        // Assert
        hooks.Should().NotBeNull();
        hooks.Should().HaveCountGreaterThan(0);

        foreach (var hook in hooks)
        {
            hook.Name.Should().NotBeNullOrEmpty();
            hook.Type.Should().NotBeNullOrEmpty();
            hook.Path.Should().NotBeNullOrEmpty();
            hook.Version.Should().NotBeNullOrEmpty();
        }
    }

    [Fact(Skip = "Mock-only test - tests simulated behavior not real implementation, no real value")]
    public async Task TestHooksAsync_ShouldTestSpecifiedHooks()
    {
        // Arrange
        var hookNames = new List<string> { "pre-commit", "post-commit", "pre-push" };

        // Act
        var results = await _service.TestHooksAsync(hookNames, dryRun: true);

        // Assert
        results.Should().NotBeNull();
        results.Should().HaveCount(hookNames.Count);

        foreach (var result in results)
        {
            result.HookName.Should().BeOneOf(hookNames);
            result.Message.Should().Contain("dry run");
            result.ExecutionTime.Should().BeGreaterThan(TimeSpan.Zero);
        }
    }



    private static void VerifyHookConfiguration(JsonDocument settings, string hookType, string expectedCommand)
    {
        settings.RootElement.TryGetProperty("hooks", out var hooksElement).Should().BeTrue();
        hooksElement.TryGetProperty(hookType, out var hookTypeElement).Should().BeTrue();

        var hookArray = hookTypeElement.EnumerateArray().ToList();
        hookArray.Should().HaveCountGreaterThan(0);

        var hasExpectedCommand = false;
        foreach (var hookItem in hookArray)
        {
            if (hookItem.TryGetProperty("hooks", out var innerHooks))
            {
                foreach (var innerHook in innerHooks.EnumerateArray())
                {
                    if (innerHook.TryGetProperty("command", out var commandElement) &&
                        commandElement.GetString() == expectedCommand)
                    {
                        hasExpectedCommand = true;

                        // Verify hook structure
                        innerHook.TryGetProperty("type", out var typeElement).Should().BeTrue();
                        typeElement.GetString().Should().Be("command");
                        break;
                    }
                }
            }
            if (hasExpectedCommand) break;
        }

        hasExpectedCommand.Should().BeTrue($"Hook {hookType} should contain command '{expectedCommand}'");
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
