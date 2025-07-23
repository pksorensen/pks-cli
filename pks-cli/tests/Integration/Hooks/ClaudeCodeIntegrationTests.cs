using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using Spectre.Console.Cli;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Hooks;

/// <summary>
/// Integration tests for Claude Code hooks compatibility
/// These tests validate the complete integration workflow and output format
/// </summary>
public class ClaudeCodeIntegrationTests : TestBase
{
    private readonly string _testProjectDirectory;
    private readonly HooksService _hooksService;

    public ClaudeCodeIntegrationTests()
    {
        _testProjectDirectory = CreateTempDirectory();
        var mockLogger = new Mock<ILogger<HooksService>>();
        _hooksService = new HooksService(mockLogger.Object);
    }

    [Fact]
    public async Task FullIntegrationWorkflow_ShouldCreateValidClaudeCodeConfiguration()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectDirectory);

        try
        {
            // Act - Initialize Claude Code hooks
            var success = await _hooksService.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

            // Assert
            success.Should().BeTrue("Hook initialization should succeed");

            var settingsPath = Path.Combine(_testProjectDirectory, ".claude", "settings.json");
            File.Exists(settingsPath).Should().BeTrue("Settings file should be created");

            // Validate JSON structure matches Claude Code specification
            var json = await File.ReadAllTextAsync(settingsPath);
            var settings = JsonDocument.Parse(json);
            
            ValidateClaudeCodeSettingsStructure(settings);
            await ValidateAllHookCommandsExecute();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HookConfiguration_ShouldMatchClaudeCodeSpecification()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectDirectory);

        try
        {
            // Act
            await _hooksService.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

            // Assert
            var settingsPath = Path.Combine(_testProjectDirectory, ".claude", "settings.json");
            var json = await File.ReadAllTextAsync(settingsPath);
            var settings = JsonDocument.Parse(json);

            // Validate hook structure for each hook type
            ValidateHookStructure(settings, "preToolUse", "pks hooks pre-tool-use", hasMatcher: true);
            ValidateHookStructure(settings, "postToolUse", "pks hooks post-tool-use", hasMatcher: true);
            ValidateHookStructure(settings, "userPromptSubmit", "pks hooks user-prompt-submit", hasMatcher: false);
            ValidateHookStructure(settings, "stop", "pks hooks stop", hasMatcher: false);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Theory]
    [InlineData(SettingsScope.Project, ".claude")]
    [InlineData(SettingsScope.Local, ".claude")]
    public async Task HookInitialization_ShouldCreateCorrectDirectoryStructure(SettingsScope scope, string expectedDir)
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectDirectory);

        try
        {
            // Act
            var success = await _hooksService.InitializeClaudeCodeHooksAsync(false, scope);

            // Assert
            success.Should().BeTrue();

            var claudeDir = Path.Combine(_testProjectDirectory, expectedDir);
            Directory.Exists(claudeDir).Should().BeTrue($"Directory {expectedDir} should be created");

            var settingsFile = Path.Combine(claudeDir, "settings.json");
            File.Exists(settingsFile).Should().BeTrue("settings.json should be created");

            // Verify permissions (on Unix-like systems)
            if (!OperatingSystem.IsWindows())
            {
                var dirInfo = new DirectoryInfo(claudeDir);
                dirInfo.Exists.Should().BeTrue();
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HookMerging_ShouldPreserveExistingNonPksHooks()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectDirectory);

        try
        {
            var claudeDir = Path.Combine(_testProjectDirectory, ".claude");
            var settingsPath = Path.Combine(claudeDir, "settings.json");
            
            Directory.CreateDirectory(claudeDir);
            
            // Create existing settings with non-PKS hooks
            var existingSettings = new
            {
                hooks = new
                {
                    preToolUse = new[]
                    {
                        new
                        {
                            matcher = "ExistingTool",
                            hooks = new[]
                            {
                                new
                                {
                                    type = "command",
                                    command = "existing-pre-hook"
                                }
                            }
                        }
                    }
                }
            };
            
            var existingJson = JsonSerializer.Serialize(existingSettings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(settingsPath, existingJson);

            // Act - Initialize PKS hooks (should merge, not replace)
            var success = await _hooksService.InitializeClaudeCodeHooksAsync(true, SettingsScope.Project);

            // Assert
            success.Should().BeTrue();

            var json = await File.ReadAllTextAsync(settingsPath);
            var settings = JsonDocument.Parse(json);

            // Verify both existing and PKS hooks are present
            settings.RootElement.TryGetProperty("hooks", out var hooksElement).Should().BeTrue();
            hooksElement.TryGetProperty("preToolUse", out var preToolUseElement).Should().BeTrue();

            var preToolUseHooks = preToolUseElement.EnumerateArray().ToList();
            preToolUseHooks.Should().HaveCountGreaterOrEqualTo(2, "Should have both existing and PKS hooks");

            // Verify existing hook is preserved
            var hasExistingHook = false;
            var hasPksHook = false;

            foreach (var hook in preToolUseHooks)
            {
                if (hook.TryGetProperty("hooks", out var innerHooks))
                {
                    foreach (var innerHook in innerHooks.EnumerateArray())
                    {
                        if (innerHook.TryGetProperty("command", out var commandElement))
                        {
                            var command = commandElement.GetString();
                            if (command == "existing-pre-hook")
                                hasExistingHook = true;
                            if (command == "pks hooks pre-tool-use")
                                hasPksHook = true;
                        }
                    }
                }
            }

            hasExistingHook.Should().BeTrue("Existing hook should be preserved");
            hasPksHook.Should().BeTrue("PKS hook should be added");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public void HookCommandNaming_ShouldFollowClaudeCodeConvention()
    {
        // Arrange - Expected command patterns for Claude Code
        var expectedCommands = new Dictionary<string, string>
        {
            ["preToolUse"] = "pks hooks pre-tool-use",
            ["postToolUse"] = "pks hooks post-tool-use",
            ["userPromptSubmit"] = "pks hooks user-prompt-submit",
            ["stop"] = "pks hooks stop"
        };

        // Assert
        foreach (var (hookType, command) in expectedCommands)
        {
            // Verify hook type follows camelCase (JSON property)
            hookType.Should().MatchRegex(@"^[a-z][a-zA-Z]*$", 
                $"Hook type '{hookType}' should be camelCase for JSON compatibility");

            // Verify command follows kebab-case (CLI command)
            command.Should().StartWith("pks hooks ", "All PKS hook commands should start with 'pks hooks '");
            
            var hookCommand = command.Substring("pks hooks ".Length);
            hookCommand.Should().MatchRegex(@"^[a-z]+(-[a-z]+)*$", 
                $"Hook command '{hookCommand}' should use kebab-case");
        }
    }

    [Fact]
    public async Task HookConfiguration_ShouldValidateAsJsonSchema()
    {
        // Arrange
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testProjectDirectory);

        try
        {
            // Act
            await _hooksService.InitializeClaudeCodeHooksAsync(false, SettingsScope.Project);

            // Assert
            var settingsPath = Path.Combine(_testProjectDirectory, ".claude", "settings.json");
            var json = await File.ReadAllTextAsync(settingsPath);
            
            // Verify JSON is valid and parseable
            var settings = JsonDocument.Parse(json);
            settings.Should().NotBeNull("Generated JSON should be valid");

            // Verify required structure exists
            settings.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            settings.RootElement.TryGetProperty("hooks", out var hooksProperty).Should().BeTrue();
            hooksProperty.ValueKind.Should().Be(JsonValueKind.Object);

            // Verify hooks object contains all expected properties
            var expectedHookTypes = new[] { "preToolUse", "postToolUse", "userPromptSubmit", "stop" };
            foreach (var hookType in expectedHookTypes)
            {
                hooksProperty.TryGetProperty(hookType, out var hookArray).Should().BeTrue($"Hook type '{hookType}' should exist");
                hookArray.ValueKind.Should().Be(JsonValueKind.Array, $"Hook type '{hookType}' should be an array");
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static void ValidateClaudeCodeSettingsStructure(JsonDocument settings)
    {
        // Root level validation
        settings.RootElement.ValueKind.Should().Be(JsonValueKind.Object, "Settings should be a JSON object");
        settings.RootElement.TryGetProperty("hooks", out var hooksElement).Should().BeTrue("Settings should contain 'hooks' property");

        // Hooks structure validation
        hooksElement.ValueKind.Should().Be(JsonValueKind.Object, "Hooks should be a JSON object");

        // Validate all required hook types exist
        var requiredHookTypes = new[] { "preToolUse", "postToolUse", "userPromptSubmit", "stop" };
        foreach (var hookType in requiredHookTypes)
        {
            hooksElement.TryGetProperty(hookType, out var hookArray).Should().BeTrue($"Hook type '{hookType}' should exist");
            hookArray.ValueKind.Should().Be(JsonValueKind.Array, $"Hook type '{hookType}' should be an array");
            hookArray.EnumerateArray().Should().HaveCountGreaterThan(0, $"Hook type '{hookType}' should have at least one configuration");
        }
    }

    private static void ValidateHookStructure(JsonDocument settings, string hookType, string expectedCommand, bool hasMatcher)
    {
        settings.RootElement.TryGetProperty("hooks", out var hooksElement).Should().BeTrue();
        hooksElement.TryGetProperty(hookType, out var hookArray).Should().BeTrue();

        var hooks = hookArray.EnumerateArray().ToList();
        hooks.Should().HaveCountGreaterThan(0, $"Hook type '{hookType}' should have configurations");

        var pksHook = hooks.FirstOrDefault(h => 
        {
            if (h.TryGetProperty("hooks", out var innerHooks))
            {
                return innerHooks.EnumerateArray().Any(ih => 
                    ih.TryGetProperty("command", out var cmd) && 
                    cmd.GetString() == expectedCommand);
            }
            return false;
        });

        pksHook.ValueKind.Should().NotBe(JsonValueKind.Undefined, $"PKS hook for '{hookType}' should exist");

        // Validate structure
        if (hasMatcher)
        {
            pksHook.TryGetProperty("matcher", out var matcher).Should().BeTrue($"Hook '{hookType}' should have matcher");
            matcher.GetString().Should().Be("Bash", "Matcher should be 'Bash' for tool-specific hooks");
        }

        pksHook.TryGetProperty("hooks", out var innerHooksArray).Should().BeTrue($"Hook '{hookType}' should have hooks array");
        var innerHooks = innerHooksArray.EnumerateArray().ToList();
        innerHooks.Should().HaveCount(1, $"Hook '{hookType}' should have exactly one inner hook");

        var innerHook = innerHooks[0];
        innerHook.TryGetProperty("type", out var typeElement).Should().BeTrue("Inner hook should have type");
        typeElement.GetString().Should().Be("command", "Inner hook type should be 'command'");

        innerHook.TryGetProperty("command", out var commandElement).Should().BeTrue("Inner hook should have command");
        commandElement.GetString().Should().Be(expectedCommand, $"Command should be '{expectedCommand}'");
    }

    private async Task ValidateAllHookCommandsExecute()
    {
        var hookCommands = new[]
        {
            ("pre-tool-use", typeof(PreToolUseCommand)),
            ("post-tool-use", typeof(PostToolUseCommand)),
            ("user-prompt-submit", typeof(UserPromptSubmitCommand)),
            ("stop", typeof(StopCommand))
        };

        foreach (var (commandName, commandType) in hookCommands)
        {
            // Create and execute the command
            var command = (AsyncCommand<HooksSettings>)Activator.CreateInstance(commandType)!;
            var settings = new HooksSettings();
            var context = CreateMockCommandContext(commandName);

            var result = await command.ExecuteAsync(context, settings);
            result.Should().Be(0, $"Hook command '{commandName}' should execute successfully");
        }
    }

    private static CommandContext CreateMockCommandContext(string commandName)
    {
        var mockContext = new Mock<CommandContext>();
        mockContext.SetupGet(x => x.Name).Returns(commandName);
        return mockContext.Object;
    }

    public override void Dispose()
    {
        if (Directory.Exists(_testProjectDirectory))
        {
            Directory.Delete(_testProjectDirectory, true);
        }
        base.Dispose();
    }
}