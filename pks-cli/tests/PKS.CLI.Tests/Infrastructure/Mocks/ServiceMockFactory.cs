using Moq;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Infrastructure.Mocks;

/// <summary>
/// Factory for creating mocks of core PKS CLI services
/// </summary>
public static class ServiceMockFactory
{
    /// <summary>
    /// Creates a mock IKubernetesService with default behavior
    /// </summary>
    public static Mock<IKubernetesService> CreateKubernetesService()
    {
        var mock = new Mock<IKubernetesService>();
        
        // Setup default successful behaviors
        mock.Setup(x => x.ValidateConnectionAsync())
            .ReturnsAsync(true);
            
        mock.Setup(x => x.DeployAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
            .ReturnsAsync(new DeploymentResult { Success = true, Message = "Deployment successful" });
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IConfigurationService with default behavior
    /// </summary>
    public static Mock<IConfigurationService> CreateConfigurationService()
    {
        var mock = new Mock<IConfigurationService>();
        
        mock.Setup(x => x.GetAsync<string>(It.IsAny<string>()))
            .ReturnsAsync((string key) => $"test-{key}");
            
        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IDeploymentService with default behavior
    /// </summary>
    public static Mock<IDeploymentService> CreateDeploymentService()
    {
        var mock = new Mock<IDeploymentService>();
        
        mock.Setup(x => x.ExecuteDeploymentAsync(It.IsAny<DeploymentPlan>()))
            .ReturnsAsync(new DeploymentResult { Success = true, Message = "Deployment completed" });
            
        mock.Setup(x => x.ValidateDeploymentAsync(It.IsAny<DeploymentPlan>()))
            .ReturnsAsync(new ValidationResult { IsValid = true });
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IInitializationService with default behavior
    /// </summary>
    public static Mock<IInitializationService> CreateInitializationService()
    {
        var mock = new Mock<IInitializationService>();
        
        mock.Setup(x => x.InitializeAsync(It.IsAny<InitializationOptions>()))
            .ReturnsAsync(new InitializationResult 
            { 
                Success = true, 
                Message = "Project initialized successfully",
                CreatedFiles = new List<string> { "Program.cs", "README.md" }
            });
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IInitializerRegistry with default behavior
    /// </summary>
    public static Mock<IInitializerRegistry> CreateInitializerRegistry()
    {
        var mock = new Mock<IInitializerRegistry>();
        
        mock.Setup(x => x.GetInitializersAsync())
            .ReturnsAsync(new List<IInitializer>());
            
        mock.Setup(x => x.GetInitializerAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => null);
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IHooksService (to be implemented)
    /// </summary>
    public static Mock<IHooksService> CreateHooksService()
    {
        var mock = new Mock<IHooksService>();
        
        mock.Setup(x => x.GetAvailableHooksAsync())
            .ReturnsAsync(new List<HookDefinition>());
            
        mock.Setup(x => x.ExecuteHookAsync(It.IsAny<string>(), It.IsAny<HookContext>()))
            .ReturnsAsync(new HookResult { Success = true, Message = "Hook executed successfully" });
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IMcpServerService (to be implemented)
    /// </summary>
    public static Mock<IMcpServerService> CreateMcpServerService()
    {
        var mock = new Mock<IMcpServerService>();
        
        mock.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>()))
            .ReturnsAsync(new McpServerResult { Success = true, Port = 8080 });
            
        mock.Setup(x => x.StopServerAsync())
            .ReturnsAsync(true);
            
        mock.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(new McpServerStatus { IsRunning = false, Port = null });
            
        return mock;
    }

    /// <summary>
    /// Creates a mock IAgentFrameworkService (to be implemented)
    /// </summary>
    public static Mock<IAgentFrameworkService> CreateAgentFrameworkService()
    {
        var mock = new Mock<IAgentFrameworkService>();
        
        mock.Setup(x => x.CreateAgentAsync(It.IsAny<AgentConfiguration>()))
            .ReturnsAsync(new AgentResult { Success = true, AgentId = "test-agent-123" });
            
        mock.Setup(x => x.ListAgentsAsync())
            .ReturnsAsync(new List<AgentInfo>());
            
        mock.Setup(x => x.GetAgentStatusAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentStatus { Id = "test-agent", Status = "Active" });
            
        mock.Setup(x => x.StartAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentResult { Success = true, Message = "Agent started" });
            
        mock.Setup(x => x.StopAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentResult { Success = true, Message = "Agent stopped" });
            
        mock.Setup(x => x.RemoveAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
            
        mock.Setup(x => x.LoadConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentConfiguration { Name = "test-agent", Type = "automation" });
            
        return mock;
    }
}

// Placeholder interfaces for services that will be implemented
public interface IHooksService
{
    Task<List<HookDefinition>> GetAvailableHooksAsync();
    Task<HookResult> ExecuteHookAsync(string hookName, HookContext context);
}

public interface IMcpServerService
{
    Task<McpServerResult> StartServerAsync(McpServerConfig config);
    Task<bool> StopServerAsync();
    Task<McpServerStatus> GetServerStatusAsync();
}