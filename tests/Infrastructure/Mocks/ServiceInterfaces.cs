// This file contains interface definitions for services that will be implemented
// These interfaces are referenced in the main source code but defined here for testing

namespace PKS.CLI.Tests.Infrastructure.Mocks;

// Core service interfaces that exist in main codebase
public interface IKubernetesService
{
    Task<bool> ValidateConnectionAsync();
    Task<DeploymentResult> DeployAsync(string environment, Dictionary<string, object> configuration);
}

public interface IConfigurationService
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync(string key, object value);
}

public interface IDeploymentService
{
    Task<DeploymentResult> ExecuteDeploymentAsync(DeploymentPlan plan);
    Task<ValidationResult> ValidateDeploymentAsync(DeploymentPlan plan);
}

// Initialization interfaces are now using the real implementations from the main codebase
// The real interfaces are at:
// - PKS.Infrastructure.Initializers.Service.IInitializationService
// - PKS.Infrastructure.Initializers.Registry.IInitializerRegistry  
// - PKS.Infrastructure.Initializers.IInitializer

// Result and model classes
public class DeploymentResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

// Additional types needed for testing
public class InitializationOptions
{
    public string ProjectName { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Agentic { get; set; }
    public bool Mcp { get; set; }
    public bool Force { get; set; }
    public string TargetDirectory { get; set; } = string.Empty;
}

// InitializationResult, InitializationContext, and InitializerOption are now provided by:
// - PKS.Infrastructure.Initializers.Context.InitializationResult
// - PKS.Infrastructure.Initializers.Context.InitializationContext  
// - PKS.Infrastructure.Initializers.Context.InitializerOption

public class DeploymentPlan
{
    public string Environment { get; set; } = string.Empty;
    public List<string> Resources { get; set; } = new();
    public Dictionary<string, object> Configuration { get; set; } = new();
}

public class HookDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
}

public class HookContext
{
    public Dictionary<string, object> Parameters { get; set; } = new();
    public string WorkingDirectory { get; set; } = string.Empty;
}

public class HookResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object> Output { get; set; } = new();
}

// MCP models are now provided by the SDK in:
// - PKS.CLI.Infrastructure.Services.MCP.McpServerConfig
// - PKS.CLI.Infrastructure.Services.MCP.McpServerResult
// - PKS.CLI.Infrastructure.Services.MCP.McpServerStatusInfo

// Agent classes are now implemented in the main codebase:
// - PKS.CLI.Infrastructure.Services.Models.AgentConfiguration
// - PKS.CLI.Infrastructure.Services.Models.AgentResult
// - PKS.CLI.Infrastructure.Services.Models.AgentInfo
// - PKS.CLI.Infrastructure.Services.Models.AgentStatus