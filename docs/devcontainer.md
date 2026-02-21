# Implementing devcontainer volume spawning in .NET CLI tools

Automating VS Code's "Clone Repository in Container Volume" workflow from a C# CLI requires orchestrating four systems: Docker volumes, the devcontainer CLI, file operations, and VS Code's remote connection protocol. The most reliable approach uses a **hybrid architecture**—Docker.DotNet SDK for volume management combined with CLI subprocess calls for file copying and devcontainer orchestration.

## The volume-first workflow must follow a precise sequence

The correct order of operations differs fundamentally from standard bind-mount devcontainers. Because named volumes start empty, you must populate the volume before the devcontainer starts.

The five-step sequence that replicates VS Code's behavior:

1. **Create the named Docker volume** with identifying labels
2. **Copy .devcontainer folder** (and optionally source files) into the volume using a temporary container
3. **Create a minimal local workspace** containing only the devcontainer.json with `workspaceMount` configured for the volume
4. **Run `devcontainer up`** pointing to this local workspace folder
5. **Launch VS Code** with a properly constructed remote URI

This sequence matters because `devcontainer up` reads configuration from a local workspace folder, even when the actual source code lives in a volume. The local folder acts as a "bootstrap" configuration that tells the CLI which volume to mount.

## devcontainer.json configuration for volume-based workspaces

The critical configuration uses `workspaceMount` to override the default bind-mount behavior:

```json
{
  "name": "Volume-based DevContainer",
  "image": "mcr.microsoft.com/devcontainers/base:ubuntu",
  "workspaceMount": "source=my-project-source,target=/workspaces/project,type=volume",
  "workspaceFolder": "/workspaces/project",
  "remoteUser": "vscode"
}
```

The `workspaceMount` property accepts Docker's `--mount` flag format. Setting `type=volume` with a named `source` tells the devcontainer CLI to mount the named volume instead of creating a bind mount from the local filesystem. The `workspaceFolder` must match the volume's target path—this becomes the default directory when VS Code connects.

**Key gotcha**: When `workspaceMount` specifies `type=volume`, the `--workspace-folder` CLI argument still points to a local folder (for reading configuration), but no files from that folder appear in the container's workspace.

## Docker volume management from C# with hybrid approach

Docker has no native "copy files to volume" operation—you must use a temporary container as an intermediary. The recommended pattern combines the Docker.DotNet SDK for volume lifecycle operations with CLI subprocess calls for file operations.

```csharp
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Diagnostics;
using System.Text.Json;

public class DevContainerVolumeManager
{
    private readonly DockerClient _docker;

    public DevContainerVolumeManager()
    {
        // Auto-detects Docker endpoint (npipe on Windows, unix socket on Linux)
        _docker = new DockerClientConfiguration().CreateClient();
    }

    public async Task<string> CreateProjectVolumeAsync(string projectName)
    {
        string volumeName = $"devcontainer-{projectName}-{Guid.NewGuid():N[..8]}";

        await _docker.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = volumeName,
            Labels = new Dictionary<string, string>
            {
                ["devcontainer.project"] = projectName,
                ["devcontainer.created"] = DateTime.UtcNow.ToString("o"),
                ["vsch.local.repository.volume"] = volumeName
            }
        });

        return volumeName;
    }

    public async Task CopyToVolumeAsync(string localPath, string volumeName, string containerPath)
    {
        // CLI approach is more reliable than SDK tar streams
        string tempContainerName = $"devcontainer-copy-{Guid.NewGuid():N[..8]}";

        await RunDockerAsync($"container create --name {tempContainerName} " +
                            $"-v {volumeName}:{containerPath} alpine:latest");
        try
        {
            await RunDockerAsync($"cp \"{localPath}/.\" {tempContainerName}:{containerPath}");
        }
        finally
        {
            await RunDockerAsync($"container rm {tempContainerName}");
        }
    }

    private async Task<string> RunDockerAsync(string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception($"Docker command failed: {error}");

        return output.Trim();
    }
}
```

Install required packages: `dotnet add package Docker.DotNet`

## devcontainer CLI invocation returns structured JSON output

The `devcontainer up` command outputs JSON containing the created container's metadata, which you need for VS Code attachment:

```csharp
public class DevContainerCli
{
    public async Task<DevContainerResult> SpawnAsync(string workspaceFolder)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "devcontainer",
                Arguments = $"up --workspace-folder \"{workspaceFolder}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Parse the JSON output
        var result = JsonSerializer.Deserialize<DevContainerResult>(output);
        return result;
    }
}

public record DevContainerResult(
    string Outcome,
    string ContainerId,
    string RemoteUser,
    string RemoteWorkspaceFolder
);
```

Successful output structure:

```json
{
  "outcome": "success",
  "containerId": "f0a055ff056c1c1bb99cc09930efbf3a0437c54d9b4644695aa23c1d57b4bd11",
  "remoteUser": "vscode",
  "remoteWorkspaceFolder": "/workspaces/project"
}
```

## VS Code attachment requires hex-encoded URI construction

VS Code's remote attachment uses URIs in the format `vscode-remote://dev-container+<HEX_ENCODED_CONFIG>/<container_path>`. The hex-encoded portion can be either a simple path or a JSON object with detailed configuration.

