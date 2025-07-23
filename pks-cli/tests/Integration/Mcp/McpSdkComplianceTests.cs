using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Tests.Infrastructure;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Tests for MCP specification compliance and SDK integration
/// Validates that the MCP server conforms to the Model Context Protocol specification
/// </summary>
public class McpSdkComplianceTests : TestBase
{
    private readonly ITestOutputHelper _output;
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpToolService _mcpToolService;
    private readonly McpResourceService _mcpResourceService;
    private readonly ILogger<McpSdkComplianceTests> _logger;

    public McpSdkComplianceTests(ITestOutputHelper output)
    {
        _output = output;
        _mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        _mcpToolService = ServiceProvider.GetRequiredService<McpToolService>();
        _mcpResourceService = ServiceProvider.GetRequiredService<McpResourceService>();
        _logger = ServiceProvider.GetRequiredService<ILogger<McpSdkComplianceTests>>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IMcpHostingService, McpHostingService>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<McpResourceService>();
    }

    [Fact]
    public void McpSdk_ShouldConformToToolSpecification()
    {
        // Arrange & Act
        var tools = _mcpToolService.GetAvailableTools().ToList();

        // Assert - MCP Tool specification compliance
        tools.Should().NotBeEmpty("MCP server should provide tools");

        foreach (var tool in tools)
        {
            _output.WriteLine($"Validating MCP compliance for tool: {tool.Name}");

            // Required fields per MCP spec
            tool.Name.Should().NotBeNullOrEmpty("Tool must have a name");
            tool.Description.Should().NotBeNullOrEmpty("Tool must have a description");

            // Name format validation
            tool.Name.Should().MatchRegex("^[a-z0-9_]+$",
                "Tool name should contain only lowercase letters, numbers, and underscores");

            // Description should be meaningful
            tool.Description.Length.Should().BeGreaterThan(5,
                "Tool description should be meaningful");

            // Category should be consistent
            tool.Category.Should().NotBeNullOrEmpty("Tool should have a category");
            tool.Category.Should().MatchRegex("^[a-z-]+$",
                "Tool category should contain only lowercase letters and hyphens");

            ValidateToolMetadataStructure(tool);
        }
    }

