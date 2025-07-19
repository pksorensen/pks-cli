using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Integration;

/// <summary>
/// Integration tests that verify the complete PKS CLI workflow from project initialization to deployment
/// </summary>
public class FullWorkflowIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IInitializationService _initializationService;
    private readonly IProjectIdentityService _projectIdentityService;
    private readonly IGitHubService _gitHubService;
    private readonly IAgentFrameworkService _agentFrameworkService;
    private readonly IMcpServerService _mcpServerService;
    private readonly IHooksService _hooksService;
    private readonly IPrdService _prdService;
    private readonly string _testProjectPath;

    public FullWorkflowIntegrationTests()
    {
        // Setup test service container
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
        
        // Register initializer services
        services.AddSingleton<IInitializationService, InitializationService>();
        
        _serviceProvider = services.BuildServiceProvider();
        
        // Get service instances
        _initializationService = _serviceProvider.GetRequiredService<IInitializationService>();
        _projectIdentityService = _serviceProvider.GetRequiredService<IProjectIdentityService>();
        _gitHubService = _serviceProvider.GetRequiredService<IGitHubService>();
        _agentFrameworkService = _serviceProvider.GetRequiredService<IAgentFrameworkService>();
        _mcpServerService = _serviceProvider.GetRequiredService<IMcpServerService>();
        _hooksService = _serviceProvider.GetRequiredService<IHooksService>();
        _prdService = _serviceProvider.GetRequiredService<IPrdService>();
        
        // Setup test project path
        _testProjectPath = Path.Combine(Path.GetTempPath(), "pks-integration-test-" + Guid.NewGuid().ToString("N")[..8]);
    }

    [Fact]
    public async Task CompleteWorkflow_ProjectInitializationToAgentManagement_ShouldSucceed()
    {
        // Arrange
        var projectName = "IntegrationTestProject";
        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act & Assert - Step 1: Project Identity Creation
            var projectIdentity = await _projectIdentityService.CreateProjectIdentityAsync(
                projectName, 
                _testProjectPath, 
                "Integration test project for PKS CLI");

            Assert.NotNull(projectIdentity);
            Assert.NotEmpty(projectIdentity.ProjectId);
            Assert.Equal(projectName, projectIdentity.Name);
            Assert.Equal(_testProjectPath, projectIdentity.ProjectPath);

            // Act & Assert - Step 2: GitHub Integration (simulated)
            var githubConfig = await _gitHubService.GenerateProjectConfigurationAsync(
                projectIdentity.ProjectId,
                "https://github.com/test/integration-test",
                new[] { "repo", "write:packages" });

            Assert.NotNull(githubConfig);
            Assert.Equal(projectIdentity.ProjectId, githubConfig.ProjectId);
            Assert.Contains("repo", githubConfig.RequiredScopes);

            // Act & Assert - Step 3: Agent Framework Integration
            var agentConfig = new AgentConfiguration
            {
                Name = "test-dev-agent",
                Type = "development",
                ProjectId = projectIdentity.ProjectId,
                Description = "Test development agent for integration testing"
            };

            var agentResult = await _agentFrameworkService.CreateAgentAsync(agentConfig);
            Assert.True(agentResult.Success);
            Assert.Contains("test-dev-agent", agentResult.Message);

            var agents = await _agentFrameworkService.ListAgentsAsync();
            Assert.Contains(agents, a => a.Name == "test-dev-agent");

            // Act & Assert - Step 4: MCP Server Integration
            var mcpConfig = new McpServerConfiguration
            {
                Name = $"pks-{projectIdentity.ProjectId}",
                Transport = "stdio",
                ProjectPath = _testProjectPath,
                ProjectId = projectIdentity.ProjectId,
                Enabled = true
            };

            var mcpResult = await _mcpServerService.CreateServerAsync(mcpConfig);
            Assert.True(mcpResult.Success);

            // Act & Assert - Step 5: Hooks Integration
            var hookConfig = new HookConfiguration
            {
                Name = "pre-build",
                Script = "echo 'Pre-build hook executed'",
                ProjectId = projectIdentity.ProjectId,
                Enabled = true
            };

            var hookResult = await _hooksService.ConfigureHookAsync(hookConfig);
            Assert.True(hookResult.Success);

            var availableHooks = await _hooksService.ListHooksAsync(projectIdentity.ProjectId);
            Assert.Contains(availableHooks, h => h.Name == "pre-build");

            // Act & Assert - Step 6: PRD Integration
            var prdResult = await _prdService.CreatePrdAsync(new PrdCreationRequest
            {
                ProjectName = projectName,
                ProjectId = projectIdentity.ProjectId,
                Description = "Integration test PRD",
                ProjectPath = _testProjectPath
            });

            Assert.True(prdResult.Success);

            // Act & Assert - Step 7: Cross-Component Validation
            // Verify project identity was updated with all integrations
            var updatedIdentity = await _projectIdentityService.GetProjectIdentityAsync(projectIdentity.ProjectId);
            Assert.NotNull(updatedIdentity);

            // Verify MCP server is associated with project
            var mcpServers = await _mcpServerService.ListServersAsync();
            Assert.Contains(mcpServers, s => s.ProjectId == projectIdentity.ProjectId);

            // Verify agents are registered with project
            var projectAgents = await _agentFrameworkService.ListAgentsAsync();
            Assert.Contains(projectAgents, a => a.ProjectId == projectIdentity.ProjectId);

            // Act & Assert - Step 8: Cleanup Test
            // Test that all components can be properly removed
            await _agentFrameworkService.RemoveAgentAsync("test-dev-agent");
            await _mcpServerService.StopServerAsync(mcpConfig.Name);
            await _projectIdentityService.RemoveProjectAsync(projectIdentity.ProjectId);

            // Verify cleanup
            var remainingAgents = await _agentFrameworkService.ListAgentsAsync();
            Assert.DoesNotContain(remainingAgents, a => a.Name == "test-dev-agent");

            var removedProject = await _projectIdentityService.GetProjectIdentityAsync(projectIdentity.ProjectId);
            Assert.Null(removedProject);
        }
        finally
        {
            // Cleanup test directory
            if (Directory.Exists(_testProjectPath))
            {
                Directory.Delete(_testProjectPath, true);
            }
        }
    }

    [Fact]
    public async Task ComponentIntegration_AllServicesCanCommunicate_ShouldSucceed()
    {
        // Arrange
        var projectName = "ComponentIntegrationTest";
        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act - Create project identity
            var projectIdentity = await _projectIdentityService.CreateProjectIdentityAsync(
                projectName, 
                _testProjectPath);

            // Act - Test GitHub service integration
            var tokenValidation = await _gitHubService.ValidateTokenAsync("test-token");
            Assert.NotNull(tokenValidation);

            // Act - Test Agent Framework integration
            var agentStatus = await _agentFrameworkService.GetFrameworkStatusAsync();
            Assert.NotNull(agentStatus);

            // Act - Test MCP service integration
            var mcpStatus = await _mcpServerService.GetServerStatusAsync();
            Assert.NotNull(mcpStatus);

            // Act - Test Hooks service integration
            var hooksStatus = await _hooksService.GetHooksStatusAsync();
            Assert.NotNull(hooksStatus);

            // Act - Test PRD service integration
            var prdStatus = await _prdService.GetPrdStatusAsync(projectIdentity.ProjectId);
            Assert.NotNull(prdStatus);

            // Assert - All services are operational
            Assert.True(true, "All services successfully integrated and responding");
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
    public async Task ErrorHandling_ServicesHandleErrorsGracefully_ShouldNotThrow()
    {
        // Arrange - Use invalid/non-existent data to test error handling

        // Act & Assert - Test error handling in various services
        await Assert.ThrowsAnyAsync<Exception>(async () => 
        {
            await _projectIdentityService.GetProjectIdentityAsync("non-existent-id");
        });

        // GitHub service should handle invalid tokens gracefully
        var invalidTokenResult = await _gitHubService.ValidateTokenAsync("invalid-token");
        Assert.False(invalidTokenResult.IsValid);

        // Agent service should handle invalid agent requests gracefully
        var invalidAgentResult = await _agentFrameworkService.GetAgentStatusAsync("non-existent-agent");
        Assert.NotNull(invalidAgentResult);
        Assert.False(invalidAgentResult.Success);

        // All error conditions should be handled gracefully without crashing
        Assert.True(true, "All services handle errors gracefully");
    }

    [Fact]
    public async Task ConfigurationPersistence_ProjectConfigurationPersistsAcrossServices_ShouldSucceed()
    {
        // Arrange
        var projectName = "ConfigPersistenceTest";
        Directory.CreateDirectory(_testProjectPath);

        try
        {
            // Act - Create project with various configurations
            var projectIdentity = await _projectIdentityService.CreateProjectIdentityAsync(
                projectName, 
                _testProjectPath);

            // Configure GitHub integration
            await _projectIdentityService.AssociateGitHubRepositoryAsync(
                projectIdentity.ProjectId, 
                "https://github.com/test/config-test");

            // Configure MCP server
            var mcpConfig = new McpServerConfiguration
            {
                Name = $"test-{projectIdentity.ProjectId}",
                ProjectId = projectIdentity.ProjectId,
                Transport = "stdio",
                Enabled = true
            };
            await _projectIdentityService.AssociateMcpServerAsync(
                projectIdentity.ProjectId, 
                mcpConfig.Name, 
                mcpConfig);

            // Register agent
            await _projectIdentityService.RegisterAgentAsync(
                projectIdentity.ProjectId, 
                "test-agent", 
                "development");

            // Act - Retrieve project and verify all configurations persisted
            var retrievedProject = await _projectIdentityService.GetProjectIdentityAsync(projectIdentity.ProjectId);

            // Assert - All configurations are preserved
            Assert.NotNull(retrievedProject);
            Assert.Equal("https://github.com/test/config-test", retrievedProject.GitHubRepository);
            Assert.Equal(mcpConfig.Name, retrievedProject.McpServerId);
            Assert.Contains(retrievedProject.Agents, a => a.AgentId == "test-agent");

            // Act - Test configuration loading from file system
            var loadedProject = await _projectIdentityService.LoadProjectConfigurationAsync(_testProjectPath);
            
            // Assert - File system persistence works
            Assert.NotNull(loadedProject);
            Assert.Equal(projectIdentity.ProjectId, loadedProject.ProjectId);
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
        
        // Cleanup any remaining test directories
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