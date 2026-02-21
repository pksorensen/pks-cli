using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for spawning devcontainers in Docker volumes
/// </summary>
public interface IDevcontainerSpawnerService
{
    /// <summary>
    /// Spawns a devcontainer locally using Docker volumes
    /// </summary>
    /// <param name="options">Spawn configuration options</param>
    /// <param name="onProgress">Optional callback invoked with progress status messages</param>
    /// <returns>Result of the spawn operation including container ID and status</returns>
    Task<DevcontainerSpawnResult> SpawnLocalAsync(DevcontainerSpawnOptions options, Action<string>? onProgress = null);

    /// <summary>
    /// Checks if Docker is available and running
    /// </summary>
    /// <returns>Docker availability status including version and running state</returns>
    Task<DockerAvailabilityResult> CheckDockerAvailabilityAsync();

    /// <summary>
    /// Checks if devcontainer CLI is installed
    /// </summary>
    /// <returns>True if devcontainer CLI is available, false otherwise</returns>
    Task<bool> IsDevcontainerCliInstalledAsync();

    /// <summary>
    /// Checks if VS Code is installed and returns information
    /// </summary>
    /// <returns>VS Code installation details including path and version</returns>
    Task<VsCodeInstallationInfo> CheckVsCodeInstallationAsync();

    /// <summary>
    /// Generates a unique volume name for a project
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <returns>Unique volume name in the format pks-{projectName}-{timestamp}</returns>
    string GenerateVolumeName(string projectName);

    /// <summary>
    /// Cleans up after a failed spawn (removes volume and temp files)
    /// </summary>
    /// <param name="volumeName">Name of the volume to remove</param>
    /// <param name="bootstrapPath">Optional path to bootstrap script to delete</param>
    /// <param name="bootstrapContainerId">Optional bootstrap container ID to stop and remove</param>
    Task CleanupFailedSpawnAsync(string volumeName, string? bootstrapPath = null, string? bootstrapContainerId = null);

    /// <summary>
    /// Finds existing devcontainer for a project path
    /// </summary>
    /// <param name="projectPath">Path to the project directory</param>
    /// <returns>Information about existing container if found, null otherwise</returns>
    Task<ExistingDevcontainerInfo?> FindExistingContainerAsync(string projectPath);

    /// <summary>
    /// Lists all managed devcontainer volumes
    /// </summary>
    /// <returns>List of all devcontainer volumes managed by PKS CLI</returns>
    Task<List<DevcontainerVolumeInfo>> ListManagedVolumesAsync();

    /// <summary>
    /// Spawns a devcontainer on a remote host (Phase 2 extension point)
    /// </summary>
    /// <param name="options">Spawn configuration options</param>
    /// <param name="remoteHost">Remote host configuration for SSH connection</param>
    /// <returns>Result of the remote spawn operation</returns>
    Task<DevcontainerSpawnResult> SpawnRemoteAsync(
        DevcontainerSpawnOptions options,
        RemoteHostConfig remoteHost);

    /// <summary>
    /// Lists all managed devcontainer containers
    /// </summary>
    /// <returns>List of all devcontainer containers managed by PKS CLI, including running and stopped containers</returns>
    Task<List<DevcontainerContainerInfo>> ListManagedContainersAsync();

    /// <summary>
    /// Gets the VS Code remote container URI for a specific container
    /// </summary>
    /// <param name="containerId">The ID of the container to connect to</param>
    /// <param name="workspaceFolder">The workspace folder path inside the container</param>
    /// <returns>VS Code remote container URI in the format vscode-remote://attached-container+{containerId}{workspaceFolder}</returns>
    Task<string> GetContainerVsCodeUriAsync(string containerId, string workspaceFolder);

    /// <summary>
    /// Starts a stopped devcontainer
    /// </summary>
    /// <param name="containerId">Container ID to start</param>
    /// <returns>True if started successfully</returns>
    Task<bool> StartContainerAsync(string containerId);

    /// <summary>
    /// Launches VS Code with the specified remote container URI
    /// </summary>
    /// <param name="vsCodeUri">VS Code remote container URI</param>
    Task LaunchVsCodeAsync(string vsCodeUri);

    /// <summary>
    /// Computes configuration hash from files inside a Docker volume
    /// </summary>
    /// <param name="volumeName">Volume name to read from</param>
    /// <param name="projectName">Project name (determines path inside volume)</param>
    /// <returns>Configuration hash computed from volume files, or null if error</returns>
    Task<string?> ComputeVolumeHashAsync(string volumeName, string projectName);

    /// <summary>
    /// Syncs .devcontainer files from volume to host
    /// </summary>
    /// <param name="volumeName">Volume name to sync from</param>
    /// <param name="projectName">Project name (determines path inside volume)</param>
    /// <param name="hostProjectPath">Host project path to sync to</param>
    /// <returns>True if sync successful</returns>
    Task<bool> SyncVolumeToHostAsync(string volumeName, string projectName, string hostProjectPath);

    /// <summary>
    /// Gets a specific label value from a container
    /// </summary>
    /// <param name="containerId">Container ID</param>
    /// <param name="labelKey">Label key to retrieve</param>
    /// <returns>Label value if found, null otherwise</returns>
    Task<string?> GetContainerLabelAsync(string containerId, string labelKey);

    /// <summary>
    /// Computes configuration hash from host files
    /// </summary>
    /// <param name="projectPath">Project path</param>
    /// <param name="devcontainerPath">Path to .devcontainer directory</param>
    /// <returns>Configuration hash</returns>
    Task<string> ComputeConfigurationHashAsync(string projectPath, string devcontainerPath);
}
