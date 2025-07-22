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

public interface IInitializationService
{
    Task<InitializationResult> InitializeAsync(InitializationOptions options);
}

public interface IInitializerRegistry
{
    Task<List<IInitializer>> GetInitializersAsync();
    Task<IInitializer?> GetInitializerAsync(string id);
}

public interface IInitializer
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    int Order { get; }
    Task<bool> ShouldRunAsync(InitializationContext context);
    Task<InitializationResult> ExecuteAsync(InitializationContext context);
    IEnumerable<InitializerOption> GetOptions();
}

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

public class InitializationResult
{
    public bool Success { get; set; } = true;
    public string? Message { get; set; }
    public string? Details { get; set; }
    public List<string> AffectedFiles { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
    public List<string> Errors { get; init; } = new();
    public Dictionary<string, object?> Data { get; init; } = new();

    public static InitializationResult CreateSuccess(string? message = null, string? details = null)
    {
        return new InitializationResult
        {
            Success = true,
            Message = message,
            Details = details
        };
    }

    public static InitializationResult CreateFailure(string message, string? details = null)
    {
        return new InitializationResult
        {
            Success = false,
            Message = message,
            Details = details
        };
    }

    public static InitializationResult CreateSuccessWithWarnings(string? message = null, params string[] warnings)
    {
        return new InitializationResult
        {
            Success = true,
            Message = message,
            Warnings = warnings.ToList()
        };
    }
}

public class InitializationContext
{
    public required string ProjectName { get; init; }
    public string? Description { get; init; }
    public required string Template { get; init; }
    public bool Force { get; init; }
    public required string TargetDirectory { get; init; }
    public required string WorkingDirectory { get; init; }
    public Dictionary<string, object?> Options { get; init; } = new();
    public bool Interactive { get; init; } = true;
    public Dictionary<string, object?> Metadata { get; init; } = new();

    public T? GetOption<T>(string key, T? defaultValue = default)
    {
        if (Options.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    public void SetOption(string key, object? value)
    {
        Options[key] = value;
    }

    public T? GetMetadata<T>(string key, T? defaultValue = default)
    {
        if (Metadata.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    public void SetMetadata(string key, object? value)
    {
        Metadata[key] = value;
    }
}

public class InitializerOption
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object DefaultValue { get; set; } = false;
}

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