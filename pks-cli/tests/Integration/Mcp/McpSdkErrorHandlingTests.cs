using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Tests for MCP SDK error handling and edge cases
/// Validates graceful degradation and proper error reporting
/// </summary>
public class McpSdkErrorHandlingTests : TestBase
{
    private readonly ITestOutputHelper _output;
    private readonly IMcpHostingService _mcpHostingService;
    private readonly McpToolService _mcpToolService;
    private readonly ILogger<McpSdkErrorHandlingTests> _logger;

    public McpSdkErrorHandlingTests(ITestOutputHelper output)
    {
        _output = output;
        _mcpHostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();
        _mcpToolService = ServiceProvider.GetRequiredService<McpToolService>();
        _logger = ServiceProvider.GetRequiredService<ILogger<McpSdkErrorHandlingTests>>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IMcpHostingService, McpHostingService>();
        services.AddSingleton<McpToolService>();
        services.AddSingleton<McpResourceService>();
    }

    [Fact]
    public async Task McpSdk_ShouldHandleInvalidToolNames()
    {
        // Arrange
        var invalidToolNames = new[]
        {
            "",
            "   ",
            "invalid-tool-name-that-does-not-exist",
            "pks_invalid_tool",
            "UPPERCASE_TOOL",
            "tool with spaces",
            "tool@with#special!chars",
            null
        };

        foreach (var toolName in invalidToolNames.Where(t => t != null))
        {
            _output.WriteLine($"Testing invalid tool name: '{toolName}'");

            // Act
            var result = await _mcpToolService.ExecuteToolAsync(toolName, new Dictionary<string, object>());

            // Assert
            result.Should().NotBeNull($"Should handle invalid tool name '{toolName}' gracefully");
            result.Success.Should().BeFalse($"Invalid tool name '{toolName}' should fail");
            result.Error.Should().NotBeNullOrEmpty($"Should provide error message for '{toolName}'");
            result.Error.Should().Contain("tool");
        }

        // Test null tool name
        var nullResult = await _mcpToolService.ExecuteToolAsync(null!, new Dictionary<string, object>());
        nullResult.Should().NotBeNull("Should handle null tool name");
        nullResult.Success.Should().BeFalse("Null tool name should fail");
    }

