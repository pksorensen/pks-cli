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

    /// <summary>
    /// Base image for the devcontainer (alias for Image property)
    /// </summary>
    [JsonIgnore]
    public string BaseImage
    {
        get => Image;
        set => Image = value;
    }

    /// <summary>
    /// List of VS Code extensions to install
    /// </summary>
    [JsonIgnore]
    public List<string> Extensions { get; set; } = new();

    [JsonPropertyName("customizations")]
    public Dictionary<string, object> Customizations { get; set; } = new();

    [JsonPropertyName("forwardPorts")]
    public int[] ForwardPorts { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Forward ports as List for easier manipulation (alias for ForwardPorts array)
    /// </summary>
    [JsonIgnore]
    public List<int> ForwardPortsList
    {
        get => ForwardPorts?.ToList() ?? new List<int>();
        set => ForwardPorts = value?.ToArray() ?? Array.Empty<int>();
    }

    [JsonPropertyName("postCreateCommand")]
    public string PostCreateCommand { get; set; } = string.Empty;

    [JsonPropertyName("mounts")]
    public string[] Mounts { get; set; } = Array.Empty<string>();

    [JsonPropertyName("remoteEnv")]
    public Dictionary<string, string> RemoteEnv { get; set; } = new();

    [JsonPropertyName("workspaceFolder")]
    public string? WorkspaceFolder { get; set; }

    [JsonPropertyName("workspaceMount")]
    public string? WorkspaceMount { get; set; }

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

    [JsonPropertyName("volumes")]
    public string[]? Volumes { get; set; }

    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

/// <summary>
/// Build configuration for devcontainer
/// </summary>
public class DevcontainerBuildConfig
{
    [JsonPropertyName("dockerfile")]
    public string? Dockerfile { get; set; }

    public string? DockerfilePath { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("args")]
    public Dictionary<string, string>? Args { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

/// <summary>
/// Alias for DevcontainerBuildConfig
/// </summary>
public class DevcontainerBuild : DevcontainerBuildConfig
{
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
    public string DocumentationUrl => Documentation; // Alias for compatibility
    public Dictionary<string, object> DefaultOptions { get; set; } = new();
    public Dictionary<string, DevcontainerFeatureOption> AvailableOptions { get; set; } = new();
    public Dictionary<string, DevcontainerFeatureOption> Options => AvailableOptions; // Alias for compatibility
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public string[] ConflictsWith { get; set; } = Array.Empty<string>();
    public bool IsDeprecated { get; set; }
    public string? DeprecationMessage { get; set; }
    public string Maintainer { get; set; } = string.Empty;
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
    public Dictionary<string, string> RequiredEnvVars { get; set; } = new(); // Key: env var name, Value: description
    public bool RequiresDockerCompose { get; set; }
    public string? DockerComposeTemplate { get; set; }
    public string? Version { get; set; } // NuGet package version
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
    public DevcontainerTemplate? SelectedTemplate { get; set; }
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
    public List<string> NuGetSources { get; set; } = new();
    public string? TemplateVersion { get; set; }
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
    public bool DockerfileCreated { get; set; }
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
    public string GeneratedFilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<string> ValidationErrors { get; set; } = new();
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

/// <summary>
/// Runtime information for a running devcontainer
/// </summary>
public class DevcontainerRuntimeInfo
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public TimeSpan Uptime { get; set; }
    public string MemoryUsage { get; set; } = string.Empty;
    public string CpuUsage { get; set; } = string.Empty;
    public Dictionary<string, string> NetworkPorts { get; set; } = new();
    public string Status { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Options for spawning a devcontainer
/// </summary>
public class DevcontainerSpawnOptions
{
    /// <summary>
    /// Name of the project
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the project directory
    /// </summary>
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the .devcontainer folder
    /// </summary>
    public string DevcontainerPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional override for volume name (generated if not provided)
    /// </summary>
    public string? VolumeName { get; set; }

    /// <summary>
    /// Whether to copy source files into the volume (default: true)
    /// </summary>
    public bool CopySourceFiles { get; set; } = true;

    /// <summary>
    /// Whether to automatically launch VS Code (default: true)
    /// </summary>
    public bool LaunchVsCode { get; set; } = true;

    /// <summary>
    /// Whether to reuse existing container if found (default: true)
    /// </summary>
    public bool ReuseExisting { get; set; } = true;

    /// <summary>
    /// Spawn mode (Local or Remote)
    /// </summary>
    public SpawnMode Mode { get; set; } = SpawnMode.Local;

    /// <summary>
    /// Use bootstrap container approach (default: true)
    /// </summary>
    public bool UseBootstrapContainer { get; set; } = true;

    /// <summary>
    /// Custom bootstrap configuration (optional)
    /// </summary>
    public BootstrapContainerConfig? BootstrapConfig { get; set; }

    /// <summary>
    /// Docker build arguments to pass to devcontainer build (optional)
    /// Format: KEY=VALUE pairs
    /// </summary>
    public Dictionary<string, string>? BuildArgs { get; set; }

    /// <summary>
    /// Path to write devcontainer build output (optional)
    /// If specified, build output will be written to this file instead of console
    /// </summary>
    public string? BuildLogPath { get; set; }

    /// <summary>
    /// Whether to forward Docker credentials from host to devcontainer (default: true)
    /// When enabled, host's Docker config will be copied to devcontainer for authenticated registry access
    /// This matches VS Code behavior and prevents "Directory nonexistent" errors in postStartCommand
    /// Set to false with --no-forward-docker-config to disable
    /// </summary>
    public bool ForwardDockerConfig { get; set; } = true;

    /// <summary>
    /// Custom path to Docker config.json (optional, defaults to ~/.docker/config.json)
    /// </summary>
    public string? DockerConfigPath { get; set; }

    /// <summary>
    /// Rebuild behavior when configuration changes are detected
    /// Auto: Prompt user (default), Always: Force rebuild, Never: Skip rebuild, Prompt: Always ask
    /// </summary>
    public RebuildBehavior RebuildBehavior { get; set; } = RebuildBehavior.Auto;

    /// <summary>
    /// Whether to skip the rebuild prompt and continue with existing container even if config changed
    /// </summary>
    public bool SkipRebuild { get; set; } = false;
}

/// <summary>
/// Rebuild behavior options for configuration change detection
/// </summary>
public enum RebuildBehavior
{
    /// <summary>
    /// Automatically determine whether to prompt based on change detection
    /// </summary>
    Auto,

    /// <summary>
    /// Always rebuild without prompting
    /// </summary>
    Always,

    /// <summary>
    /// Never rebuild, always reuse existing container
    /// </summary>
    Never,

    /// <summary>
    /// Always prompt user regardless of change detection
    /// </summary>
    Prompt
}

/// <summary>
/// Result of a devcontainer spawn operation
/// </summary>
public class DevcontainerSpawnResult
{
    /// <summary>
    /// Whether the spawn was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// User-friendly message about the result
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// ID of the spawned container
    /// </summary>
    public string? ContainerId { get; set; }

    /// <summary>
    /// Name of the Docker volume used
    /// </summary>
    public string? VolumeName { get; set; }

    /// <summary>
    /// VS Code URI for connecting to the container
    /// </summary>
    public string? VsCodeUri { get; set; }

    /// <summary>
    /// List of errors encountered
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// List of warnings encountered
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Duration of the spawn operation
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Last completed step in the spawn workflow
    /// </summary>
    public DevcontainerSpawnStep CompletedStep { get; set; }

    /// <summary>
    /// Bootstrap container ID (if used)
    /// </summary>
    public string? BootstrapContainerId { get; set; }

    /// <summary>
    /// Bootstrap container logs (for debugging failures)
    /// </summary>
    [Obsolete("Use DevcontainerCliOutput and DevcontainerCliStderr for more detailed error information")]
    public string? BootstrapLogs { get; set; }

    /// <summary>
    /// Full stdout output from devcontainer CLI (for detailed error diagnostics)
    /// </summary>
    public string? DevcontainerCliOutput { get; set; }

    /// <summary>
    /// Full stderr output from devcontainer CLI (for detailed error diagnostics)
    /// </summary>
    public string? DevcontainerCliStderr { get; set; }
}

/// <summary>
/// Steps in the devcontainer spawn workflow
/// </summary>
public enum DevcontainerSpawnStep
{
    None = 0,
    DockerCheck = 1,
    DevcontainerCliCheck = 2,
    BootstrapImageCheck = 3,
    VolumeCreation = 4,
    BootstrapContainerStart = 5,
    FileCopyToBootstrap = 6,
    DevcontainerUp = 7,
    BootstrapCleanup = 8,
    VsCodeLaunch = 9,
    Completed = 10
}

/// <summary>
/// Mode for spawning devcontainers
/// </summary>
public enum SpawnMode
{
    /// <summary>
    /// Spawn locally on the current machine
    /// </summary>
    Local = 0,

    /// <summary>
    /// Spawn on a remote host (Phase 2)
    /// </summary>
    Remote = 1
}

/// <summary>
/// Result of Docker availability check
/// </summary>
public class DockerAvailabilityResult
{
    /// <summary>
    /// Whether Docker is available
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Message about Docker availability
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Docker version if available
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Whether Docker daemon is running
    /// </summary>
    public bool IsRunning { get; set; }
}

/// <summary>
/// Information about VS Code installation
/// </summary>
public class VsCodeInstallationInfo
{
    /// <summary>
    /// Whether VS Code is installed
    /// </summary>
    public bool IsInstalled { get; set; }

    /// <summary>
    /// Full path to VS Code executable
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// VS Code version
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Edition of VS Code (Stable or Insiders)
    /// </summary>
    public VsCodeEdition Edition { get; set; }
}

/// <summary>
/// VS Code edition types
/// </summary>
public enum VsCodeEdition
{
    Stable = 0,
    Insiders = 1
}

/// <summary>
/// Result from devcontainer CLI up command
/// </summary>
public class DevcontainerUpResult
{
    /// <summary>
    /// Outcome of the up command
    /// </summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>
    /// ID of the created container
    /// </summary>
    [JsonPropertyName("containerId")]
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Remote user in the container
    /// </summary>
    [JsonPropertyName("remoteUser")]
    public string RemoteUser { get; set; } = string.Empty;

    /// <summary>
    /// Remote workspace folder in the container
    /// </summary>
    [JsonPropertyName("remoteWorkspaceFolder")]
    public string RemoteWorkspaceFolder { get; set; } = string.Empty;
}

/// <summary>
/// Information about an existing devcontainer
/// </summary>
public class ExistingDevcontainerInfo
{
    /// <summary>
    /// Container ID
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Volume name used by the container
    /// </summary>
    public string VolumeName { get; set; } = string.Empty;

    /// <summary>
    /// When the container was created
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Whether the container is currently running
    /// </summary>
    public bool IsRunning { get; set; }
}

/// <summary>
/// Information about a devcontainer Docker volume
/// </summary>
public class DevcontainerVolumeInfo
{
    /// <summary>
    /// Volume name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Project name associated with the volume
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// When the volume was created
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Size of the volume in bytes (if available)
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Docker labels on the volume
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();
}

/// <summary>
/// Information about a managed devcontainer
/// </summary>
public class DevcontainerContainerInfo
{
    /// <summary>
    /// Container ID
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Container name
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Project name associated with the container
    /// </summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Volume name used by the container
    /// </summary>
    public string VolumeName { get; set; } = string.Empty;

    /// <summary>
    /// Container status (e.g., "running", "stopped", "paused", "exited")
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the container was created
    /// </summary>
    public DateTime CreatedDate { get; set; }

    /// <summary>
    /// Docker labels on the container
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>
    /// Remote workspace folder path inside the container
    /// </summary>
    public string WorkspaceFolder { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for spawning on a remote host (Phase 2)
/// </summary>
public class RemoteHostConfig
{
    /// <summary>
    /// Remote host address
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// SSH username
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// SSH port (default: 22)
    /// </summary>
    public int Port { get; set; } = 22;

    /// <summary>
    /// Path to SSH private key file
    /// </summary>
    public string? KeyPath { get; set; }
}

/// <summary>
/// Configuration for bootstrap container execution
/// </summary>
public class BootstrapContainerConfig
{
    public string ImageName { get; set; } = "pks-devcontainer-bootstrap";
    public string ImageTag { get; set; } = "latest";
    public string ContainerNamePrefix { get; set; } = "pks-bootstrap";
    public string VolumeName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public bool MountDockerSocket { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 600;
    public Dictionary<string, string> Labels { get; set; } = new()
    {
        { "pks.managed", "true" },
        { "pks.bootstrap", "true" }
    };
}

/// <summary>
/// Information about a running bootstrap container
/// </summary>
public class BootstrapContainerInfo
{
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public string VolumeName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
}

/// <summary>
/// Result of executing a command in bootstrap container
/// </summary>
public class BootstrapExecutionResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Combined output from stdout and stderr with clear labeling
    /// </summary>
    public string CombinedOutput
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Output))
            {
                parts.Add("=== STDOUT ===");
                parts.Add(Output.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(Error))
            {
                parts.Add("=== STDERR ===");
                parts.Add(Error.TrimEnd());
            }

            return parts.Count > 0 ? string.Join(Environment.NewLine, parts) : string.Empty;
        }
    }

    /// <summary>
    /// Formatted diagnostics including exit code, duration, and output
    /// </summary>
    public string FormattedDiagnostics()
    {
        var lines = new List<string>
        {
            $"Exit Code: {ExitCode}",
            $"Duration: {Duration.TotalSeconds:F2}s",
            string.Empty
        };

        if (!string.IsNullOrWhiteSpace(Output) || !string.IsNullOrWhiteSpace(Error))
        {
            lines.Add(CombinedOutput);
        }
        else
        {
            lines.Add("(No output captured)");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Result of ensuring bootstrap image is available
/// </summary>
public class BootstrapImageResult
{
    public bool Success { get; set; }
    public string ImageId { get; set; } = string.Empty;
    public string ImageName { get; set; } = string.Empty;
    public bool WasBuilt { get; set; }
    public TimeSpan BuildDuration { get; set; }
    public string Message { get; set; } = string.Empty;
}
/// <summary>
/// Result of computing configuration hash
/// </summary>
public class ConfigurationHashResult
{
    /// <summary>
    /// The computed SHA256 hash
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// List of files included in the hash computation
    /// </summary>
    public List<string> IncludedFiles { get; set; } = new();

    /// <summary>
    /// Individual file hashes (for detailed change detection)
    /// </summary>
    public Dictionary<string, string> FileHashes { get; set; } = new();

    /// <summary>
    /// Timestamp when hash was computed
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hash schema version (for future compatibility)
    /// </summary>
    public int Version { get; set; } = 1;
}

/// <summary>
/// Result of checking if configuration changed
/// </summary>
public class ConfigurationChangeResult
{
    /// <summary>
    /// Whether configuration changed
    /// </summary>
    public bool Changed { get; set; }

    /// <summary>
    /// Reason for the result
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Current configuration hash
    /// </summary>
    public string CurrentHash { get; set; } = string.Empty;

    /// <summary>
    /// Stored configuration hash from container label
    /// </summary>
    public string? StoredHash { get; set; }

    /// <summary>
    /// List of files that changed
    /// </summary>
    public List<string> ChangedFiles { get; set; } = new();

    /// <summary>
    /// Timestamp when container was built (from label)
    /// </summary>
    public DateTime? ContainerBuildTimestamp { get; set; }

    /// <summary>
    /// Detailed hash information
    /// </summary>
    public ConfigurationHashResult? HashDetails { get; set; }
}
