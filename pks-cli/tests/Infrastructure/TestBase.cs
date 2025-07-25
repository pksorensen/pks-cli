using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.CLI.Tests.Infrastructure.Mocks;
using Spectre.Console;
using Spectre.Console.Testing;
using System.Text;
using System.Diagnostics;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Base class for all test classes providing common utilities and setup
/// </summary>
public abstract class TestBase : IDisposable
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly TestConsole TestConsole;
    protected readonly StringBuilder LogOutput;
    protected readonly Mock<ILogger> MockLogger;

    protected TestBase()
    {
        // Setup test console for Spectre.Console testing
        TestConsole = new TestConsole();

        // Setup logging capture
        LogOutput = new StringBuilder();
        MockLogger = new Mock<ILogger>();

        // Setup mock logger to capture log messages
        MockLogger.Setup(x => x.Log(
            It.IsAny<LogLevel>(),
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception, Delegate>((level, eventId, state, exception, formatter) =>
            {
                LogOutput.AppendLine($"[{level}] {formatter.DynamicInvoke(state, exception)}");
            });

        // Create service collection for dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Override this method to configure services for testing
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add common test services
        services.AddSingleton<IAnsiConsole>(TestConsole);
        services.AddSingleton(MockLogger.Object);
        
        // Add logger factory for generic logger creation
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // Add HTTP client for services that need it
        services.AddHttpClient();

        // Register mock services from ServiceMockFactory
        RegisterMockServices(services);
    }

    /// <summary>
    /// Registers all standard mock services for testing
    /// </summary>
    protected virtual void RegisterMockServices(IServiceCollection services)
    {
        // Core infrastructure services
        services.AddSingleton(ServiceMockFactory.CreateKubernetesService().Object);
        services.AddSingleton(ServiceMockFactory.CreateConfigurationService().Object);
        services.AddSingleton(ServiceMockFactory.CreateDeploymentService().Object);
        
        // For real services, register concrete implementations that work with actual interfaces
        services.AddSingleton<PKS.Infrastructure.Initializers.Service.IInitializationService, PKS.Infrastructure.Initializers.Service.InitializationService>();

        // Agent framework services
        var agentFrameworkService = ServiceMockFactory.CreateAgentFrameworkService();
        services.AddSingleton<PKS.CLI.Infrastructure.Services.IAgentFrameworkService>(agentFrameworkService.Object);

        // Hooks services
        var hooksService = ServiceMockFactory.CreateHooksService();
        services.AddSingleton<PKS.Infrastructure.Services.IHooksService>(hooksService.Object);

        // MCP services with proper interface and concrete registration
        var mcpHostingService = ServiceMockFactory.CreateMcpHostingService();
        services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.IMcpHostingService>(mcpHostingService.Object);
        
        // Add concrete MCP services that require loggers
        services.AddSingleton<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpResourceService>>(
            Mock.Of<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpResourceService>>());
        services.AddSingleton<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpToolService>>(
            Mock.Of<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpToolService>>());
        services.AddSingleton<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpHostingService>>(
            Mock.Of<ILogger<PKS.CLI.Infrastructure.Services.MCP.McpHostingService>>());
            
        // Register concrete MCP services for direct use in integration tests
        services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.McpResourceService>();
        services.AddSingleton<PKS.CLI.Infrastructure.Services.MCP.McpToolService>();

        // Devcontainer services with proper interface registration
        var devcontainerService = ServiceMockFactory.CreateDevcontainerService();
        services.AddSingleton<PKS.Infrastructure.Services.IDevcontainerService>(devcontainerService.Object);
        
        var featureRegistry = ServiceMockFactory.CreateDevcontainerFeatureRegistry();
        services.AddSingleton<PKS.Infrastructure.Services.IDevcontainerFeatureRegistry>(featureRegistry.Object);
        
        var templateService = ServiceMockFactory.CreateDevcontainerTemplateService();
        services.AddSingleton<PKS.Infrastructure.Services.IDevcontainerTemplateService>(templateService.Object);
        
        var fileGenerator = ServiceMockFactory.CreateDevcontainerFileGenerator();
        services.AddSingleton<PKS.Infrastructure.Services.IDevcontainerFileGenerator>(fileGenerator.Object);
        
        var extensionService = ServiceMockFactory.CreateVsCodeExtensionService();
        services.AddSingleton<PKS.Infrastructure.Services.IVsCodeExtensionService>(extensionService.Object);

        // NuGet template discovery service
        var nugetService = ServiceMockFactory.CreateNuGetTemplateDiscoveryService();
        services.AddSingleton<PKS.Infrastructure.Services.INuGetTemplateDiscoveryService>(nugetService.Object);

        // Template packaging service
        var templatePackagingService = ServiceMockFactory.CreateTemplatePackagingService();
        services.AddSingleton<PKS.Infrastructure.Services.ITemplatePackagingService>(templatePackagingService.Object);

        // Register initializers as transient services (matching main Program.cs)
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DotNetProjectInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.DevcontainerInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.GitHubIntegrationInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.AgenticFeaturesInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ReadmeInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.ClaudeDocumentationInitializer>();
        services.AddTransient<PKS.Infrastructure.Initializers.Implementations.McpConfigurationInitializer>();
        
        services.AddSingleton<PKS.Infrastructure.Initializers.Registry.IInitializerRegistry, PKS.Infrastructure.Initializers.Registry.InitializerRegistry>();

        // Keep test interface registrations separate
        RegisterTestInterfaceImplementations(services);
    }

    /// <summary>
    /// Registers implementations for interfaces defined in ServiceInterfaces.cs
    /// </summary>
    protected virtual void RegisterTestInterfaceImplementations(IServiceCollection services)
    {
        // These interfaces are defined in ServiceInterfaces.cs for testing
        services.AddSingleton<PKS.CLI.Tests.Infrastructure.Mocks.IKubernetesService>(
            ServiceMockFactory.CreateKubernetesService().Object);
        services.AddSingleton<PKS.CLI.Tests.Infrastructure.Mocks.IConfigurationService>(
            ServiceMockFactory.CreateConfigurationService().Object);
        services.AddSingleton<PKS.CLI.Tests.Infrastructure.Mocks.IDeploymentService>(
            ServiceMockFactory.CreateDeploymentService().Object);
        // These are now using real implementations, not mock interfaces
        // services.AddSingleton<PKS.CLI.Tests.Infrastructure.Mocks.IInitializationService>(
        //     _ => ServiceMockFactory.CreateInitializationService().Object);
        // services.AddSingleton<PKS.CLI.Tests.Infrastructure.Mocks.IInitializerRegistry>(
        //     _ => ServiceMockFactory.CreateInitializerRegistry().Object);
    }

    /// <summary>
    /// Gets a service from the test service provider
    /// </summary>
    protected T GetService<T>() where T : class
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Creates a mock of the specified type
    /// </summary>
    protected Mock<T> CreateMock<T>() where T : class
    {
        return new Mock<T>();
    }

    /// <summary>
    /// Asserts that the console output contains the expected text
    /// </summary>
    protected void AssertConsoleOutput(string expectedText)
    {
        // Get both raw output and cleaned output for more flexible assertions
        var rawOutput = TestConsole.Output;
        var cleanedOutput = StripAnsiEscapeCodes(rawOutput);
        
        // Debug output for troubleshooting
        System.Diagnostics.Debug.WriteLine($"Raw output length: {rawOutput.Length}");
        System.Diagnostics.Debug.WriteLine($"Raw output: '{rawOutput}'");
        System.Diagnostics.Debug.WriteLine($"Cleaned output: '{cleanedOutput}'");
        System.Diagnostics.Debug.WriteLine($"Looking for: '{expectedText}'");
        
        // Check both cleaned and raw output
        if (!cleanedOutput.Contains(expectedText) && !rawOutput.Contains(expectedText))
        {
            rawOutput.Should().Contain(expectedText, 
                $"Expected text '{expectedText}' not found in console output. Raw output: '{rawOutput}', Cleaned output: '{cleanedOutput}'");
        }
    }

    /// <summary>
    /// Strips ANSI escape codes from text to get clean content
    /// </summary>
    private string StripAnsiEscapeCodes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
            
        // Remove ANSI escape sequences (ESC[ followed by any number of parameter bytes, then a final byte)
        return System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", "");
    }

    /// <summary>
    /// Asserts that a log message with the specified level was written
    /// </summary>
    protected void AssertLogMessage(LogLevel level, string expectedMessage)
    {
        LogOutput.ToString().Should().Contain($"[{level}]").And.Contain(expectedMessage);
    }

    /// <summary>
    /// Clears the test console output
    /// </summary>
    protected void ClearConsoleOutput()
    {
        TestConsole.Clear();
    }

    /// <summary>
    /// Creates a temporary directory for test files
    /// </summary>
    protected string CreateTempDirectory()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);
        return tempPath;
    }

    /// <summary>
    /// Creates a temporary file with the specified content
    /// </summary>
    protected string CreateTempFile(string content = "", string extension = ".txt")
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"pks-cli-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(tempFile, content);
        return tempFile;
    }

    /// <summary>
    /// Ensures all background tasks are properly disposed
    /// </summary>
    protected void EnsureNoBackgroundTasks()
    {
        // Wait for any pending tasks to complete
        Task.Delay(100).Wait();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// Kills any processes that might have been started during testing
    /// </summary>
    protected void KillTestProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("dotnet")
                .Where(p => p.ProcessName.Contains("pks") || 
                           p.StartInfo.Arguments?.Contains("pks") == true)
                .ToArray();

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                    }
                }
                catch
                {
                    // Ignore process cleanup errors
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Ignore process enumeration errors
        }
    }

    public virtual void Dispose()
    {
        try
        {
            EnsureNoBackgroundTasks();
            KillTestProcesses();
        }
        finally
        {
            if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}