```csharp
public class VsCodeLauncher
{
    public void AttachToDevContainer(string localConfigPath, string containerWorkspacePath)
    {
        // Simple path encoding
        string hexPath = Convert.ToHexString(
            System.Text.Encoding.UTF8.GetBytes(localConfigPath)).ToLowerInvariant();

        string uri = $"vscode-remote://dev-container+{hexPath}{containerWorkspacePath}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"--folder-uri \"{uri}\"",
            UseShellExecute = true
        });
    }

    // For more control, use JSON-encoded configuration
    public void AttachWithConfig(string hostPath, string configFile, string containerPath)
    {
        var config = new
        {
            hostPath = hostPath,
            configFile = configFile
        };

        string json = JsonSerializer.Serialize(config);
        string hexConfig = Convert.ToHexString(
            System.Text.Encoding.UTF8.GetBytes(json)).ToLowerInvariant();

        string uri = $"vscode-remote://dev-container+{hexConfig}{containerPath}";

        Process.Start(new ProcessStartInfo
        {
            FileName = "code",
            Arguments = $"--folder-uri \"{uri}\"",
            UseShellExecute = true
        });
    }
}
```

## Container identification uses specific Docker labels

Both VS Code and the devcontainer CLI apply labels for container identification and reconnection. Understanding these enables finding existing containers:

| Label                          | Purpose                                 |
| ------------------------------ | --------------------------------------- |
| `devcontainer.local_folder`    | Absolute local path to workspace folder |
| `devcontainer.config_file`     | Path to devcontainer.json used          |
| `vsch.local.folder`            | VS Code's version of local folder path  |
| `vsch.local.repository.volume` | Volume name for repository containers   |
| `vsch.quality`                 | VS Code edition (stable/insiders)       |

Finding existing containers by project:

```csharp
public async Task<string?> FindExistingContainerAsync(string localFolder)
{
    var containers = await _docker.Containers.ListContainersAsync(
        new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool>
                {
                    [$"devcontainer.local_folder={localFolder}"] = true
                }
            }
        });

    return containers.FirstOrDefault()?.ID;
}
```

## Complete orchestration implementation

Bringing all components together for a full "spawn devcontainer with volume" workflow:

```csharp
public class DevContainerSpawner
{
    private readonly DevContainerVolumeManager _volumes;
    private readonly DevContainerCli _cli;
    private readonly VsCodeLauncher _vscode;

    public async Task SpawnWithVolumeAsync(
        string projectName,
        string devcontainerPath,
        string? initialSourcePath = null)
    {
        // 1. Create named volume
        string volumeName = await _volumes.CreateProjectVolumeAsync(projectName);
        Console.WriteLine($"Created volume: {volumeName}");

        // 2. Copy .devcontainer to volume
        await _volumes.CopyToVolumeAsync(
            devcontainerPath,
            volumeName,
            "/workspaces/.devcontainer");

        // 3. Optionally copy initial source files
        if (initialSourcePath != null)
        {
            await _volumes.CopyToVolumeAsync(
                initialSourcePath,
                volumeName,
                "/workspaces/project");
        }

        // 4. Create local bootstrap workspace with volume-aware config
        string bootstrapPath = CreateBootstrapWorkspace(projectName, volumeName);

        // 5. Run devcontainer up
        var result = await _cli.SpawnAsync(bootstrapPath);

        if (result.Outcome != "success")
            throw new Exception($"devcontainer up failed: {result.Outcome}");

        Console.WriteLine($"Container started: {result.ContainerId[..12]}");

        // 6. Launch VS Code attached to container
        _vscode.AttachToDevContainer(bootstrapPath, result.RemoteWorkspaceFolder);
    }

    private string CreateBootstrapWorkspace(string projectName, string volumeName)
    {
        string bootstrapPath = Path.Combine(
            Path.GetTempPath(),
            $"devcontainer-bootstrap-{projectName}");

        Directory.CreateDirectory(Path.Combine(bootstrapPath, ".devcontainer"));

        var config = new
        {
            name = projectName,
            image = "mcr.microsoft.com/devcontainers/base:ubuntu",
            workspaceMount = $"source={volumeName},target=/workspaces/project,type=volume",
            workspaceFolder = "/workspaces/project"
        };

        File.WriteAllText(
            Path.Combine(bootstrapPath, ".devcontainer", "devcontainer.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        return bootstrapPath;
    }
}
```

## Critical gotchas with volume-based workflows

**Permissions ownership**: Volumes created by containers running as root cause permission issues for non-root users. Add a post-create command to fix ownership:

```json
{
  "postCreateCommand": "sudo chown -R vscode:vscode /workspaces/project"
}
```

**Empty volume bootstrap**: The volume starts empty. If your devcontainer expects source code to exist, either copy it during volume setup or use `postCreateCommand` to clone/initialize.

**Config file caching**: VS Code caches devcontainer configurations. Changes to devcontainer.json in the volume may not take effect until you "Rebuild Container" from the command palette.

**Docker Compose volumes**: The `workspaceMount` property is ignored when using Docker Compose. Define volumes in docker-compose.yml instead and reference them in the compose file's `volumes` section.

**Variable substitution**: Use `${devcontainerId}` for stable, unique volume names per project configuration—this hash remains consistent across rebuilds but changes if the devcontainer.json location changes.
