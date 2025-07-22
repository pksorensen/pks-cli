using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.CLI.Tests.Infrastructure;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Integration.Mcp;

/// <summary>
/// Tests for validating SDK-based tool registration and discovery
/// Focuses on attribute-based tool configuration and parameter validation
/// </summary>
public class McpSdkToolValidationTests : TestBase
{
    private readonly ITestOutputHelper _output;
    private readonly McpToolService _mcpToolService;
    private readonly ILogger<McpSdkToolValidationTests> _logger;

    public McpSdkToolValidationTests(ITestOutputHelper output)
    {
        _output = output;
        _mcpToolService = ServiceProvider.GetRequiredService<McpToolService>();
        _logger = ServiceProvider.GetRequiredService<ILogger<McpSdkToolValidationTests>>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<McpToolService>();
        
        // Register all tool services for testing
        services.AddSingleton<ProjectToolService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<DeploymentToolService>();
        services.AddSingleton<StatusToolService>();
        services.AddSingleton<SwarmToolService>();
    }

    [Fact]
    public void McpSdk_ShouldDiscoverAllToolServicesViaReflection()
    {
        // Arrange & Act
        var toolServiceTypes = GetToolServiceTypes();
        
        // Assert
        toolServiceTypes.Should().NotBeEmpty("Should discover tool service types");
        toolServiceTypes.Should().HaveCountGreaterThan(3, "Should discover multiple tool services");

        foreach (var serviceType in toolServiceTypes)
        {
            _output.WriteLine($"Discovered tool service: {serviceType.Name}");
            
            // Verify service can be resolved from DI container
            var service = ServiceProvider.GetService(serviceType);
            service.Should().NotBeNull($"Tool service {serviceType.Name} should be resolvable from DI container");
        }
    }

    [Fact]
    public void McpSdk_ShouldValidateToolMethodSignatures()
    {
        // Arrange
        var toolServiceTypes = GetToolServiceTypes();

        foreach (var serviceType in toolServiceTypes)
        {
            // Act
            var toolMethods = GetToolMethods(serviceType);

            // Assert
            toolMethods.Should().NotBeEmpty($"{serviceType.Name} should have tool methods");

            foreach (var method in toolMethods)
            {
                _output.WriteLine($"Validating tool method: {serviceType.Name}.{method.Name}");

                // Validate return type
                method.ReturnType.Should().Be(typeof(Task<object>), 
                    $"Tool method {method.Name} must return Task<object>");

                // Validate method is public
                method.IsPublic.Should().BeTrue($"Tool method {method.Name} must be public");

                // Validate method is not static
                method.IsStatic.Should().BeFalse($"Tool method {method.Name} should not be static");

                // Validate method naming convention
                method.Name.Should().EndWith("Async", $"Tool method {method.Name} should follow async naming convention");

                ValidateToolMethodParameters(method);
            }
        }
    }

    [Fact]
    public void McpSdk_ShouldValidateToolNamingConventions()
    {
        // Arrange
        var tools = _mcpToolService.GetAvailableTools().ToList();

        // Assert
        tools.Should().NotBeEmpty("Should have registered tools");

        foreach (var tool in tools)
        {
            _output.WriteLine($"Validating tool naming: {tool.Name}");

            // Validate PKS tool naming convention
            tool.Name.Should().StartWith("pks_", $"Tool {tool.Name} should start with 'pks_'");
            tool.Name.Should().NotContain(" ", $"Tool {tool.Name} should not contain spaces");
            tool.Name.Should().BeEquivalentTo(tool.Name.ToLowerInvariant(), $"Tool {tool.Name} should be lowercase");

            // Validate categories are consistent
            var validCategories = new[] 
            { 
                "project-management", 
                "deployment", 
                "agent-management", 
                "task-management",
                "monitoring",
                "utility"
            };
            
            validCategories.Should().Contain(tool.Category, 
                $"Tool {tool.Name} category '{tool.Category}' should be from valid categories");

            // Validate descriptions are meaningful
            tool.Description.Should().NotBeNullOrEmpty($"Tool {tool.Name} should have a description");
            tool.Description.Length.Should().BeGreaterThan(10, $"Tool {tool.Name} description should be descriptive");
        }
    }