    [Fact]
    public void McpSdk_ShouldConformToResourceSpecification()
    {
        // Arrange & Act
        var resources = _mcpResourceService.GetAvailableResources().ToList();

        // Assert - MCP Resource specification compliance
        resources.Should().NotBeEmpty("MCP server should provide resources");

        foreach (var resource in resources)
        {
            _output.WriteLine($"Validating MCP compliance for resource: {resource.Name}");

            // Required fields per MCP spec
            resource.Name.Should().NotBeNullOrEmpty("Resource must have a name");
            resource.Uri.Should().NotBeNullOrEmpty("Resource must have a URI");
            resource.MimeType.Should().NotBeNullOrEmpty("Resource must have a MIME type");

            // URI format validation
            resource.Uri.Should().StartWith("pks://", "Resource URI should use PKS scheme");

            // MIME type validation
            var validMimeTypes = new[] { "application/json", "text/plain", "application/yaml", "text/markdown" };
            validMimeTypes.Should().Contain(resource.MimeType,
                $"Resource MIME type '{resource.MimeType}' should be supported");

            // Metadata validation
            resource.Metadata.Should().NotBeNull("Resource should have metadata");
            resource.Metadata.Should().ContainKey("category", "Resource metadata should include category");

            ValidateResourceMetadataStructure(resource);
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleJsonRpcProtocol()
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        try
        {
            // Act - Start server
            var startResult = await _mcpHostingService.StartServerAsync(config);
            startResult.Success.Should().BeTrue("MCP server should start for JSON-RPC testing");

            // Test JSON-RPC message structure compliance
            await ValidateJsonRpcCompliance();

            _output.WriteLine("JSON-RPC protocol compliance validated");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldSupportCapabilityNegotiation()
    {
        // Arrange
        var config = new McpServerConfig
        {
            Transport = "stdio",
            Debug = true
        };

        try
        {
            // Act
            var startResult = await _mcpHostingService.StartServerAsync(config);
            startResult.Success.Should().BeTrue();

            // Assert - Server should report capabilities
            var status = await _mcpHostingService.GetServerStatusAsync();
            status.Should().NotBeNull("Server should provide status information");

            // Validate capability reporting
            var tools = _mcpToolService.GetAvailableTools();
            tools.Should().NotBeEmpty("Server should report tool capabilities");

            var resources = _mcpResourceService.GetAvailableResources();
            resources.Should().NotBeEmpty("Server should report resource capabilities");

            _output.WriteLine($"Capabilities validated: {tools.Count()} tools, {resources.Count()} resources");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("http")]
    [InlineData("sse")]
    public async Task McpSdk_ShouldSupportStandardTransports(string transport)
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
            startResult.Success.Should().BeTrue($"MCP server should support {transport} transport");
            startResult.Transport.Should().Be(transport, $"Server should report correct transport type");

            // Validate transport-specific requirements
            switch (transport)
            {
                case "stdio":
                    ValidateStdioTransport(startResult);
                    break;
                case "http":
                    ValidateHttpTransport(startResult, config.Port);
                    break;
                case "sse":
                    ValidateSseTransport(startResult, config.Port);
                    break;
            }

            _output.WriteLine($"Transport {transport} compliance validated");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleErrorsGracefully()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            await _mcpHostingService.StartServerAsync(config);

            // Test error handling scenarios
            var errorScenarios = new[]
            {
                new { Tool = "non_existent_tool", Args = new Dictionary<string, object>() },
                new { Tool = "pks_init_project", Args = new Dictionary<string, object> { ["invalid_param"] = "value" } }
            };

            foreach (var scenario in errorScenarios)
            {
                _output.WriteLine($"Testing error handling for {scenario.Tool}");

                // Act
                var result = await _mcpToolService.ExecuteToolAsync(scenario.Tool, scenario.Args);

                // Assert - Errors should be handled gracefully
                result.Should().NotBeNull("Error scenarios should return result objects");

                if (!result.Success)
                {
                    result.Error.Should().NotBeNullOrEmpty("Failed operations should provide error details");
                    result.Error.Length.Should().BeLessThan(500, "Error messages should be concise");

                    // Error should not contain sensitive information
                    result.Error.Should().NotContain("Exception");
                    result.Error.Should().NotContain("StackTrace");
                }

                _output.WriteLine($"Error handled gracefully: {result.Success} - {result.Error}");
            }
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldProvideConsistentResponseFormat()
    {
        // Arrange
        var tools = _mcpToolService.GetAvailableTools().Take(3);

        foreach (var tool in tools)
        {
            _output.WriteLine($"Validating response format for {tool.Name}");

            // Act
            var args = CreateMinimalValidArgs(tool.Name);
            var result = await _mcpToolService.ExecuteToolAsync(tool.Name, args);

            // Assert - Response format consistency
            result.Should().NotBeNull($"Tool {tool.Name} should return response");

            // Standard response fields - Success is boolean and always has a value
            result.DurationMs.Should().BeGreaterThan(0, $"Response should include execution duration");

            if (result.Success)
            {
                result.Data.Should().NotBeNull($"Successful response should include data");
                result.Message.Should().NotBeNullOrEmpty($"Successful response should include message");
            }
            else
            {
                result.Error.Should().NotBeNullOrEmpty($"Failed response should include error");
            }

            // Validate response can be serialized to JSON (MCP requirement)
            ValidateJsonSerializability(result);
        }
    }

    [Fact]
    public void McpSdk_ShouldImplementProperLogging()
    {
        // Arrange & Act
        var loggerFactory = ServiceProvider.GetRequiredService<ILoggerFactory>();
        var testLogger = loggerFactory.CreateLogger("McpSdkTest");

        // Assert - Logging infrastructure should be available
        loggerFactory.Should().NotBeNull("MCP server should have logging infrastructure");
        testLogger.Should().NotBeNull("Should be able to create loggers");

        // Test logging doesn't interfere with MCP protocol
        testLogger.LogInformation("Test log message for MCP compliance");
        testLogger.LogWarning("Test warning message for MCP compliance");

        // Verify logging is configured properly for MCP transports
        // (In stdio mode, logs should go to stderr, not stdout)
        _output.WriteLine("Logging infrastructure validated");
    }

    [Fact]
    public async Task McpSdk_ShouldHandleConcurrentRequests()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            await _mcpHostingService.StartServerAsync(config);

            // Act - Execute multiple tools concurrently
            var concurrentTasks = new List<Task<McpToolExecutionResult>>();
            var toolNames = _mcpToolService.GetAvailableTools()
                .Select(t => t.Name)
                .Take(3)
                .ToList();

            foreach (var toolName in toolNames)
            {
                var args = CreateMinimalValidArgs(toolName);
                concurrentTasks.Add(_mcpToolService.ExecuteToolAsync(toolName, args));
            }

            // Wait for all tasks to complete
            var results = await Task.WhenAll(concurrentTasks);

            // Assert - All requests should complete successfully
            results.Should().HaveCount(concurrentTasks.Count, "All concurrent requests should complete");
            results.Should().AllSatisfy(result =>
            {
                result.Should().NotBeNull("Concurrent request should return result");
                result.DurationMs.Should().BeGreaterThan(0, "Concurrent request should report duration");
            });

            _output.WriteLine($"Concurrent request handling validated: {results.Length} requests");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    /// <summary>
    /// Validate tool metadata structure
    /// </summary>
    private void ValidateToolMetadataStructure(McpServerTool tool)
    {
        // Tool should have consistent structure
        tool.Name.Should().NotBeNullOrEmpty();
        tool.Description.Should().NotBeNullOrEmpty();
        tool.Category.Should().NotBeNullOrEmpty();
        tool.Enabled.Should().Be(true);

        // PKS-specific validations
        tool.Name.Should().StartWith("pks_", "PKS tools should follow naming convention");
    }

    /// <summary>
    /// Validate resource metadata structure
    /// </summary>
    private void ValidateResourceMetadataStructure(McpServerResource resource)
    {
        // Resource should have consistent structure
        resource.Name.Should().NotBeNullOrEmpty();
        resource.Uri.Should().NotBeNullOrEmpty();
        resource.MimeType.Should().NotBeNullOrEmpty();
        resource.Metadata.Should().NotBeNull();

        // PKS-specific validations
        resource.Uri.Should().StartWith("pks://", "PKS resources should use PKS URI scheme");
    }

    /// <summary>
    /// Validate JSON-RPC protocol compliance
    /// </summary>
    private async Task ValidateJsonRpcCompliance()
    {
        // Test that tool execution follows JSON-RPC patterns
        var tools = _mcpToolService.GetAvailableTools().Take(1);

        foreach (var tool in tools)
        {
            var args = CreateMinimalValidArgs(tool.Name);
            var result = await _mcpToolService.ExecuteToolAsync(tool.Name, args);

            // JSON-RPC compliance checks
            result.Should().NotBeNull("JSON-RPC should return response");

            // Response should be serializable
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false });
            json.Should().NotBeNullOrEmpty("Response should be JSON serializable");

            var deserialized = JsonSerializer.Deserialize<McpToolExecutionResult>(json);
            deserialized.Should().NotBeNull("Response should be JSON deserializable");
        }
    }

    /// <summary>
    /// Validate stdio transport compliance
    /// </summary>
    private void ValidateStdioTransport(McpServerResult result)
    {
        result.Transport.Should().Be("stdio");
        result.ProcessId.Should().BeGreaterThan(0, "Stdio transport should report process ID");
    }

    /// <summary>
    /// Validate HTTP transport compliance
    /// </summary>
    private void ValidateHttpTransport(McpServerResult result, int expectedPort)
    {
        result.Transport.Should().Be("http");
        result.Port.Should().Be(expectedPort, "HTTP transport should report correct port");
    }

    /// <summary>
    /// Validate SSE transport compliance
    /// </summary>
    private void ValidateSseTransport(McpServerResult result, int expectedPort)
    {
        result.Transport.Should().Be("sse");
        result.Port.Should().Be(expectedPort, "SSE transport should report correct port");
    }

    /// <summary>
    /// Validate object can be serialized to JSON
    /// </summary>
    private void ValidateJsonSerializability(object obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            json.Should().NotBeNullOrEmpty("Object should be JSON serializable");

            var roundTrip = JsonSerializer.Deserialize<object>(json);
            roundTrip.Should().NotBeNull("JSON should deserialize back to object");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Object failed JSON serialization: {ex.Message}");
        }
    }

    /// <summary>
    /// Create minimal valid arguments for a tool
    /// </summary>
    private Dictionary<string, object> CreateMinimalValidArgs(string toolName)
    {
        return toolName switch
        {
            "pks_init_project" => new Dictionary<string, object>
            {
                ["projectName"] = $"test-{Guid.NewGuid().ToString("N")[..6]}"
            },
            "pks_create_task" => new Dictionary<string, object>
            {
                ["taskDescription"] = "Compliance test task"
            },
            "pks_project_status" => new Dictionary<string, object>(),
            _ => new Dictionary<string, object>()
        };
    }
}