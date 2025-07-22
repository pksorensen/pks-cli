using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Tests.Infrastructure;
using Xunit;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Integration tests for MCP server functionality
/// Tests the complete MCP server workflow including tool execution
/// </summary>
public class McpIntegrationTests : TestBase
{
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpToolService _mcpToolService;
    private readonly McpResourceService _mcpResourceService;

    public McpIntegrationTests()
    {
        _mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        _mcpToolService = ServiceProvider.GetRequiredService<McpToolService>();
        _mcpResourceService = ServiceProvider.GetRequiredService<McpResourceService>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IMcpHostingService, McpHostingService>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<McpResourceService>();
    }

    [Fact]
    public async Task McpServer_ShouldStartAndStopWithStdioTransport()
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        // Act - Start
        var startResult = await _mcpHostingService.StartServerAsync(config);

        // Assert - Start
        startResult.Success.Should().BeTrue();
        startResult.Transport.Should().Be("stdio");
        startResult.Message.Should().Contain("successfully");

        // Act - Status
        var status = await _mcpHostingService.GetServerStatusAsync();

        // Assert - Status
        status.Status.Should().Be(McpServerStatus.Running);
        status.Transport.Should().Be("stdio");

        // Act - Stop
        var stopResult = await _mcpHostingService.StopServerAsync();

        // Assert - Stop
        stopResult.Should().BeTrue();

        // Verify stopped
        var finalStatus = await _mcpHostingService.GetServerStatusAsync();
        finalStatus.Status.Should().Be(McpServerStatus.Stopped);
    }

    [Fact]
    public async Task McpServer_ShouldProvideResources()
    {
        // Act
        var resources = _mcpResourceService.GetAvailableResources();

        // Assert
        resources.Should().NotBeEmpty();
        resources.Should().HaveCount(3);

        var resourceList = resources.ToList();
        resourceList.Should().Contain(r => r.Name == "Agents");
        resourceList.Should().Contain(r => r.Name == "Current Tasks");
        resourceList.Should().Contain(r => r.Name == "Projects");

        // Verify resource structure
        var agentsResource = resourceList.First(r => r.Name == "Agents");
        agentsResource.Uri.Should().Be("pks://agents");
        agentsResource.MimeType.Should().Be("application/json");
        agentsResource.Metadata.Should().ContainKey("category");
    }

    [Fact]
    public void McpServer_ShouldProvideTools()
    {
        // Act
        var tools = _mcpToolService.GetAvailableTools();

        // Assert
        tools.Should().NotBeEmpty();
        tools.Should().HaveCount(4);

        var toolList = tools.ToList();
        toolList.Should().Contain(t => t.Name == "pks_create_task");
        toolList.Should().Contain(t => t.Name == "pks_get_agent_status");
        toolList.Should().Contain(t => t.Name == "pks_deploy");
        toolList.Should().Contain(t => t.Name == "pks_init_project");

        // Verify tool structure
        var initTool = toolList.First(t => t.Name == "pks_init_project");
        initTool.Category.Should().Be("project-management");
        initTool.Description.Should().Contain("Initialize new projects");
        initTool.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task McpServer_ShouldExecuteSwarmInitTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["swarm_name"] = "test-swarm",
            ["max_agents"] = 5,
            ["coordination_strategy"] = "centralized",
            ["memory_limit_mb"] = 1024
        };

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("mcp__pks__swarm_init", arguments);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("initialized successfully");
        result.DurationMs.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();

        // Verify returned data structure
        var data = result.Data as dynamic;
        data.Should().NotBeNull();
        // Note: We can't easily test the exact structure of dynamic objects in tests
        // But we verified the structure exists
    }

    [Fact]
    public async Task McpServer_ShouldExecuteAgentSpawnTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["agent_type"] = "testing",
            ["agent_name"] = "test-agent-1",
            ["swarm_id"] = "test-swarm-id",
            ["capabilities"] = new[] { "test-execution", "reporting" },
            ["priority"] = "high"
        };

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("mcp__pks__agent_spawn", arguments);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Agent spawned successfully");
        result.DurationMs.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task McpServer_ShouldExecuteTaskOrchestrationTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["description"] = "Run integration tests for MCP functionality",
            ["priority"] = "high",
            ["estimated_duration"] = "10 minutes"
        };

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("mcp__pks__task_orchestrate", arguments);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Task orchestration started");
        result.DurationMs.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task McpServer_ShouldExecuteMemoryUsageTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>();

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("mcp__pks__memory_usage", arguments);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Memory usage retrieved");
        result.DurationMs.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task McpServer_ShouldExecuteSwarmMonitorTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>();

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("mcp__pks__swarm_monitor", arguments);

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Swarm monitoring data retrieved");
        result.DurationMs.Should().BeGreaterThan(0);
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task McpServer_ShouldReturnErrorForUnknownTool()
    {
        // Arrange
        var arguments = new Dictionary<string, object>();

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("unknown_tool", arguments);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Unknown tool");
        result.Error.Should().Contain("not implemented");
    }

    [Theory]
    [InlineData("http", 3000)]
    [InlineData("sse", 8080)]
    public async Task McpServer_ShouldStartWithDifferentTransports(string transport, int port)
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = transport,
            Port = port
        };

        try
        {
            // Act
            var result = await _mcpHostingService.StartServerAsync(config);

            // Assert
            result.Success.Should().BeTrue();
            result.Transport.Should().Be(transport);
            result.Port.Should().Be(port);
        }
        finally
        {
            // Cleanup
            await _mcpHostingService.StopServerAsync();
        }
    }
}