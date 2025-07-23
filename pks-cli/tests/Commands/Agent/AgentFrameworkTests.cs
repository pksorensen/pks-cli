using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.CLI.Tests.Infrastructure.Fixtures;
using PKS.Commands.Agent;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Agent;

/// <summary>
/// Tests for the agent framework functionality
/// These tests define the expected behavior for AI agent management
/// </summary>
public class AgentFrameworkTests : TestBase
{
    private Mock<IAgentFrameworkService> _mockAgentService = null!;

    private AgentCommand GetCommand() => ServiceProvider.GetRequiredService<AgentCommand>();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        _mockAgentService = ServiceMockFactory.CreateAgentFrameworkService();
        services.AddSingleton(_mockAgentService.Object);
        services.AddSingleton<ILogger<AgentCommand>>(Mock.Of<ILogger<AgentCommand>>());
        services.AddTransient<AgentCommand>();
    }

    [Fact]
    public async Task Create_ShouldCreateNewAgent_WhenValidConfigurationProvided()
    {
        // Arrange
        var agentConfig = TestDataGenerator.GenerateAgentConfiguration("test-agent", "automation");
        var expectedResult = new AgentResult
        {
            Success = true,
            AgentId = "agent-123",
            Message = "Agent created successfully"
        };

        _mockAgentService.Setup(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()))
            .ReturnsAsync(expectedResult);

        var settings = new AgentSettings
        {
            Action = AgentAction.Create,
            Name = "test-agent",
            Type = "automation",
            ConfigFile = "agent-config.json"
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Creating agent");
        AssertConsoleOutput("test-agent");
        AssertConsoleOutput("Agent created successfully");
        _mockAgentService.Verify(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()), Times.Once);
    }

    [Fact]
    public async Task List_ShouldDisplayAllAgents_WhenAgentsExist()
    {
        // Arrange
        var agents = new List<AgentInfo>
        {
            new AgentInfo { Id = "agent-1", Name = "Test Agent 1", Type = "automation", Status = "Active" },
            new AgentInfo { Id = "agent-2", Name = "Test Agent 2", Type = "monitoring", Status = "Inactive" },
            new AgentInfo { Id = "agent-3", Name = "Test Agent 3", Type = "deployment", Status = "Active" }
        };

        _mockAgentService.Setup(x => x.ListAgentsAsync())
            .ReturnsAsync(agents);

        var settings = new AgentSettings { Action = AgentAction.List };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Available Agents");
        agents.ForEach(agent =>
        {
            AssertConsoleOutput(agent.Name);
            AssertConsoleOutput(agent.Type);
            AssertConsoleOutput(agent.Status);
        });
        _mockAgentService.Verify(x => x.ListAgentsAsync(), Times.Once);
    }

    [Fact]
    public async Task Status_ShouldDisplayAgentStatus_WhenValidAgentIdProvided()
    {
        // Arrange
        var agentId = "agent-123";
        var expectedStatus = new AgentStatus
        {
            Id = agentId,
            Status = "Active",
            LastActivity = DateTime.UtcNow.AddMinutes(-5)
        };

        _mockAgentService.Setup(x => x.GetAgentStatusAsync(agentId))
            .ReturnsAsync(expectedStatus);

        var settings = new AgentSettings
        {
            Action = AgentAction.Status,
            AgentId = agentId
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Agent Status");
        AssertConsoleOutput(agentId);
        AssertConsoleOutput("Active");
        _mockAgentService.Verify(x => x.GetAgentStatusAsync(agentId), Times.Once);
    }

    [Fact]
    public async Task Remove_ShouldRemoveAgent_WhenValidAgentIdProvided()
    {
        // Arrange
        var agentId = "agent-to-remove";
        _mockAgentService.Setup(x => x.RemoveAgentAsync(agentId))
            .ReturnsAsync(true);

        var settings = new AgentSettings
        {
            Action = AgentAction.Remove,
            AgentId = agentId
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Removing agent");
        AssertConsoleOutput("Agent removed successfully");
    }

    [Fact]
    public async Task Start_ShouldStartAgent_WhenValidAgentIdProvided()
    {
        // Arrange
        var agentId = "agent-to-start";
        _mockAgentService.Setup(x => x.StartAgentAsync(agentId))
            .ReturnsAsync(new AgentResult { Success = true, Message = "Agent started" });

        var settings = new AgentSettings
        {
            Action = AgentAction.Start,
            AgentId = agentId
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Starting agent");
        AssertConsoleOutput("Agent started");
    }

    [Fact]
    public async Task Stop_ShouldStopAgent_WhenValidAgentIdProvided()
    {
        // Arrange
        var agentId = "agent-to-stop";
        _mockAgentService.Setup(x => x.StopAgentAsync(agentId))
            .ReturnsAsync(new AgentResult { Success = true, Message = "Agent stopped" });

        var settings = new AgentSettings
        {
            Action = AgentAction.Stop,
            AgentId = agentId
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Stopping agent");
        AssertConsoleOutput("Agent stopped");
    }

    [Fact]
    public async Task Create_ShouldReturnError_WhenAgentCreationFails()
    {
        // Arrange
        var failedResult = new AgentResult
        {
            Success = false,
            Message = "Failed to create agent: Invalid configuration"
        };

        _mockAgentService.Setup(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()))
            .ReturnsAsync(failedResult);

        var settings = new AgentSettings
        {
            Action = AgentAction.Create,
            Name = "invalid-agent"
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        AssertConsoleOutput("Failed to create agent");
        AssertConsoleOutput("Invalid configuration");
    }

    [Fact]
    public async Task Status_ShouldReturnError_WhenAgentNotFound()
    {
        // Arrange
        var agentId = "non-existent-agent";
        _mockAgentService.Setup(x => x.GetAgentStatusAsync(agentId))
            .ThrowsAsync(new AgentNotFoundException($"Agent '{agentId}' not found"));

        var settings = new AgentSettings
        {
            Action = AgentAction.Status,
            AgentId = agentId
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        AssertConsoleOutput("Agent not found");
    }

    [Fact]
    public async Task Create_ShouldLoadConfigurationFromFile_WhenConfigFileProvided()
    {
        // Arrange
        var configFilePath = CreateTempFile(TestDataGenerator.GenerateJsonContent(), ".json");
        var expectedResult = new AgentResult { Success = true, AgentId = "config-agent" };

        _mockAgentService.Setup(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()))
            .ReturnsAsync(expectedResult);

        var settings = new AgentSettings
        {
            Action = AgentAction.Create,
            ConfigFile = configFilePath
        };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Loading configuration from file");
        _mockAgentService.Verify(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()), Times.Once);

        // Cleanup
        File.Delete(configFilePath);
    }

    [Fact]
    public async Task List_ShouldShowEmptyMessage_WhenNoAgentsExist()
    {
        // Arrange
        _mockAgentService.Setup(x => x.ListAgentsAsync())
            .ReturnsAsync(new List<AgentInfo>());

        var settings = new AgentSettings { Action = AgentAction.List };

        // Act
        var result = await GetCommand().ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("No agents found");
    }
}