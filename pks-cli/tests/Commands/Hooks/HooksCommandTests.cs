using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.CLI.Tests.Infrastructure.Fixtures;
using PKS.Commands.Hooks;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Hooks;

/// <summary>
/// Tests for the hooks command functionality - Claude Code compatibility
/// These tests verify proper JSON output and silent operation for Claude Code integration
/// </summary>
public class HooksCommandTests : TestBase
{
    [Fact]
    public async Task PreToolUseCommand_WithJsonFlag_OutputsNoJson_WhenProceeding()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var context = CreateCommandContext("pre-tool-use");
        var settings = new HooksSettings { Json = true };

        // Act
        using var output = new StringWriter();
        Console.SetOut(output);
        
        var result = await command.ExecuteAsync(context, settings);
        
        // Assert
        result.Should().Be(0);
        output.ToString().Should().BeEmpty(); // No output for proceed decision
    }

    [Fact]
    public async Task PreToolUseCommand_WithoutJsonFlag_OutputsUserFriendlyInfo()
    {
        // Arrange
        var command = new PreToolUseCommand();
        var context = CreateCommandContext("pre-tool-use");
        var settings = new HooksSettings { Json = false };

        // Act
        var result = await command.ExecuteAsync(context, settings);

        // Assert
        result.Should().Be(0);
        // Note: In real scenario, this would output to AnsiConsole, 
        // but we're testing the execution path completes successfully
    }

    [Fact]
    public async Task PostToolUseCommand_WithJsonFlag_OutputsNoJson_WhenProceeding()
    {
        // Arrange
        var command = new PostToolUseCommand();
        var context = CreateCommandContext("post-tool-use");
        var settings = new HooksSettings { Json = true };

        // Act
        using var output = new StringWriter();
        Console.SetOut(output);
        
        var result = await command.ExecuteAsync(context, settings);
        
        // Assert
        result.Should().Be(0);
        output.ToString().Should().BeEmpty(); // No output for proceed decision
    }

    [Fact]
    public async Task UserPromptSubmitCommand_WithJsonFlag_OutputsNoJson_WhenProceeding()
    {
        // Arrange
        var command = new UserPromptSubmitCommand();
        var context = CreateCommandContext("user-prompt-submit");
        var settings = new HooksSettings { Json = true };

        // Act
        using var output = new StringWriter();
        Console.SetOut(output);
        
        var result = await command.ExecuteAsync(context, settings);
        
        // Assert
        result.Should().Be(0);
        output.ToString().Should().BeEmpty(); // No output for proceed decision
    }

    [Fact]
    public async Task StopCommand_WithJsonFlag_OutputsNoJson_WhenProceeding()
    {
        // Arrange
        var command = new StopCommand();
        var context = CreateCommandContext("stop");
        var settings = new HooksSettings { Json = true };

        // Act
        using var output = new StringWriter();
        Console.SetOut(output);
        
        var result = await command.ExecuteAsync(context, settings);
        
        // Assert
        result.Should().Be(0);
        output.ToString().Should().BeEmpty(); // No output for proceed decision
    }

    [Fact]
    public void HookDecision_Block_CreatesBlockDecision()
    {
        // Act
        var decision = HookDecision.Block("Test reason");

        // Assert
        decision.Decision.Should().Be("block");
        decision.Message.Should().Be("Test reason");
    }

    [Fact]
    public void HookDecision_Approve_CreatesApproveDecision()
    {
        // Act
        var decision = HookDecision.Approve();

        // Assert
        decision.Decision.Should().Be("approve");
        decision.Message.Should().BeNull();
    }

    [Fact]
    public void HookDecision_Proceed_CreatesEmptyDecision()
    {
        // Act
        var decision = HookDecision.Proceed();

        // Assert
        decision.Decision.Should().BeNull();
        decision.Message.Should().BeNull();
        decision.Continue.Should().BeNull();
    }

    [Fact]
    public void HookDecision_Stop_CreatesStopDecision()
    {
        // Act
        var decision = HookDecision.Stop("Test stop reason");

        // Assert
        decision.Continue.Should().BeFalse();
        decision.StopReason.Should().Be("Test stop reason");
    }

    [Fact]
    public void HookDecision_JsonSerialization_ProducesCorrectFormat()
    {
        // Arrange
        var decision = HookDecision.Block("Access denied");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        // Act
        var json = JsonSerializer.Serialize(decision, options);

        // Assert
        json.Should().Be(@"{""decision"":""block"",""message"":""Access denied""}");
    }

    private static CommandContext CreateCommandContext(string commandName)
    {
        var context = new Mock<CommandContext>(
            Mock.Of<CommandSettings>(),
            commandName,
            Mock.Of<IRemainingArguments>());
        
        return context.Object;
    }
}