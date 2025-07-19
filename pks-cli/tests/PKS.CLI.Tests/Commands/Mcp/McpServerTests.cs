using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.CLI.Tests.Infrastructure.Fixtures;
using PKS.CLI.Commands.Mcp;
using PKS.CLI.Infrastructure.Services;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Mcp;

/// <summary>
/// Tests for the MCP server functionality
/// These tests define the expected behavior for Model Context Protocol server management
/// </summary>
public class McpServerTests : TestBase
{
    private Mock<PKS.CLI.Infrastructure.Services.IMcpServerService> _mockMcpService;
    private PKS.CLI.Commands.Mcp.McpCommand _command;

    public McpServerTests()
    {
        // Setup will be done after ConfigureServices is called
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        
        // Create and configure mock service
        _mockMcpService = new Mock<PKS.CLI.Infrastructure.Services.IMcpServerService>();
        
        // Setup default mock behaviors
        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()))
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerResult { Success = true, Port = 8080 });
            
        _mockMcpService.Setup(x => x.StopServerAsync())
            .ReturnsAsync(true);
            
        _mockMcpService.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerStatus { IsRunning = false, Port = null });
        
        services.AddSingleton(_mockMcpService.Object);
        services.AddLogging();
        
        // Create command after services are configured
        var mockLogger = new Mock<ILogger<PKS.CLI.Commands.Mcp.McpCommand>>();
        _command = new PKS.CLI.Commands.Mcp.McpCommand(_mockMcpService.Object, mockLogger.Object);
    }

    [Fact]
    public async Task Start_ShouldStartMcpServer_WhenValidConfigurationProvided()
    {
        // Arrange
        var config = TestDataGenerator.GenerateMcpServerConfig(8080);
        var expectedResult = new PKS.CLI.Infrastructure.Services.Models.McpServerResult 
        { 
            Success = true, 
            Port = 8080,
            Message = "MCP Server started successfully"
        };

        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()))
            .ReturnsAsync(expectedResult);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings 
        { 
            Action = PKS.CLI.Commands.Mcp.McpAction.Start,
            Port = 8080,
            Transport = "stdio"
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Starting MCP Server");
        AssertConsoleOutput("8080");
        AssertConsoleOutput("started successfully");
        _mockMcpService.Verify(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()), Times.Once);
    }

    [Fact]
    public async Task Stop_ShouldStopMcpServer_WhenServerIsRunning()
    {
        // Arrange
        _mockMcpService.Setup(x => x.StopServerAsync())
            .ReturnsAsync(true);

        _mockMcpService.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerStatus { IsRunning = true, Port = 8080 });

        var settings = new PKS.CLI.Commands.Mcp.McpSettings { Action = PKS.CLI.Commands.Mcp.McpAction.Stop };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Stopping MCP Server");
        AssertConsoleOutput("stopped successfully");
        _mockMcpService.Verify(x => x.StopServerAsync(), Times.Once);
    }

    [Fact]
    public async Task Stop_ShouldReturnError_WhenServerIsNotRunning()
    {
        // Arrange
        _mockMcpService.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerStatus { IsRunning = false });

        var settings = new PKS.CLI.Commands.Mcp.McpSettings { Action = PKS.CLI.Commands.Mcp.McpAction.Stop };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        AssertConsoleOutput("MCP Server is not running");
    }

    [Fact]
    public async Task Status_ShouldDisplayServerStatus_WhenCalled()
    {
        // Arrange
        var status = new PKS.CLI.Infrastructure.Services.Models.McpServerStatus 
        { 
            IsRunning = true, 
            Port = 8080,
            StartTime = DateTime.UtcNow.AddMinutes(-30)
        };

        _mockMcpService.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(status);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings { Action = PKS.CLI.Commands.Mcp.McpAction.Status };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("MCP Server Status");
        AssertConsoleOutput("Running");
        AssertConsoleOutput("8080");
        _mockMcpService.Verify(x => x.GetServerStatusAsync(), Times.Once);
    }

    [Fact]
    public async Task Start_ShouldUseDefaultPort_WhenPortNotSpecified()
    {
        // Arrange
        var expectedResult = new PKS.CLI.Infrastructure.Services.Models.McpServerResult 
        { 
            Success = true, 
            Port = 3000, // Default port
            Message = "MCP Server started on default port"
        };

        _mockMcpService.Setup(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Port == 3000)))
            .ReturnsAsync(expectedResult);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings 
        { 
            Action = PKS.CLI.Commands.Mcp.McpAction.Start,
            Transport = "stdio"
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        _mockMcpService.Verify(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Port == 3000)), Times.Once);
    }

    [Fact]
    public async Task Start_ShouldConfigureStdioTransport_WhenStdioSpecified()
    {
        // Arrange
        var expectedResult = new PKS.CLI.Infrastructure.Services.Models.McpServerResult { Success = true, Port = 3000 };

        _mockMcpService.Setup(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Transport == "stdio")))
            .ReturnsAsync(expectedResult);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings 
        { 
            Action = PKS.CLI.Commands.Mcp.McpAction.Start,
            Transport = "stdio"
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        _mockMcpService.Verify(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Transport == "stdio")), Times.Once);
    }

    [Fact]
    public async Task Start_ShouldConfigureSseTransport_WhenSseSpecified()
    {
        // Arrange
        var expectedResult = new PKS.CLI.Infrastructure.Services.Models.McpServerResult { Success = true, Port = 8080 };

        _mockMcpService.Setup(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Transport == "sse")))
            .ReturnsAsync(expectedResult);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings 
        { 
            Action = PKS.CLI.Commands.Mcp.McpAction.Start,
            Transport = "sse",
            Port = 8080
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        _mockMcpService.Verify(x => x.StartServerAsync(It.Is<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>(c => c.Transport == "sse")), Times.Once);
    }

    [Fact]
    public async Task Start_ShouldReturnError_WhenStartupFails()
    {
        // Arrange
        var failedResult = new PKS.CLI.Infrastructure.Services.Models.McpServerResult 
        { 
            Success = false, 
            Message = "Failed to start server: Port already in use"
        };

        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()))
            .ReturnsAsync(failedResult);

        var settings = new PKS.CLI.Commands.Mcp.McpSettings 
        { 
            Action = PKS.CLI.Commands.Mcp.McpAction.Start,
            Port = 8080
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        AssertConsoleOutput("Failed to start server");
        AssertConsoleOutput("Port already in use");
    }

    [Fact]
    public async Task Restart_ShouldStopAndStartServer_WhenServerIsRunning()
    {
        // Arrange
        _mockMcpService.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerStatus { IsRunning = true, Port = 8080 });

        _mockMcpService.Setup(x => x.StopServerAsync())
            .ReturnsAsync(true);

        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()))
            .ReturnsAsync(new PKS.CLI.Infrastructure.Services.Models.McpServerResult { Success = true, Port = 8080 });

        var settings = new PKS.CLI.Commands.Mcp.McpSettings { Action = PKS.CLI.Commands.Mcp.McpAction.Restart };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        AssertConsoleOutput("Restarting MCP Server");
        _mockMcpService.Verify(x => x.StopServerAsync(), Times.Once);
        _mockMcpService.Verify(x => x.StartServerAsync(It.IsAny<PKS.CLI.Infrastructure.Services.Models.McpServerConfig>()), Times.Once);
    }
}