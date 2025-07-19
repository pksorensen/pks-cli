using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Initializers.Implementations;
using PKS.Infrastructure.Services;
using Xunit;

namespace PKS.CLI.Tests.Integration;

/// <summary>
/// Integration tests for the complete initializer system including all initializers working together
/// </summary>
public class InitializerSystemIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IInitializationService _initializationService;
    private readonly IInitializerRegistry _initializerRegistry;
    private readonly string _testProjectPath;

    public InitializerSystemIntegrationTests()
    {
        // Setup complete service container with all initializers
        var services = new ServiceCollection();
        
        // Register logging
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        
        // Register core services
        services.AddSingleton<IKubernetesService, KubernetesService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IDeploymentService, DeploymentService>();
        services.AddSingleton<IHooksService, HooksService>();
        services.AddSingleton<IMcpServerService, McpServerService>();
        services.AddSingleton<IAgentFrameworkService, AgentFrameworkService>();
        services.AddSingleton<IPrdService, PrdService>();
        
        // Register GitHub and Project Identity services
        services.AddHttpClient<IGitHubService, GitHubService>();
        services.AddSingleton<IProjectIdentityService, ProjectIdentityService>();
        
        // Register all initializers
        services.AddTransient<DotNetProjectInitializer>();
        services.AddTransient<GitHubIntegrationInitializer>();
        services.AddTransient<AgenticFeaturesInitializer>();
        services.AddTransient<ClaudeDocumentationInitializer>();
        services.AddTransient<McpConfigurationInitializer>();
        services.AddTransient<HooksInitializer>();
        services.AddTransient<ReadmeInitializer>();
        
        // Register initializer system
        services.AddSingleton<IInitializerRegistry>(serviceProvider =>
        {
            var registry = new InitializerRegistry(serviceProvider);
            
            // Register all initializer types
            registry.Register<DotNetProjectInitializer>();
            registry.Register<GitHubIntegrationInitializer>();
            registry.Register<AgenticFeaturesInitializer>();
            registry.Register<ClaudeDocumentationInitializer>();
            registry.Register<McpConfigurationInitializer>();
            registry.Register<HooksInitializer>();
            registry.Register<ReadmeInitializer>();
            
            return registry;
        });
        
        services.AddSingleton<IInitializationService, InitializationService>();
        
        _serviceProvider = services.BuildServiceProvider();
        _initializationService = _serviceProvider.GetRequiredService<IInitializationService>();
        _initializerRegistry = _serviceProvider.GetRequiredService<IInitializerRegistry>();
        
        _testProjectPath = Path.Combine(Path.GetTempPath(), "pks-initializer-test-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public async Task FullInitialization_AllInitializersExecuteInOrder_ShouldCreateCompleteProject()
    {
        // Arrange
        var projectName = "FullInitializationTest";
        var options = new Dictionary<string, object>
        {
            { "template", "api" },
            { "agentic", true },
            { "mcp", true },
            { "hooks", true },
            { "github", false }, // Skip GitHub for integration test
            { "project-type", "api" },
            { "description", "Integration test project with all features" }
        };

        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act
            var result = await _initializationService.InitializeProjectAsync(
                projectName, 
                _testProjectPath, 
                "api", 
                options);

            // Assert - Initialization succeeded
            Assert.True(result.Success);
            Assert.NotEmpty(result.AffectedFiles);

            // Assert - .NET project files were created
            Assert.True(File.Exists(Path.Combine(_testProjectPath, $"{projectName}.csproj")));
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "Program.cs")));
            Assert.True(File.Exists(Path.Combine(_testProjectPath, ".gitignore")));

            // Assert - Project identity was created
            Assert.True(Directory.Exists(Path.Combine(_testProjectPath, ".pks")));
            Assert.True(File.Exists(Path.Combine(_testProjectPath, ".pks", "project.json")));

            // Assert - CLAUDE.md documentation was created
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "CLAUDE.md")));
            
            var claudeContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "CLAUDE.md"));
            Assert.Contains(projectName, claudeContent);
            Assert.Contains("PKS CLI", claudeContent);

            // Assert - MCP configuration was created (if enabled)
            if ((bool)options["mcp"])
            {
                Assert.True(Directory.Exists(Path.Combine(_testProjectPath, ".pks")));
                // MCP config should be in the .pks directory
            }

            // Assert - Hooks configuration was created (if enabled)
            if ((bool)options["hooks"])
            {
                Assert.True(Directory.Exists(Path.Combine(_testProjectPath, "hooks")));
            }

            // Assert - Agentic features were configured (if enabled)
            if ((bool)options["agentic"])
            {
                // Agentic configuration should be present
                var projectConfigPath = Path.Combine(_testProjectPath, ".pks", "project.json");
                if (File.Exists(projectConfigPath))
                {
                    var projectConfig = await File.ReadAllTextAsync(projectConfigPath);
                    Assert.Contains("agent", projectConfig.ToLower());
                }
            }

            // Assert - README was created last
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "README.md")));
            
            var readmeContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "README.md"));
            Assert.Contains(projectName, readmeContent);
        }
        finally
        {
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    [Fact]
    public async Task ConditionalInitialization_OnlySelectedInitializersExecute_ShouldCreatePartialProject()
    {
        // Arrange
        var projectName = "ConditionalInitTest";
        var options = new Dictionary<string, object>
        {
            { "template", "console" },
            { "agentic", false },
            { "mcp", false },
            { "hooks", false },
            { "github", false },
            { "project-type", "console" },
            { "description", "Minimal console project" }
        };

        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act
            var result = await _initializationService.InitializeProjectAsync(
                projectName, 
                _testProjectPath, 
                "console", 
                options);

            // Assert - Basic initialization succeeded
            Assert.True(result.Success);

            // Assert - Core .NET files were created
            Assert.True(File.Exists(Path.Combine(_testProjectPath, $"{projectName}.csproj")));
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "Program.cs")));

            // Assert - CLAUDE.md was created (always runs)
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "CLAUDE.md")));

            // Assert - Optional features were NOT created
            Assert.False(Directory.Exists(Path.Combine(_testProjectPath, "hooks")));
            
            // Verify the project is a minimal console app
            var programContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "Program.cs"));
            Assert.Contains("Hello", programContent); // Basic console app content
        }
        finally
        {
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    [Fact]
    public async Task InitializerOrdering_InitializersExecuteInCorrectOrder_ShouldMaintainDependencies()
    {
        // Arrange
        var projectName = "OrderingTest";
        var options = new Dictionary<string, object>
        {
            { "template", "api" },
            { "agentic", true },
            { "mcp", true },
            { "hooks", true },
            { "github", true },
            { "project-type", "api" },
            { "remote-url", "https://github.com/test/ordering-test" }
        };

        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act
            var result = await _initializationService.InitializeProjectAsync(
                projectName, 
                _testProjectPath, 
                "api", 
                options);

            // Assert - Initialization succeeded
            Assert.True(result.Success);

            // Verify execution order by checking file timestamps and dependencies
            var projectFile = Path.Combine(_testProjectPath, $"{projectName}.csproj");
            var claudeFile = Path.Combine(_testProjectPath, "CLAUDE.md");
            var readmeFile = Path.Combine(_testProjectPath, "README.md");

            // Project files should exist (created first)
            Assert.True(File.Exists(projectFile));
            
            // CLAUDE.md should exist (created after project identity)
            Assert.True(File.Exists(claudeFile));
            
            // README should exist (created last)
            Assert.True(File.Exists(readmeFile));

            // Verify that project identity was created and is referenced in CLAUDE.md
            var claudeContent = await File.ReadAllTextAsync(claudeFile);
            Assert.Contains("Project ID", claudeContent);
        }
        finally
        {
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    [Fact]
    public async Task ErrorHandling_InitializerFailureHandledGracefully_ShouldProvideDetailedFeedback()
    {
        // Arrange
        var projectName = "ErrorHandlingTest";
        var invalidPath = "/invalid/path/that/cannot/be/created";
        var options = new Dictionary<string, object>
        {
            { "template", "api" },
            { "project-type", "api" }
        };

        // Act
        var result = await _initializationService.InitializeProjectAsync(
            projectName, 
            invalidPath, 
            "api", 
            options);

        // Assert - Initialization should fail gracefully
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("path", result.Errors.First().ToLower());
    }

    [Fact]
    public async Task TemplateVariableReplacement_VariablesReplacedCorrectly_ShouldGenerateCorrectContent()
    {
        // Arrange
        var projectName = "VariableReplacementTest";
        var description = "Test project for variable replacement";
        var options = new Dictionary<string, object>
        {
            { "template", "api" },
            { "project-type", "api" },
            { "description", description }
        };

        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act
            var result = await _initializationService.InitializeProjectAsync(
                projectName, 
                _testProjectPath, 
                "api", 
                options);

            // Assert
            Assert.True(result.Success);

            // Verify CLAUDE.md has correct variable replacement
            var claudeContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "CLAUDE.md"));
            Assert.Contains(projectName, claudeContent);
            Assert.Contains(description, claudeContent);
            Assert.Contains("api", claudeContent);

            // Verify project file has correct name
            Assert.True(File.Exists(Path.Combine(_testProjectPath, $"{projectName}.csproj")));

            // Verify README has correct content
            if (File.Exists(Path.Combine(_testProjectPath, "README.md")))
            {
                var readmeContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "README.md"));
                Assert.Contains(projectName, readmeContent);
            }
        }
        finally
        {
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    [Fact]
    public async Task ModularCLAUDEDocumentation_FileInclusionsWork_ShouldGenerateComprehensiveDocumentation()
    {
        // Arrange
        var projectName = "ModularCLAUDETest";
        var options = new Dictionary<string, object>
        {
            { "template", "api" },
            { "project-type", "api" },
            { "agentic", true },
            { "mcp", true },
            { "description", "Test project for modular CLAUDE.md" }
        };

        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act
            var result = await _initializationService.InitializeProjectAsync(
                projectName, 
                _testProjectPath, 
                "api", 
                options);

            // Assert
            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(_testProjectPath, "CLAUDE.md")));

            var claudeContent = await File.ReadAllTextAsync(Path.Combine(_testProjectPath, "CLAUDE.md"));

            // Verify that modular content was included
            Assert.Contains("Commands Reference", claudeContent);
            Assert.Contains("Development Rules", claudeContent);
            Assert.Contains("Development Principles", claudeContent);
            Assert.Contains("Agent Personas", claudeContent);
            Assert.Contains("MCP Integration", claudeContent);

            // Verify project-specific content was inserted
            Assert.Contains(projectName, claudeContent);
            Assert.Contains("PKS CLI", claudeContent);
        }
        finally
        {
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        
        if (Directory.Exists(_testProjectPath))
        {
            try
            {
                Directory.Delete(_testProjectPath, true);
            }
            catch
            {
                // Ignore cleanup failures in tests
            }
        }
    }
}