    [Fact]
    public async Task McpSdk_ShouldValidateToolParameterHandling()
    {
        // Arrange
        var testCases = new[]
        {
            new
            {
                ToolName = "pks_init_project",
                ValidArgs = new Dictionary<string, object>
                {
                    ["projectName"] = "test-project",
                    ["template"] = "console",
                    ["description"] = "Test project"
                },
                InvalidArgs = new Dictionary<string, object>
                {
                    ["projectName"] = "", // Empty project name
                    ["template"] = "invalid-template"
                }
            },
            new
            {
                ToolName = "pks_create_task",
                ValidArgs = new Dictionary<string, object>
                {
                    ["taskDescription"] = "Test task",
                    ["agentType"] = "deployment",
                    ["priority"] = "medium"
                },
                InvalidArgs = new Dictionary<string, object>
                {
                    ["taskDescription"] = "", // Empty description
                    ["priority"] = "invalid-priority"
                }
            }
        };

        foreach (var testCase in testCases)
        {
            _output.WriteLine($"Testing parameter handling for tool: {testCase.ToolName}");

            // Test valid arguments
            var validResult = await _mcpToolService.ExecuteToolAsync(testCase.ToolName, testCase.ValidArgs);
            validResult.Should().NotBeNull($"Tool {testCase.ToolName} should handle valid arguments");

            // Test invalid arguments - should handle gracefully
            var invalidResult = await _mcpToolService.ExecuteToolAsync(testCase.ToolName, testCase.InvalidArgs);
            invalidResult.Should().NotBeNull($"Tool {testCase.ToolName} should handle invalid arguments gracefully");
            
            if (!invalidResult.Success)
            {
                invalidResult.Error.Should().NotBeNullOrEmpty($"Failed execution should provide error details");
                _output.WriteLine($"Expected validation error: {invalidResult.Error}");
            }
        }
    }

    [Fact]
    public async Task McpSdk_ShouldHandleMissingRequiredParameters()
    {
        // Arrange
        var toolsWithRequiredParams = new[]
        {
            new { Tool = "pks_init_project", RequiredParam = "projectName" },
            new { Tool = "pks_create_task", RequiredParam = "taskDescription" }
        };

        foreach (var testCase in toolsWithRequiredParams)
        {
            _output.WriteLine($"Testing missing required parameter for {testCase.Tool}");

            // Act - Call tool without required parameter
            var emptyArgs = new Dictionary<string, object>();
            var result = await _mcpToolService.ExecuteToolAsync(testCase.Tool, emptyArgs);

            // Assert
            result.Should().NotBeNull($"Tool {testCase.Tool} should handle missing parameters");
            
            if (!result.Success)
            {
                result.Error.Should().Contain(testCase.RequiredParam,
                    $"Error should mention missing parameter {testCase.RequiredParam}");
                _output.WriteLine($"Proper validation error: {result.Error}");
            }
            else
            {
                _output.WriteLine($"Tool {testCase.Tool} handled missing parameters with defaults");
            }
        }
    }

    [Fact]
    public void McpSdk_ShouldValidateToolServiceDependencies()
    {
        // Arrange
        var toolServiceTypes = GetToolServiceTypes();

        foreach (var serviceType in toolServiceTypes)
        {
            _output.WriteLine($"Validating dependencies for {serviceType.Name}");

            // Act - Get service instance
            var service = ServiceProvider.GetService(serviceType);
            service.Should().NotBeNull($"Should be able to resolve {serviceType.Name}");

            // Assert - Validate constructor dependencies are satisfied
            var constructors = serviceType.GetConstructors();
            constructors.Should().HaveCount(1, $"{serviceType.Name} should have exactly one constructor");

            var constructor = constructors.First();
            var parameters = constructor.GetParameters();

            foreach (var param in parameters)
            {
                var dependency = ServiceProvider.GetService(param.ParameterType);
                dependency.Should().NotBeNull($"Dependency {param.ParameterType.Name} should be resolvable for {serviceType.Name}");
                
                _output.WriteLine($"  Dependency resolved: {param.ParameterType.Name}");
            }
        }
    }

