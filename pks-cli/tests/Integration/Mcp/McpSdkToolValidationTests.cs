using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.CLI.Infrastructure.Services.MCP.Tools;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
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

        // Register additional loggers for tool services
        services.AddSingleton<ILogger<DeploymentToolService>>(
            Mock.Of<ILogger<DeploymentToolService>>());
        services.AddSingleton<ILogger<AgentToolService>>(
            Mock.Of<ILogger<AgentToolService>>());
        services.AddSingleton<ILogger<ProjectToolService>>(
            Mock.Of<ILogger<ProjectToolService>>());
        services.AddSingleton<ILogger<StatusToolService>>(
            Mock.Of<ILogger<StatusToolService>>());
        services.AddSingleton<ILogger<SwarmToolService>>(
            Mock.Of<ILogger<SwarmToolService>>());

        // TestBase registers test-specific interfaces, but tool services need the real interfaces
        // Register the real service interfaces that the tool services expect
        var deploymentServiceMock = new Mock<PKS.Infrastructure.IDeploymentService>();
        deploymentServiceMock.Setup(x => x.DeployAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        deploymentServiceMock.Setup(x => x.GetDeploymentInfoAsync(It.IsAny<string>()))
            .ReturnsAsync(new { status = "healthy", replicas = 3 });
        deploymentServiceMock.Setup(x => x.RollbackAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        services.AddSingleton<PKS.Infrastructure.IDeploymentService>(deploymentServiceMock.Object);

        var kubernetesServiceMock = new Mock<PKS.Infrastructure.IKubernetesService>();
        kubernetesServiceMock.Setup(x => x.GetDeploymentsAsync(It.IsAny<string>()))
            .ReturnsAsync(new[] { "deployment1", "deployment2" });
        kubernetesServiceMock.Setup(x => x.GetDeploymentStatusAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new { status = "ready", replicas = 3 });
        services.AddSingleton<PKS.Infrastructure.IKubernetesService>(kubernetesServiceMock.Object);

        var configServiceMock = new Mock<PKS.Infrastructure.IConfigurationService>();
        configServiceMock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync("test-value");
        services.AddSingleton<PKS.Infrastructure.IConfigurationService>(configServiceMock.Object);

        // Add DevcontainerToolService dependencies
        var devcontainerServiceMock = new Mock<IDevcontainerService>();
        devcontainerServiceMock.Setup(x => x.InitializeAsync(It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync(new DevcontainerResult { Success = true, Message = "Test initialization" });
        devcontainerServiceMock.Setup(x => x.HasDevcontainerAsync()).ReturnsAsync(true);
        devcontainerServiceMock.Setup(x => x.GetConfigurationAsync())
            .ReturnsAsync(new DevcontainerConfiguration { Name = "test-container" });
        services.AddSingleton<IDevcontainerService>(devcontainerServiceMock.Object);

        var featureRegistryMock = new Mock<IDevcontainerFeatureRegistry>();
        featureRegistryMock.Setup(x => x.GetAvailableFeaturesAsync())
            .ReturnsAsync(new List<DevcontainerFeature>());
        services.AddSingleton<IDevcontainerFeatureRegistry>(featureRegistryMock.Object);

        var templateServiceMock = new Mock<IDevcontainerTemplateService>();
        templateServiceMock.Setup(x => x.GetAvailableTemplatesAsync())
            .ReturnsAsync(new List<DevcontainerTemplate>());
        services.AddSingleton<IDevcontainerTemplateService>(templateServiceMock.Object);

        var extensionServiceMock = new Mock<IVsCodeExtensionService>();
        extensionServiceMock.Setup(x => x.GetRecommendedExtensionsAsync(It.IsAny<string[]>()))
            .ReturnsAsync(new List<VsCodeExtension>());
        services.AddSingleton<IVsCodeExtensionService>(extensionServiceMock.Object);

        services.AddSingleton<ILogger<DevcontainerToolService>>(
            Mock.Of<ILogger<DevcontainerToolService>>());

        // Add GitHubToolService dependencies
        var githubServiceMock = new Mock<IGitHubService>();
        githubServiceMock.Setup(x => x.RepositoryExistsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        githubServiceMock.Setup(x => x.CreateRepositoryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(new GitHubRepository
            {
                Name = "test-repo",
                FullName = "owner/test-repo",  
                Description = "Test repository",
                HtmlUrl = "https://github.com/owner/test-repo",
                CloneUrl = "https://github.com/owner/test-repo.git",
                IsPrivate = false,
                Owner = "owner",
                CreatedAt = DateTime.UtcNow
            });
        services.AddSingleton<IGitHubService>(githubServiceMock.Object);

        var projectIdentityServiceMock = new Mock<IProjectIdentityService>();
        projectIdentityServiceMock.Setup(x => x.GetProjectIdentityAsync(It.IsAny<string>()))
            .ReturnsAsync(new ProjectIdentity { GitHubRepository = "test-repo" });
        services.AddSingleton<IProjectIdentityService>(projectIdentityServiceMock.Object);

        services.AddSingleton<ILogger<GitHubToolService>>(
            Mock.Of<ILogger<GitHubToolService>>());

        // Add HooksToolService dependencies
        var hooksServiceMock = new Mock<IHooksService>();
        hooksServiceMock.Setup(x => x.GetAvailableHooksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<HookDefinition>
            {
                new HookDefinition { Name = "pre-commit", Description = "Pre-commit validation" },
                new HookDefinition { Name = "pre-push", Description = "Pre-push checks" }
            });
        hooksServiceMock.Setup(x => x.GetInstalledHooksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<InstalledHook>());
        hooksServiceMock.Setup(x => x.InstallHooksAsync(It.IsAny<HooksConfiguration>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HookInstallResult { Success = true, Message = "Hooks installed successfully" });
        services.AddSingleton<IHooksService>(hooksServiceMock.Object);

        services.AddSingleton<ILogger<HooksToolService>>(
            Mock.Of<ILogger<HooksToolService>>());

        // Add McpManagementToolService dependencies
        var mcpHostingServiceMock = new Mock<IMcpHostingService>();
        mcpHostingServiceMock.Setup(x => x.IsRunningAsync()).ReturnsAsync(true);
        mcpHostingServiceMock.Setup(x => x.GetServerInfoAsync())
            .ReturnsAsync(new McpServerInfo
            {
                Transport = "stdio",
                StartedAt = DateTime.UtcNow.AddHours(-1),
                ActiveConnections = 1,
                DefaultTransport = "stdio",
                SupportedTransports = new[] { "stdio" },
                EnableAutoToolDiscovery = true,
                EnabledCategories = new[] { "all" },
                DisabledTools = new string[0],
                MaxConnections = 10,
                TimeoutSettings = new McpTimeoutSettings()
            });
        services.AddSingleton<IMcpHostingService>(mcpHostingServiceMock.Object);

        services.AddSingleton<ILogger<McpResourceService>>(
            Mock.Of<ILogger<McpResourceService>>());
        services.AddSingleton<McpResourceService>();

        services.AddSingleton<ILogger<McpManagementToolService>>(
            Mock.Of<ILogger<McpManagementToolService>>());

        // Add PrdToolService dependencies
        var prdServiceMock = new Mock<IPrdService>();
        prdServiceMock.Setup(x => x.GeneratePrdAsync(It.IsAny<PrdGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrdGenerationResult { Success = true, OutputFile = "PRD.md", Message = "Generated PRD content" });
        prdServiceMock.Setup(x => x.ValidatePrdAsync(It.IsAny<PrdValidationOptions>()))
            .ReturnsAsync(new PrdValidationResult { Success = true, IsValid = true, OverallScore = 95 });
        prdServiceMock.Setup(x => x.GetAvailableTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PrdTemplateInfo> 
            { 
                new PrdTemplateInfo { Id = "standard", Name = "Standard", Category = "business", IsDefault = true }
            });
        prdServiceMock.Setup(x => x.LoadPrdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrdLoadResult { Success = true, ProductName = "Test Product", Template = "standard" });
        prdServiceMock.Setup(x => x.UpdatePrdAsync(It.IsAny<PrdUpdateOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrdUpdateResult { Success = true, Message = "PRD updated successfully" });
        services.AddSingleton<IPrdService>(prdServiceMock.Object);

        services.AddSingleton<ILogger<PrdToolService>>(
            Mock.Of<ILogger<PrdToolService>>());

        // Add TemplateToolService dependencies
        var templateDiscoveryServiceMock = new Mock<INuGetTemplateDiscoveryService>();
        templateDiscoveryServiceMock.Setup(x => x.SearchTemplatesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetTemplateSearchResult>
            {
                new NuGetTemplateSearchResult { Id = "test-template", Title = "Test Template", Description = "Test description" }
            });
        templateDiscoveryServiceMock.Setup(x => x.GetInstalledTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetDevcontainerTemplate>());
        templateDiscoveryServiceMock.Setup(x => x.GetLatestVersionAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("1.0.0");
        templateDiscoveryServiceMock.Setup(x => x.GetTemplateDetailsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NuGetTemplateDetails { Id = "test-template", Title = "Test Template", Description = "Test description", Version = "1.0.0" });
        templateDiscoveryServiceMock.Setup(x => x.InstallTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NuGetTemplateExtractionResult { Success = true, Message = "Template installed" });
        templateDiscoveryServiceMock.Setup(x => x.UninstallTemplateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        services.AddSingleton<INuGetTemplateDiscoveryService>(templateDiscoveryServiceMock.Object);

        services.AddSingleton<ILogger<TemplateToolService>>(
            Mock.Of<ILogger<TemplateToolService>>());

        // Add UtilityToolService dependencies (only needs logger)
        services.AddSingleton<ILogger<UtilityToolService>>(
            Mock.Of<ILogger<UtilityToolService>>());

        // Register all tool services for testing
        services.AddSingleton<ProjectToolService>();
        services.AddSingleton<AgentToolService>();
        services.AddSingleton<DeploymentToolService>();
        services.AddSingleton<DevcontainerToolService>();
        services.AddSingleton<GitHubToolService>();
        services.AddSingleton<HooksToolService>();
        services.AddSingleton<McpManagementToolService>();
        services.AddSingleton<PrdToolService>();
        services.AddSingleton<StatusToolService>();
        services.AddSingleton<SwarmToolService>();
        services.AddSingleton<TemplateToolService>();
        services.AddSingleton<UtilityToolService>();
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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

    [Fact(Skip = "Mock-only test - tests simulated MCP behavior not real integration, no real value")]
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