    [Fact]
    public async Task McpSdk_ShouldHandleInvalidParameters()
    {
        // Arrange
        var validTool = "pks_init_project";
        var invalidParameterSets = new[]
        {
            new { Name = "null_arguments", Args = (Dictionary<string, object>?)null },
            new { Name = "extremely_long_values", Args = new Dictionary<string, object>
                {
                    ["projectName"] = new string('a', 10000),
                    ["description"] = new string('b', 50000)
                }
            },
            new { Name = "special_characters", Args = new Dictionary<string, object>
                {
                    ["projectName"] = "project<>:\"/\\|?*",
                    ["description"] = "Description with \0 null character"
                }
            },
            new { Name = "sql_injection_attempt", Args = new Dictionary<string, object>
                {
                    ["projectName"] = "'; DROP TABLE users; --",
                    ["description"] = "1' OR '1'='1"
                }
            },
            new { Name = "script_injection_attempt", Args = new Dictionary<string, object>
                {
                    ["projectName"] = "<script>alert('xss')</script>",
                    ["description"] = "javascript:alert('test')"
                }
            }
        };

        foreach (var paramSet in invalidParameterSets)
        {
            _output.WriteLine($"Testing invalid parameters: {paramSet.Name}");

            // Act
            var result = await _mcpToolService.ExecuteToolAsync(validTool, paramSet.Args!);

            // Assert
            result.Should().NotBeNull($"Should handle {paramSet.Name} gracefully");

            // Should either succeed with sanitized values or fail gracefully
            if (!result.Success)
            {
                result.Error.Should().NotBeNullOrEmpty($"Should provide error for {paramSet.Name}");
                result.Error.Length.Should().BeLessThan(1000, "Error messages should be reasonable length");
            }

            // Security check - error should not echo back potentially dangerous content
            if (result.Error?.Contains("<script>") == true || result.Error?.Contains("DROP TABLE") == true)
            {
                throw new Exception($"Security risk: Error message echoes back dangerous content: {result.Error}");
            }

            _output.WriteLine($"Parameter validation handled: {paramSet.Name} -> {result.Success}");
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleTypeConversionErrors()
    {
        // Arrange
        var typeConversionTests = new[]
        {
            new { Tool = "pks_init_project", Args = new Dictionary<string, object>
                {
                    ["projectName"] = 12345, // Should be string
                    ["agentic"] = "not-a-boolean", // Should be bool
                    ["mcp"] = new { complex = "object" } // Should be bool
                }
            },
            new { Tool = "pks_create_task", Args = new Dictionary<string, object>
                {
                    ["taskDescription"] = new[] { "array", "instead", "of", "string" },
                    ["priority"] = 999 // Should be string
                }
            }
        };

        foreach (var test in typeConversionTests)
        {
            _output.WriteLine($"Testing type conversion for {test.Tool}");

            // Act
            var result = await _mcpToolService.ExecuteToolAsync(test.Tool, test.Args);

            // Assert
            result.Should().NotBeNull($"Should handle type conversion errors for {test.Tool}");

            if (!result.Success)
            {
                result.Error.Should().NotBeNullOrEmpty("Type conversion errors should provide meaningful messages");
                result.Error.Should().NotContain("Exception", "Error messages should be user-friendly");
            }

            _output.WriteLine($"Type conversion handled: {test.Tool} -> Success: {result.Success}");
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleServerStateErrors()
    {
        // Test 1: Execute tools when server is not started
        _output.WriteLine("Testing tool execution without server start");

        var tools = _mcpToolService.GetAvailableTools().Take(1);
        foreach (var tool in tools)
        {
            var args = new Dictionary<string, object> { ["projectName"] = "test" };
            var result = await _mcpToolService.ExecuteToolAsync(tool.Name, args);

            result.Should().NotBeNull("Should handle execution without server");
            // May succeed (tools work independently) or fail (server-dependent)
            _output.WriteLine($"Tool {tool.Name} execution without server: {result.Success}");
        }

        // Test 2: Start server with invalid configuration
        var invalidConfigs = new[]
        {
            new McpServerConfig { Transport = "invalid-transport" },
            new McpServerConfig { Transport = "http", Port = -1 },
            new McpServerConfig { Transport = "http", Port = 70000 }, // Invalid port
            new McpServerConfig { Transport = null! }
        };

        foreach (var config in invalidConfigs)
        {
            _output.WriteLine($"Testing invalid config: Transport={config.Transport}, Port={config.Port}");

            var startResult = await _mcpHostingService.StartServerAsync(config);
            startResult.Should().NotBeNull("Should handle invalid config gracefully");

            if (!startResult.Success)
            {
                startResult.Message.Should().NotBeNullOrEmpty("Should provide error message for invalid config");
            }

            // Ensure cleanup
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleConcurrentServerOperations()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        // Test concurrent start operations
        var startTasks = new List<Task<McpServerResult>>();
        for (int i = 0; i < 5; i++)
        {
            startTasks.Add(_mcpHostingService.StartServerAsync(config));
        }

        // Act
        var startResults = await Task.WhenAll(startTasks);

        // Assert
        startResults.Should().HaveCount(5, "All start operations should complete");

        // Only one should succeed, others should fail gracefully
        var successCount = startResults.Count(r => r.Success);
        successCount.Should().Be(1, "Only one server start should succeed");

        var failureCount = startResults.Count(r => !r.Success);
        failureCount.Should().Be(4, "Other start attempts should fail gracefully");

        // All failures should have meaningful error messages
        var failures = startResults.Where(r => !r.Success);
        failures.Should().AllSatisfy(result =>
        {
            result.Message.Should().NotBeNullOrEmpty("Failed start should have error message");
            result.Message.Should().Contain("already", "Error should indicate server is already running");
        });

        // Cleanup
        await _mcpHostingService.StopServerAsync();

        _output.WriteLine($"Concurrent operations handled: {successCount} success, {failureCount} expected failures");
    }

    [Fact]
    public async Task McpSdk_ShouldHandleResourceExhaustion()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            await _mcpHostingService.StartServerAsync(config);

            // Act - Execute many tools simultaneously to test resource handling
            var concurrentTasks = new List<Task<McpToolExecutionResult>>();
            var toolName = "pks_project_status";

            for (int i = 0; i < 50; i++) // High concurrency
            {
                var args = new Dictionary<string, object> { ["detailed"] = i % 2 == 0 };
                concurrentTasks.Add(_mcpToolService.ExecuteToolAsync(toolName, args));
            }

            var results = await Task.WhenAll(concurrentTasks);

            // Assert - All operations should complete (may succeed or fail gracefully)
            results.Should().HaveCount(50, "All concurrent operations should complete");
            results.Should().AllSatisfy(result =>
            {
                result.Should().NotBeNull("High concurrency should not cause null results");
                result.DurationMs.Should().BeGreaterThan(0, "Duration should be reported even under load");
            });

            var successRate = results.Count(r => r.Success) / (double)results.Length;
            successRate.Should().BeGreaterThan(0.5, "At least 50% of requests should succeed under load");

            _output.WriteLine($"Resource exhaustion test: {results.Count(r => r.Success)}/{results.Length} succeeded");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleTimeoutScenarios()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            await _mcpHostingService.StartServerAsync(config);

            // Test tool execution timeout behavior
            var longRunningArgs = new Dictionary<string, object>
            {
                ["projectName"] = "timeout-test-project",
                ["description"] = new string('a', 1000) // Large description
            };

            // Act - Execute tool and measure time
            var startTime = DateTime.UtcNow;
            var result = await _mcpToolService.ExecuteToolAsync("pks_init_project", longRunningArgs);
            var duration = DateTime.UtcNow - startTime;

            // Assert
            result.Should().NotBeNull("Tool should complete even with large inputs");
            duration.TotalSeconds.Should().BeLessThan(30, "Tool execution should not hang indefinitely");

            if (!result.Success && result.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true)
            {
                _output.WriteLine($"Tool properly timed out after {duration.TotalSeconds:F2} seconds");
            }
            else
            {
                _output.WriteLine($"Tool completed in {duration.TotalSeconds:F2} seconds");
            }
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleMemoryPressure()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        try
        {
            await _mcpHostingService.StartServerAsync(config);

            // Act - Create memory pressure with large payloads
            var largeArgs = new Dictionary<string, object>
            {
                ["projectName"] = "memory-test",
                ["description"] = new string('x', 100000), // 100KB description
                ["template"] = "console"
            };

            var results = new List<McpToolExecutionResult>();

            // Execute multiple times to test memory handling
            for (int i = 0; i < 10; i++)
            {
                var result = await _mcpToolService.ExecuteToolAsync("pks_init_project", largeArgs);
                results.Add(result);

                // Small delay to allow garbage collection
                await Task.Delay(100);
            }

            // Assert - System should handle memory pressure gracefully
            results.Should().HaveCount(10, "All operations should complete under memory pressure");
            results.Should().AllSatisfy(result =>
            {
                result.Should().NotBeNull("Memory pressure should not cause null results");
            });

            // Check if any operations failed due to memory issues
            var memoryFailures = results.Count(r => !r.Success &&
                r.Error?.Contains("memory", StringComparison.OrdinalIgnoreCase) == true);

            _output.WriteLine($"Memory pressure test: {results.Count(r => r.Success)}/{results.Count} succeeded, {memoryFailures} memory-related failures");
        }
        finally
        {
            await _mcpHostingService.StopServerAsync();

            // Force garbage collection after test
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    [Fact]
    public async Task McpSdk_ShouldRecoverFromTransientErrors()
    {
        // Arrange
        var config = new McpServerConfig { Transport = "stdio", Debug = true };

        // Test recovery from server restart
        await _mcpHostingService.StartServerAsync(config);

        // Execute a tool successfully
        var initialResult = await _mcpToolService.ExecuteToolAsync("pks_project_status", new Dictionary<string, object>());
        initialResult.Should().NotBeNull("Initial tool execution should work");

        // Stop and restart server
        await _mcpHostingService.StopServerAsync();
        await Task.Delay(500); // Brief pause
        await _mcpHostingService.StartServerAsync(config);

        // Execute tool again after restart
        var recoveryResult = await _mcpToolService.ExecuteToolAsync("pks_project_status", new Dictionary<string, object>());

        // Assert - Should work after restart
        recoveryResult.Should().NotBeNull("Tool should work after server restart");

        // Cleanup
        await _mcpHostingService.StopServerAsync();

        _output.WriteLine($"Recovery test: Initial={initialResult.Success}, After restart={recoveryResult.Success}");
    }

    [Fact]
    public void McpSdk_ShouldHandleDisposalGracefully()
    {
        // Arrange
        var hostingService = ServiceProvider.GetRequiredService<IMcpHostingService>();

        // Act & Assert - Disposal should not throw exceptions
        if (hostingService is IDisposable disposable)
        {
            Action disposal = () => disposable.Dispose();
            disposal.Should().NotThrow("Service disposal should be graceful");

            // Multiple disposals should be safe
            disposal.Should().NotThrow("Multiple disposals should be safe");
        }

        _output.WriteLine("Service disposal handled gracefully");
    }
}