    [Fact]
    public async Task McpSdk_ShouldProvideConsistentToolResults()
    {
        // Arrange
        var tools = _mcpToolService.GetAvailableTools().ToList();
        var toolsToTest = tools.Take(3).ToList(); // Test a subset to avoid long test times

        foreach (var tool in toolsToTest)
        {
            _output.WriteLine($"Testing result consistency for {tool.Name}");

            // Act - Execute tool multiple times with same parameters
            var args = CreateDefaultArgumentsForTool(tool.Name);
            var results = new List<McpToolExecutionResult>();

            for (int i = 0; i < 3; i++)
            {
                var result = await _mcpToolService.ExecuteToolAsync(tool.Name, args);
                results.Add(result);
                
                // Small delay to ensure different timestamps
                await Task.Delay(100);
            }

            // Assert - Results should be consistent in structure
            results.Should().AllSatisfy(result => 
            {
                result.Should().NotBeNull($"Tool {tool.Name} should always return a result");
                result.DurationMs.Should().BeGreaterThan(0, $"Tool {tool.Name} should report execution time");
            });

            // All results should have same success status for identical inputs
            var successStatuses = results.Select(r => r.Success).Distinct().ToList();
            successStatuses.Should().HaveCount(1, $"Tool {tool.Name} should return consistent success status");

            _output.WriteLine($"Tool {tool.Name} consistency validated: {results.Count} executions");
        }
    }

    [Theory]
    [InlineData("pks_init_project")]
    [InlineData("pks_create_task")]
    [InlineData("pks_project_status")]
    public async Task McpSdk_ShouldProvideToolExecutionMetrics(string toolName)
    {
        // Arrange
        var args = CreateDefaultArgumentsForTool(toolName);

        // Act
        var startTime = DateTime.UtcNow;
        var result = await _mcpToolService.ExecuteToolAsync(toolName, args);
        var endTime = DateTime.UtcNow;

        // Assert
        result.Should().NotBeNull($"Tool {toolName} should return result");
        result.DurationMs.Should().BeGreaterThan(0, $"Tool {toolName} should report execution duration");
        
        var totalDuration = (endTime - startTime).TotalMilliseconds;
        result.DurationMs.Should().BeLessOrEqualTo((long)(totalDuration + 100), 
            $"Reported duration should be reasonable for {toolName}");

        _output.WriteLine($"Tool {toolName} execution metrics: {result.DurationMs}ms");
    }

    /// <summary>
    /// Get all tool service types from the current assembly
    /// </summary>
    private IEnumerable<Type> GetToolServiceTypes()
    {
        return Assembly.GetAssembly(typeof(ProjectToolService))!
            .GetTypes()
            .Where(t => t.Name.EndsWith("ToolService") && 
                       t.IsClass && 
                       !t.IsAbstract &&
                       t.Namespace?.Contains("Tools") == true);
    }

    /// <summary>
    /// Get tool methods from a service type
    /// </summary>
    private IEnumerable<MethodInfo> GetToolMethods(Type serviceType)
    {
        return serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(Task<object>) &&
                       m.Name.EndsWith("Async") &&
                       !m.Name.StartsWith("get_") && // Exclude property getters
                       !m.Name.StartsWith("set_")); // Exclude property setters
    }

    /// <summary>
    /// Validate tool method parameters
    /// </summary>
    private void ValidateToolMethodParameters(MethodInfo method)
    {
        var parameters = method.GetParameters();
        parameters.Should().NotBeEmpty($"Tool method {method.Name} should have parameters");

        foreach (var param in parameters)
        {
            // Validate parameter types are supported by MCP
            var supportedTypes = new[] 
            { 
                typeof(string), 
                typeof(bool), 
                typeof(int), 
                typeof(double),
                typeof(string[]),
                typeof(int?),
                typeof(bool?)
            };

            supportedTypes.Should().Contain(param.ParameterType, 
                $"Parameter {param.Name} in {method.Name} has unsupported type {param.ParameterType}");
        }
    }

    /// <summary>
    /// Create default arguments for testing a tool
    /// </summary>
    private Dictionary<string, object> CreateDefaultArgumentsForTool(string toolName)
    {
        return toolName switch
        {
            "pks_init_project" => new Dictionary<string, object>
            {
                ["projectName"] = $"test-{Guid.NewGuid().ToString("N")[..8]}",
                ["template"] = "console",
                ["description"] = "Test project for validation"
            },
            "pks_create_task" => new Dictionary<string, object>
            {
                ["taskDescription"] = "Test task for validation",
                ["agentType"] = "deployment",
                ["priority"] = "medium"
            },
            "pks_project_status" => new Dictionary<string, object>
            {
                ["detailed"] = false
            },
            _ => new Dictionary<string, object>()
        };
    }
}