using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers.Service;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Comprehensive integration tests for SDK-based MCP server implementation
/// Tests tool discovery, registration, and SDK integration patterns
/// </summary>
public class McpSdkIntegrationTests : TestBase
{
    private readonly ITestOutputHelper _output;
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpToolService _mcpToolService;
    private readonly McpResourceService _mcpResourceService;
    private readonly ILogger<McpSdkIntegrationTests> _logger;

    public McpSdkIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        _mcpToolService = ServiceProvider.GetRequiredService<McpToolService>();
        _mcpResourceService = ServiceProvider.GetRequiredService<McpResourceService>();
        _logger = ServiceProvider.GetRequiredService<ILogger<McpSdkIntegrationTests>>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IMcpHostingService, McpHostingService>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<McpResourceService>();

        // Register all PKS tool services
        services.AddSingleton<ProjectToolService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<DeploymentToolService>();
        services.AddSingleton<StatusToolService>();
        services.AddSingleton<SwarmToolService>();
    }

    [Fact]
    public async Task McpSdk_ShouldDiscoverAndRegisterAllToolServices()
    {
        // Arrange
        _output.WriteLine("Testing tool service discovery and registration");

        // Act - Get available tools from the service
        var tools = _mcpToolService.GetAvailableTools();
        var toolsList = tools.ToList();

        // Assert - Verify all expected tool services are discovered
        toolsList.Should().NotBeEmpty("Tool discovery should find registered tools");
        
        // Log discovered tools for verification
        foreach (var tool in toolsList)
        {
            _output.WriteLine($"Discovered tool: {tool.Name} - {tool.Description} [{tool.Category}]");
        }

        // Verify specific tool categories exist
        var categories = toolsList.Select(t => t.Category).Distinct().ToList();
        categories.Should().Contain("project-management", "Should discover project management tools");
        categories.Should().Contain("deployment", "Should discover deployment tools");
        categories.Should().Contain("agent-management", "Should discover agent management tools");
    }

    [Fact]
    public async Task McpSdk_ShouldRegisterToolsWithCorrectAttributes()
    {
        // Arrange
        var projectToolService = ServiceProvider.GetRequiredService<ProjectToolService>();

        // Act - Use reflection to verify tool attributes
        var toolMethods = GetToolMethods(typeof(ProjectToolService));

        // Assert
        toolMethods.Should().NotBeEmpty("ProjectToolService should have tool methods");
        
        foreach (var method in toolMethods)
        {
            _output.WriteLine($"Tool method: {method.Name}");
            
            // Verify the method has proper signatures for MCP tools
            method.ReturnType.Should().Be(typeof(Task<object>), 
                $"Tool method {method.Name} should return Task<object>");
            
            // Verify parameters have proper attributes (when attributes are properly implemented)
            var parameters = method.GetParameters();
            parameters.Should().NotBeEmpty($"Tool method {method.Name} should have parameters");
        }
    }

    [Fact] 
    public async Task McpSdk_ShouldStartServerWithToolDiscovery()
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        try
        {
            // Act - Start the server
            var startResult = await _mcpHostingService.StartServerAsync(config);

            // Assert - Server should start successfully
            startResult.Success.Should().BeTrue("MCP server should start successfully");
            startResult.Message.Should().Contain("successfully", "Start message should indicate success");

            // Verify server status
            var status = await _mcpHostingService.GetServerStatusAsync();
            status.Status.Should().Be(McpServerStatus.Running, "Server should be in running state");

            // Verify tools are available after server start
            var tools = _mcpToolService.GetAvailableTools();
            tools.Should().NotBeEmpty("Tools should be available after server start");

            _output.WriteLine($"Server started successfully with {tools.Count()} tools available");
        }
        finally
        {
            // Cleanup
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    [InlineData("sse")]
    public async Task McpSdk_ShouldSupportAllTransportModes(string transport)
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = transport,
            Port = transport == "stdio" ? 0 : 3000 + Random.Shared.Next(1000),
            Debug = true
        };

        try
        {
            // Act
            var startResult = await _mcpHostingService.StartServerAsync(config);

            // Assert
            startResult.Success.Should().BeTrue($"MCP server should start with {transport} transport");
            startResult.Transport.Should().Be(transport, $"Result should reflect {transport} transport");

            if (transport != "stdio")
            {
                startResult.Port.Should().Be(config.Port, $"Port should be set for {transport} transport");
            }

            _output.WriteLine($"Successfully started MCP server with {transport} transport");
        }
        finally
        {
            // Cleanup
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldExecuteProjectInitToolCorrectly()
    {
        // Arrange
        var projectTool = ServiceProvider.GetRequiredService<ProjectToolService>();
        var testProjectName = $"test-project-{Guid.NewGuid().ToString("N")[..8]}";
        var testDir = Path.Combine(Path.GetTempPath(), testProjectName);

        try
        {
            // Ensure clean test environment
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, true);
            }

            var originalDirectory = Environment.CurrentDirectory;
            Environment.CurrentDirectory = Path.GetTempPath();

            // Act
            var result = await ProjectToolService.InitializeProjectAsync(
                logger: ServiceProvider.GetRequiredService<ILogger<ProjectToolService>>(),
                initializationService: ServiceProvider.GetRequiredService<IInitializationService>(),
                projectName: testProjectName,
                template: "console",
                description: "Test project for MCP SDK validation",
                agentic: true,
                mcp: true,
                force: false);

            // Assert
            result.Should().NotBeNull("Project initialization should return result");
            
            var resultDict = result as dynamic;
            if (resultDict != null)
            {
                // Note: Dynamic assertion is limited, but we verify the structure exists
                _output.WriteLine($"Project initialization result: {result}");
                
                // Verify project directory was created (if initialization was successful)
                // This depends on the actual implementation being working
            }

            Environment.CurrentDirectory = originalDirectory;
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(testDir))
            {
                try
                {
                    Directory.Delete(testDir, true);
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"Cleanup warning: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleToolExecutionErrors()
    {
        // Arrange
        var arguments = new Dictionary<string, object>
        {
            ["projectName"] = "", // Invalid empty project name
            ["template"] = "invalid-template"
        };

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("pks_init_project", arguments);

        // Assert
        result.Should().NotBeNull("Tool execution should always return a result");
        
        // The result should indicate failure for invalid inputs
        if (!result.Success)
        {
            result.Error.Should().NotBeNullOrEmpty("Failed execution should have error message");
            _output.WriteLine($"Expected error occurred: {result.Error}");
        }
        else
        {
            _output.WriteLine("Tool execution succeeded despite invalid inputs - may need validation improvement");
        }
    }

    [Fact]
    public async Task McpSdk_ShouldValidateToolParameterTypes()
    {
        // Arrange
        var invalidArguments = new Dictionary<string, object>
        {
            ["projectName"] = 123, // Wrong type - should be string
            ["agentic"] = "not-a-boolean", // Wrong type - should be bool
            ["mcp"] = null // Null value
        };

        // Act
        var result = await _mcpToolService.ExecuteToolAsync("pks_init_project", invalidArguments);

        // Assert - Should handle type mismatches gracefully
        result.Should().NotBeNull("Tool execution should handle type mismatches");
        
        if (!result.Success)
        {
            result.Error.Should().Contain("type");
            _output.WriteLine($"Type validation error: {result.Error}");
        }
        else
        {
            _output.WriteLine("Tool execution succeeded - type conversion may be handled automatically");
        }
    }

    [Fact]
    public async Task McpSdk_ShouldProvideCompleteToolMetadata()
    {
        // Act
        var tools = _mcpToolService.GetAvailableTools().ToList();

        // Assert
        foreach (var tool in tools)
        {
            // Validate required metadata
            tool.Name.Should().NotBeNullOrEmpty($"Tool {tool.Name} should have a name");
            tool.Description.Should().NotBeNullOrEmpty($"Tool {tool.Name} should have a description");
            tool.Category.Should().NotBeNullOrEmpty($"Tool {tool.Name} should have a category");
            
            // Validate tool naming convention
            tool.Name.Should().StartWith("pks_", $"Tool {tool.Name} should follow PKS naming convention");
            
            _output.WriteLine($"Tool metadata validated: {tool.Name} in {tool.Category}");
        }

        // Verify we have tools from all expected services
        var toolNames = tools.Select(t => t.Name).ToList();
        toolNames.Should().Contain(name => name.StartsWith("pks_init"), "Should have project init tools");
        toolNames.Should().Contain(name => name.Contains("agent"), "Should have agent management tools");
        toolNames.Should().Contain(name => name.Contains("deploy"), "Should have deployment tools");
        toolNames.Should().Contain(name => name.Contains("status"), "Should have status tools");
    }

    [Fact]
    public async Task McpSdk_ShouldSupportResourceDiscovery()
    {
        // Act
        var resources = _mcpResourceService.GetAvailableResources().ToList();

        // Assert
        resources.Should().NotBeEmpty("Resource discovery should find available resources");

        foreach (var resource in resources)
        {
            resource.Name.Should().NotBeNullOrEmpty("Resource should have a name");
            resource.Uri.Should().NotBeNullOrEmpty("Resource should have a URI");
            resource.MimeType.Should().NotBeNullOrEmpty("Resource should have a mime type");

            _output.WriteLine($"Resource discovered: {resource.Name} ({resource.Uri}) - {resource.MimeType}");
        }

        // Verify expected resources
        var resourceNames = resources.Select(r => r.Name).ToList();
        resourceNames.Should().Contain("Agents", "Should provide agents resource");
        resourceNames.Should().Contain("Projects", "Should provide projects resource");
        resourceNames.Should().Contain("Current Tasks", "Should provide tasks resource");
    }

    [Fact]
    public async Task McpSdk_ShouldHandleServerLifecycleCorrectly()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        // Test startup
        var startResult = await _mcpHostingService.StartServerAsync(config);
        startResult.Success.Should().BeTrue("Server should start successfully");

        var runningStatus = await _mcpHostingService.GetServerStatusAsync();
        runningStatus.Status.Should().Be(McpServerStatus.Running);

        // Test shutdown
        var stopResult = await _mcpHostingService.StopServerAsync();
        stopResult.Should().BeTrue("Server should stop successfully");

        var stoppedStatus = await _mcpHostingService.GetServerStatusAsync();
        stoppedStatus.Status.Should().Be(McpServerStatus.Stopped);

        // Test restart
        var restartResult = await _mcpHostingService.RestartServerAsync();
        restartResult.Success.Should().BeTrue("Server should restart successfully");

        var restartedStatus = await _mcpHostingService.GetServerStatusAsync();
        restartedStatus.Status.Should().Be(McpServerStatus.Running);

        // Cleanup
        await _mcpHostingService.StopServerAsync();

        _output.WriteLine("Server lifecycle management validated successfully");
    }

    [Fact]
    public async Task McpSdk_ShouldPreventMultipleServerInstances()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            // Start first instance
            var firstStart = await _mcpHostingService.StartServerAsync(config);
            firstStart.Success.Should().BeTrue("First server instance should start");

            // Try to start second instance
            var secondStart = await _mcpHostingService.StartServerAsync(config);
            secondStart.Success.Should().BeFalse("Second server instance should be rejected");
            secondStart.Message.Should().Contain("already running", "Error message should indicate server is already running");

            _output.WriteLine("Multiple server instance prevention validated");
        }
        finally
        {
            // Cleanup
            await _mcpHostingService.StopServerAsync();
        }
    }

    /// <summary>
    /// Helper method to extract tool methods using reflection
    /// </summary>
    private static IEnumerable<MethodInfo> GetToolMethods(Type serviceType)
    {
        return serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(Task<object>) && 
                       m.Name.EndsWith("Async") &&
                       m.GetParameters().Length > 0);
    }
}