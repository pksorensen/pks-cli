using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Commands.Mcp;
using PKS.CLI.Infrastructure.Services.MCP;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Commands.Mcp;

/// <summary>
/// Tests for the MCP server hosting functionality
/// These tests define the expected behavior for Model Context Protocol server management
/// </summary>
public class McpServerTests : TestBase
{
    private Mock<IMcpHostingService> _mockMcpService;
    private Mock<IOptions<McpConfiguration>> _mockConfiguration;
    private McpCommand _command;

    public McpServerTests()
    {
        // Setup will be done after ConfigureServices is called
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);

        // Create and configure mock service
        _mockMcpService = new Mock<IMcpHostingService>();
        _mockConfiguration = new Mock<IOptions<McpConfiguration>>();

        // Setup default mock behaviors
        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpServerResult { Success = true, Port = 8080 });

        _mockMcpService.Setup(x => x.StopServerAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockConfiguration.Setup(x => x.Value)
            .Returns(new McpConfiguration());

        services.AddSingleton(_mockMcpService.Object);
        services.AddSingleton(_mockConfiguration.Object);
        services.AddLogging();

        // Create command after services are configured
        var mockLogger = new Mock<ILogger<McpCommand>>();
        _command = new McpCommand(_mockMcpService.Object, _mockConfiguration.Object, mockLogger.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStartMcpServer_WhenValidConfigurationProvided()
    {
        // Arrange
        var expectedResult = new McpServerResult
        {
            Success = true,
            Port = 8080,
            Message = "MCP Server started successfully"
        };

        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var settings = new McpSettings
        {
            Transport = "stdio",
            Port = 8080,
            Debug = true
        };

        // Create a cancellation token that cancels immediately for testing
        var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        // Act - The command will start server and then be cancelled
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(0);
        _mockMcpService.Verify(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnError_WhenStartupFails()
    {
        // Arrange
        var failedResult = new McpServerResult
        {
            Success = false,
            Message = "Failed to start server: Port already in use"
        };

        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(failedResult);

        var settings = new McpSettings
        {
            Transport = "http",
            Port = 8080
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
        _mockMcpService.Verify(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleException_WhenServiceThrows()
    {
        // Arrange
        _mockMcpService.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Service unavailable"));

        var settings = new McpSettings
        {
            Transport = "http"
        };

        // Act
        var result = await _command.ExecuteAsync(null!, settings);

        // Assert
        result.Should().Be(1);
    }

    [Fact]
    public void McpSettings_ShouldHaveCorrectDefaults()
    {
        // Act
        var settings = new McpSettings();

        // Assert
        settings.Transport.Should().Be("stdio");
        settings.Port.Should().Be(3000);
        settings.Debug.Should().BeFalse();
        settings.ConfigFile.Should().BeNull();
    }

    [Fact]
    public void McpSettings_ShouldAllowOverridingDefaults()
    {
        // Act
        var settings = new McpSettings
        {
            Transport = "http",
            Port = 8080,
            Debug = true,
            ConfigFile = "/path/to/config.json"
        };

        // Assert
        settings.Transport.Should().Be("http");
        settings.Port.Should().Be(8080);
        settings.Debug.Should().BeTrue();
        settings.ConfigFile.Should().Be("/path/to/config.json");
    }
}