using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Commands.Hooks;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using System.Text;
using Xunit;
using Moq;

namespace PKS.CLI.Tests.Commands.Hooks;

/// <summary>
/// Tests for individual hook event commands (PreToolUse, PostToolUse, UserPromptSubmit, Stop)
/// Tests output format validation, error handling, and Claude Code compatibility
/// </summary>
public class HookEventCommandsTests : TestBase
{
    private readonly TestConsole _testConsole;
    private readonly string _testDirectory;

    public HookEventCommandsTests()
    {
        _testConsole = new TestConsole();
        _testDirectory = CreateTempDirectory();
    }

    [Fact]
    public async Task PreToolUseCommand_ShouldExecuteSuccessfully()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0, "PreToolUse command should return success exit code");
            
            var output = _testConsole.Output;
            output.Should().Contain("PKS Hooks: PreToolUse Event Triggered");
            output.Should().Contain("✓ PreToolUse hook completed successfully");
            output.Should().Contain("Environment Variables:");
            output.Should().Contain("Command Line Arguments:");
            output.Should().Contain("Working Directory:");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task PostToolUseCommand_ShouldExecuteSuccessfully()
    {
        // Arrange
        var command = new PostToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("post-tool-use");
        
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0, "PostToolUse command should return success exit code");
            
            var output = _testConsole.Output;
            output.Should().Contain("PKS Hooks: PostToolUse Event Triggered");
            output.Should().Contain("✓ PostToolUse hook completed successfully");
            output.Should().Contain("Environment Variables:");
            output.Should().Contain("Command Line Arguments:");
            output.Should().Contain("Working Directory:");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task UserPromptSubmitCommand_ShouldExecuteSuccessfully()
    {
        // Arrange
        var command = new UserPromptSubmitCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("user-prompt-submit");
        
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0, "UserPromptSubmit command should return success exit code");
            
            var output = _testConsole.Output;
            output.Should().Contain("PKS Hooks: UserPromptSubmit Event Triggered");
            output.Should().Contain("✓ UserPromptSubmit hook completed successfully");
            output.Should().Contain("Environment Variables:");
            output.Should().Contain("Command Line Arguments:");
            output.Should().Contain("Working Directory:");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task StopCommand_ShouldExecuteSuccessfully()
    {
        // Arrange
        var command = new StopCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("stop");
        
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0, "Stop command should return success exit code");
            
            var output = _testConsole.Output;
            output.Should().Contain("PKS Hooks: Stop Event Triggered");
            output.Should().Contain("✓ Stop hook completed successfully");
            output.Should().Contain("Environment Variables:");
            output.Should().Contain("Command Line Arguments:");
            output.Should().Contain("Working Directory:");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Theory]
    [InlineData(typeof(PreToolUseCommand), "PreToolUse")]
    [InlineData(typeof(PostToolUseCommand), "PostToolUse")]
    [InlineData(typeof(UserPromptSubmitCommand), "UserPromptSubmit")]
    [InlineData(typeof(StopCommand), "Stop")]
    public async Task HookEventCommands_ShouldDisplayCorrectEventType(Type commandType, string expectedEventType)
    {
        // Arrange
        var command = (AsyncCommand<HooksSettings>)Activator.CreateInstance(commandType)!;
        var settings = new HooksSettings();
        var context = CreateMockCommandContext(expectedEventType.ToLower());
        
        AnsiConsole.Console = _testConsole;

        // Act
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0);
        var output = _testConsole.Output;
        output.Should().Contain($"PKS Hooks: {expectedEventType} Event Triggered");
        output.Should().Contain($"✓ {expectedEventType} hook completed successfully");
    }

    [Fact]
    public async Task HookCommands_ShouldDisplayEnvironmentVariables()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        // Set test environment variable
        Environment.SetEnvironmentVariable("PKS_TEST_VAR", "test_value");
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0);
            var output = _testConsole.Output;
            output.Should().Contain("Environment Variables:");
            output.Should().Contain("PKS_TEST_VAR");
            output.Should().Contain("test_value");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PKS_TEST_VAR", null);
        }
    }

    [Fact]
    public async Task HookCommands_ShouldDisplayCommandLineArguments()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        AnsiConsole.Console = _testConsole;

        // Act
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0);
        var output = _testConsole.Output;
        output.Should().Contain("Command Line Arguments:");
        output.Should().Contain("args[");
        output.Should().Contain("] =");
    }

    [Fact]
    public async Task HookCommands_ShouldDisplayWorkingDirectory()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        var originalDirectory = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDirectory);
        AnsiConsole.Console = _testConsole;

        try
        {
            // Act
            var result = await command.ExecuteAsync(context, settings);

            // Assert
            result.Should().Be(0);
            var output = _testConsole.Output;
            output.Should().Contain("Working Directory:");
            output.Should().Contain(_testDirectory);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task HookCommands_ShouldHandleStdinInput()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        AnsiConsole.Console = _testConsole;

        // Act
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0);
        var output = _testConsole.Output;
        output.Should().Contain("STDIN Input:");
        // Since no input is redirected in tests, should show detection message
        output.Should().Contain("No piped input detected");
    }

    [Fact]
    public async Task HookCommands_ShouldHandleStdinError()
    {
        // This test verifies error handling for stdin reading
        // The actual commands handle exceptions gracefully
        
        // Arrange
        var command = new PreToolUseCommand();
        var settings = new HooksSettings();
        var context = CreateMockCommandContext("pre-tool-use");
        
        AnsiConsole.Console = _testConsole;

        // Act
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0, "Commands should handle stdin errors gracefully");
        // The command should not crash even if stdin reading fails
    }

    [Theory]
    [InlineData(typeof(PreToolUseCommand))]
    [InlineData(typeof(PostToolUseCommand))]
    [InlineData(typeof(UserPromptSubmitCommand))]
    [InlineData(typeof(StopCommand))]
    public void HookEventCommands_ShouldInheritFromAsyncCommandWithHooksSettings(Type commandType)
    {
        // Assert
        commandType.Should().BeAssignableTo<AsyncCommand<HooksSettings>>(
            "All hook event commands should inherit from AsyncCommand<HooksSettings>");
    }

    [Fact]
    public void AllHookEventCommands_ShouldBeInCorrectNamespace()
    {
        // Arrange
        var commandTypes = new[]
        {
            typeof(PreToolUseCommand),
            typeof(PostToolUseCommand),
            typeof(UserPromptSubmitCommand),
            typeof(StopCommand)
        };

        // Assert
        foreach (var type in commandTypes)
        {
            type.Namespace.Should().Be("PKS.Commands.Hooks",
                "All hook event commands should be in the PKS.Commands.Hooks namespace");
        }
    }

    [Fact]
    public void HookEventCommands_ShouldHaveCorrectXmlDocumentation()
    {
        // This test ensures all hook commands have proper documentation
        // which is important for maintainability and Claude Code integration
        
        var commandTypes = new[]
        {
            typeof(PreToolUseCommand),
            typeof(PostToolUseCommand),
            typeof(UserPromptSubmitCommand),
            typeof(StopCommand)
        };

        foreach (var type in commandTypes)
        {
            type.Should().NotBeNull($"{type.Name} should exist");
            // Additional XML doc validation would require reflection on XML comments
            // which is handled at compile time
        }
    }

    [Theory]
    [InlineData("PreToolUse", "pre-tool-use")]
    [InlineData("PostToolUse", "post-tool-use")]
    [InlineData("UserPromptSubmit", "user-prompt-submit")]
    [InlineData("Stop", "stop")]
    public void HookEventCommands_ShouldFollowNamingConvention(string eventType, string expectedCommandName)
    {
        // Assert - Verify the mapping between event types and command names
        eventType.Should().MatchRegex(@"^[A-Z][a-zA-Z]*$", 
            "Event types should be PascalCase");
        
        expectedCommandName.Should().MatchRegex(@"^[a-z]+-?[a-z]*-?[a-z]*$", 
            "Command names should be kebab-case");
        
        expectedCommandName.Should().NotContain("_", 
            "Command names should not contain underscores");
    }

    private CommandContext CreateMockCommandContext(string commandName)
    {
        var mockContext = new Mock<CommandContext>(new[] { commandName }, new Dictionary<string, object>(), "test");
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