using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using System.IO.Abstractions.TestingHelpers;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration;

/// <summary>
/// Integration tests for the InitCommand
/// </summary>
public class InitCommandTests : TestBase
{
    private readonly ConsoleTestHelper _console;

    public InitCommandTests(ITestOutputHelper output) : base(output)
    {
        _console = new ConsoleTestHelper();
    }

    [Fact]
    public void InitCommand_Execute_WithProjectName_ReturnsSuccess()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "TestProject",
            Template = "console",
            EnableAgentic = false
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0, "Command should succeed");
    }

    [Fact]
    public void InitCommand_Execute_WithApiTemplate_ReturnsSuccess()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "MyApiProject",
            Template = "api",
            EnableAgentic = true
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0, "Command should succeed with API template");
    }

    [Fact]
    public void InitCommand_Execute_WithWebTemplate_ReturnsSuccess()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "MyWebApp",
            Template = "web",
            EnableAgentic = false
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0, "Command should succeed with web template");
    }

    [Fact]
    public void InitCommand_Execute_WithAgentTemplate_ReturnsSuccess()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "MyAgentProject",
            Template = "agent",
            EnableAgentic = true
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0, "Command should succeed with agent template");
    }

    [Fact]
    public void InitCommand_Execute_WithAgenticEnabled_ShowsAgenticFeatures()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "AgenticProject",
            Template = "console",
            EnableAgentic = true
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0);
        // Note: The current InitCommand doesn't use the provided console,
        // so we can't test output directly. This would require refactoring
        // InitCommand to accept IAnsiConsole as a dependency.
    }

    [Fact]
    public void InitCommand_Execute_WithDefaultTemplate_UsesConsole()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = "DefaultProject"
            // Template not set, should default to "console"
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0);
        settings.Template.Should().Be("console", "Should default to console template");
    }

    [Theory]
    [InlineData("console")]
    [InlineData("api")]
    [InlineData("web")]
    [InlineData("agent")]
    public void InitCommand_Execute_WithAllSupportedTemplates_Succeeds(string template)
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = $"Project_{template}",
            Template = template,
            EnableAgentic = template == "agent"
        };

        // Act
        var result = command.Execute(context, settings);

        // Assert
        result.Should().Be(0, $"Should succeed with {template} template");
    }

    [Fact]
    public void InitCommand_Settings_HasCorrectAttributes()
    {
        // Arrange & Act
        var settings = new InitCommand.Settings();
        var settingsType = typeof(InitCommand.Settings);

        // Assert
        settingsType.Should().BeDecoratedWith<CommandSettings>();
        
        var projectNameProperty = settingsType.GetProperty(nameof(InitCommand.Settings.ProjectName));
        projectNameProperty.Should().NotBeNull();
        
        var templateProperty = settingsType.GetProperty(nameof(InitCommand.Settings.Template));
        templateProperty.Should().NotBeNull();
        
        var agenticProperty = settingsType.GetProperty(nameof(InitCommand.Settings.EnableAgentic));
        agenticProperty.Should().NotBeNull();
    }

    [Fact]
    public void InitCommand_Settings_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var settings = new InitCommand.Settings();

        // Assert
        settings.Template.Should().Be("console", "Template should default to console");
        settings.EnableAgentic.Should().BeFalse("Agentic should default to false");
        settings.ProjectName.Should().BeNull("ProjectName should default to null");
    }

    [Fact]
    public void InitCommand_Settings_TemplateProperty_AcceptsValidValues()
    {
        // Arrange
        var settings = new InitCommand.Settings();
        var validTemplates = new[] { "console", "api", "web", "agent" };

        // Act & Assert
        foreach (var template in validTemplates)
        {
            settings.Template = template;
            settings.Template.Should().Be(template);
        }
    }

    [Fact]
    public void InitCommand_Settings_ProjectNameProperty_AcceptsValidNames()
    {
        // Arrange
        var settings = new InitCommand.Settings();
        var validNames = new[] { "MyProject", "Test123", "Project_Name", "API-Service" };

        // Act & Assert
        foreach (var name in validNames)
        {
            settings.ProjectName = name;
            settings.ProjectName.Should().Be(name);
        }
    }

    [Fact]
    public void InitCommand_Settings_EnableAgenticProperty_CanBeToggled()
    {
        // Arrange
        var settings = new InitCommand.Settings();

        // Act & Assert
        settings.EnableAgentic = true;
        settings.EnableAgentic.Should().BeTrue();

        settings.EnableAgentic = false;
        settings.EnableAgentic.Should().BeFalse();
    }

    // Test that would verify interactive behavior if InitCommand was refactored
    [Fact]
    public void InitCommand_Execute_WithNullProjectName_WouldPromptUser()
    {
        // Arrange
        var command = new InitCommand();
        var context = CreateCommandContext();
        var settings = new InitCommand.Settings
        {
            ProjectName = null, // This should trigger interactive prompt
            Template = "console"
        };

        // Act & Assert
        // Note: The current implementation uses AnsiConsole.Ask directly,
        // which makes it difficult to test in isolation. 
        // For now, we just verify the command doesn't crash.
        Action act = () => command.Execute(context, settings);
        
        // In a real test environment, this would hang waiting for input
        // To properly test this, InitCommand would need to be refactored
        // to accept IAnsiConsole as a dependency
        
        // For now, we'll skip this test as it would require user input
        act.Should().NotThrow("Command should handle null project name gracefully");
    }

    private CommandContext CreateCommandContext()
    {
        var app = new CommandApp();
        return new CommandContext(
            remainingArguments: Array.Empty<string>(),
            name: "init",
            data: null);
    }

    public override void Dispose()
    {
        _console?.Dispose();
        base.Dispose();
    }
}