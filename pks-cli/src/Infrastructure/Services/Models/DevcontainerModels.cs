using System.ComponentModel;
using System.Text.Json.Serialization;

namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Main devcontainer configuration model
/// </summary>
public class DevcontainerConfiguration
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public string Image { get; set; } = string.Empty;

    [JsonPropertyName("features")]
    public Dictionary<string, object> Features { get; set; } = new();

    [JsonPropertyName("customizations")]
    public Dictionary<string, object> Customizations { get; set; } = new();

    [JsonPropertyName("forwardPorts")]
    public int[] ForwardPorts { get; set; } = Array.Empty<int>();

    [JsonPropertyName("postCreateCommand")]
    public string PostCreateCommand { get; set; } = string.Empty;

    [JsonPropertyName("mounts")]
    public string[] Mounts { get; set; } = Array.Empty<string>();

    [JsonPropertyName("remoteEnv")]
    public Dictionary<string, string> RemoteEnv { get; set; } = new();

    [JsonPropertyName("workspaceFolder")]
    public string? WorkspaceFolder { get; set; }

    [JsonPropertyName("runArgs")]
    public string[]? RunArgs { get; set; }

    [JsonPropertyName("containerEnv")]
    public Dictionary<string, string>? ContainerEnv { get; set; }

    [JsonPropertyName("build")]
    public DevcontainerBuildConfig? Build { get; set; }

    [JsonPropertyName("dockerComposeFile")]
    public string? DockerComposeFile { get; set; }

    [JsonPropertyName("service")]
    public string? Service { get; set; }

    [JsonPropertyName("runServices")]
    public string[]? RunServices { get; set; }
}

/// <summary>
/// Build configuration for devcontainer
/// </summary>
public class DevcontainerBuildConfig
{
    [JsonPropertyName("dockerfile")]
    public string? Dockerfile { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("args")]
    public Dictionary<string, string>? Args { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

/// <summary>
/// Devcontainer feature definition
/// </summary>
public class DevcontainerFeature
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string Documentation { get; set; } = string.Empty;
    public Dictionary<string, object> DefaultOptions { get; set; } = new();
    public Dictionary<string, DevcontainerFeatureOption> AvailableOptions { get; set; } = new();
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public string[] ConflictsWith { get; set; } = Array.Empty<string>();
    public bool IsDeprecated { get; set; }
    public string? DeprecationMessage { get; set; }
}

/// <summary>
/// Feature option definition
/// </summary>
public class DevcontainerFeatureOption
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? Default { get; set; }
    public string[]? Enum { get; set; }
    public bool Required { get; set; }
    public string? Pattern { get; set; }
    public object? Minimum { get; set; }
    public object? Maximum { get; set; }
}

/// <summary>
/// Devcontainer template definition
/// </summary>
public class DevcontainerTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string BaseImage { get; set; } = string.Empty;
    public string[] RequiredFeatures { get; set; } = Array.Empty<string>();
    public string[] OptionalFeatures { get; set; } = Array.Empty<string>();
    public Dictionary<string, object> DefaultCustomizations { get; set; } = new();
    public string[] DefaultPorts { get; set; } = Array.Empty<string>();
    public string? DefaultPostCreateCommand { get; set; }
    public Dictionary<string, string> DefaultEnvVars { get; set; } = new();
    public bool RequiresDockerCompose { get; set; }
    public string? DockerComposeTemplate { get; set; }
}

/// <summary>
/// VS Code extension information
/// </summary>
public class VsCodeExtension
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string? Version { get; set; }
    public bool IsEssential { get; set; }
    public string[] RequiredFeatures { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Options for creating devcontainer configuration
/// </summary>
public class DevcontainerOptions
{
    public string Name { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string? Template { get; set; }
    public List<string> Features { get; set; } = new();
    public List<string> Extensions { get; set; } = new();
    public bool UseDockerCompose { get; set; }
    public bool Interactive { get; set; }
    public Dictionary<string, object> CustomSettings { get; set; } = new();
    public string? BaseImage { get; set; }
    public List<int> ForwardPorts { get; set; } = new();
    public string? PostCreateCommand { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool IncludeDevPackages { get; set; } = true;
    public bool EnableGitCredentials { get; set; } = true;
    public string? WorkspaceFolder { get; set; }
}

/// <summary>
/// Result of devcontainer creation operation
/// </summary>
public class DevcontainerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DevcontainerConfiguration? Configuration { get; set; }
    public List<string> GeneratedFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Result of devcontainer validation
/// </summary>
public class DevcontainerValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<string> Suggestions { get; set; } = new();
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.None;
}

/// <summary>
/// Result of feature dependency resolution
/// </summary>
public class FeatureResolutionResult
{
    public bool Success { get; set; }
    public List<DevcontainerFeature> ResolvedFeatures { get; set; } = new();
    public List<FeatureConflict> ConflictingFeatures { get; set; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
    public List<string> ResolvedDependencies { get; set; } = new();
    public List<string> MissingDependencies { get; set; } = new();
}

/// <summary>
/// Feature conflict information
/// </summary>
public class FeatureConflict
{
    public string Feature1 { get; set; } = string.Empty;
    public string Feature2 { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public ConflictSeverity Severity { get; set; } = ConflictSeverity.Error;
    public string? Resolution { get; set; }
}

/// <summary>
/// Result of feature validation
/// </summary>
public class FeatureValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<string, object>? ValidatedOptions { get; set; }
}

/// <summary>
/// Result of file generation
/// </summary>
public class FileGenerationResult
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Result of path validation
/// </summary>
public class PathValidationResult
{
    public bool IsValid { get; set; }
    public bool CanWrite { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? ResolvedPath { get; set; }
    public bool PathExists { get; set; }
    public bool IsDirectory { get; set; }
}

/// <summary>
/// Result of extension validation
/// </summary>
public class ExtensionValidationResult
{
    public bool IsValid { get; set; }
    public bool Exists { get; set; }
    public bool IsCompatible { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? LatestVersion { get; set; }
    public List<string> Dependencies { get; set; } = new();
}

/// <summary>
/// Validation severity levels
/// </summary>
public enum ValidationSeverity
{
    None = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

/// <summary>
/// Feature conflict severity levels
/// </summary>
public enum ConflictSeverity
{
    Warning = 1,
    Error = 2,
    Critical = 3
}

/// <summary>
/// Devcontainer generation mode
/// </summary>
public enum DevcontainerMode
{
    Image,
    Dockerfile,
    DockerCompose
}

/// <summary>
/// Feature installation mode
/// </summary>
public enum FeatureInstallMode
{
    Auto,
    Latest,
    Specific,
    None
}