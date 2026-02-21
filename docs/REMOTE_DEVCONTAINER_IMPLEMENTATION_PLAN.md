# PKS CLI Remote Devcontainer Spawning - Comprehensive Implementation Plan
## Updated with Reconnection & Lifecycle Management

## Executive Summary

This plan extends the existing PKS CLI devcontainer spawning functionality to support remote host deployment with comprehensive container lifecycle management. The implementation adds critical reconnection workflows, container discovery, and lifecycle operations while maintaining the existing direct SSH connection architecture and Docker.DotNet integration strategy.

## Architecture Decision: Direct SSH Connections

**Approach**: Use Docker.DotNet with direct SSH URI connections instead of global Docker context switching.

```csharp
// Create isolated Docker client with SSH connection
var config = new DockerClientConfiguration(
    new Uri($"ssh://{username}@{host}:{port}"));
var dockerClient = config.CreateClient();

// Use this client for all operations - no global state changes
await spawnerService.SpawnAsync(options, dockerClient);
```

**Why This is Superior**:

| Aspect | Direct SSH (Recommended) | Global Context Switching |
|--------|-------------------------|--------------------------|
| **Thread Safety** | ✅ Each operation isolated | ❌ Global state corruption risk |
| **Crash Safety** | ✅ No cleanup needed | ❌ Requires try-finally restoration |
| **User Impact** | ✅ Zero impact on local Docker | ❌ Affects user's Docker commands |
| **Parallel Operations** | ✅ Multiple remotes simultaneously | ❌ Sequential only |
| **Complexity** | ✅ Simple, direct connections | ❌ Context management overhead |

## Current State Analysis

### What Exists Today

**Strong Foundation:**
1. Local spawning fully implemented with Docker.DotNet
2. Bootstrap container pattern for volume spawning
3. Configuration hash detection (three-way comparison)
4. Container discovery via `ListManagedContainersAsync()`
5. VS Code integration with URI generation
6. Basic `DevcontainerConnectCommand` (local only)

**What's Missing:**
- Remote spawning implementation (`SpawnRemoteAsync` is stub)
- Remote container discovery and listing
- Container lifecycle operations (stop, restart, logs, exec, remove)
- Remote reconnection workflow
- SSH connection management
- Container labeling strategy

## Phase 0: Docker.DotNet Migration (Week 1)

### Refactor Subprocess Docker Calls

**Current Issue**: 10 locations use subprocess `docker` CLI calls that won't work with remote hosts.

**Solution**: Replace all with Docker.DotNet equivalents.

| Subprocess Call | Docker.DotNet Alternative |
|----------------|---------------------------|
| `docker build` | `dockerClient.Images.BuildImageFromDockerfileAsync()` |
| `docker inspect` | `dockerClient.Images.InspectImageAsync()` |
| `docker run` | `dockerClient.Containers.CreateContainerAsync()` + `StartContainerAsync()` |
| `docker exec` | `dockerClient.Exec.ExecCreateContainerAsync()` + `StartAndAttachContainerExecAsync()` |
| `docker cp` | `dockerClient.Containers.ExtractArchiveToContainerAsync()` / `GetArchiveFromContainerAsync()` |
| `docker stop` | `dockerClient.Containers.StopContainerAsync()` |
| `docker rm` | `dockerClient.Containers.RemoveContainerAsync()` |

**Tar Archive Helpers** (using .NET 10's built-in `System.Formats.Tar`):

```csharp
private async Task<Stream> CreateTarArchiveAsync(string sourcePath)
{
    var memoryStream = new MemoryStream();
    using (var tarWriter = new TarWriter(memoryStream, leaveOpen: true))
    {
        await TarFile.CreateFromDirectoryAsync(sourcePath, tarWriter, includeBaseDirectory: false);
    }
    memoryStream.Position = 0;
    return memoryStream;
}

private async Task ExtractTarArchiveAsync(Stream tarStream, string destPath)
{
    await TarFile.ExtractToDirectoryAsync(tarStream, destPath, overwriteFiles: true);
}
```

**Refactor DevcontainerSpawnerService**:

```csharp
// OLD: Uses injected _dockerClient field
public async Task<DevcontainerSpawnResult> SpawnLocalAsync(...)
{
    await _dockerClient.Containers.CreateContainerAsync(...);
}

// NEW: Accepts dockerClient parameter
private async Task<DevcontainerSpawnResult> SpawnWithClientAsync(
    DevcontainerSpawnOptions options,
    IDockerClient dockerClient)
{
    await dockerClient.Containers.CreateContainerAsync(...); // Uses parameter
}

public async Task<DevcontainerSpawnResult> SpawnLocalAsync(...)
{
    return await SpawnWithClientAsync(options, _localDockerClient);
}

public async Task<DevcontainerSpawnResult> SpawnRemoteAsync(
    DevcontainerSpawnOptions options,
    RemoteHostConfig remoteHost)
{
    var remoteClient = _dockerClientFactory.CreateRemoteClient(remoteHost);
    return await SpawnWithClientAsync(options, remoteClient);
}
```

## Phase 1: Foundation - SSH & Remote Infrastructure (Week 2)

### 1.1 SSH Connection Management

**New Service: `IRemoteSshService`**

```csharp
public interface IRemoteSshService
{
    Task<RemoteSshTestResult> TestConnectionAsync(RemoteHostConfig config);
    Task<RemoteCommandResult> ExecuteCommandAsync(RemoteHostConfig config, string command, bool sudo = false);
    Task<RemoteTransferResult> TransferFilesAsync(RemoteHostConfig config, string localPath, string remotePath);
    Task<RemoteSshTunnel> CreateDockerTunnelAsync(RemoteHostConfig config, int localPort = 2375);
}
```

**Implementation**: Use `SSH.NET` NuGet package with connection pooling and retry logic.

### 1.2 Remote Docker Client Factory

**New Service: `IRemoteDockerClientFactory`**

```csharp
public interface IRemoteDockerClientFactory
{
    Task<IDockerClient> CreateRemoteClientAsync(RemoteHostConfig config);
    Task<DockerAvailabilityResult> CheckRemoteDockerAsync(RemoteHostConfig config);
}
```

**Implementation**:
1. Create SSH tunnel forwarding local port to remote Docker socket
2. Create `DockerClient` pointing to `tcp://localhost:{tunnelPort}`
3. Maintain tunnel lifetime tied to client

### 1.3 Remote Host Configuration

**Enhanced Model**:

```csharp
public class RemoteHostConfig
{
    public string Id { get; set; } = string.Empty; // e.g., "hetzner", "aws-dev"
    public string Host { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string? KeyPath { get; set; }
    public RemoteHostCapabilities Capabilities { get; set; } = new();
}
```

**Storage**: `~/.pks/remote-hosts.json`

**Commands**:
- `pks remote add <id> --host <host> --user <user> --key <path>`
- `pks remote list`
- `pks remote test <id>`
- `pks remote remove <id>`

## Phase 2: Remote Spawning + Discovery (Week 3)

### 2.1 Container Labeling Strategy

**Labels Applied to Containers**:

```csharp
var labels = new Dictionary<string, string>
{
    ["pks.managed"] = "true",
    ["pks.project.name"] = options.ProjectName,
    ["pks.project.path"] = options.ProjectPath,
    ["pks.spawn.date"] = DateTime.UtcNow.ToString("o"),
    ["pks.remote.host"] = remoteHost.Id,
    ["pks.volume.name"] = volumeName,
    ["pks.workspace.folder"] = $"/workspaces/{options.ProjectName}",
    ["pks.spawned.by"] = Environment.UserName,
    ["devcontainer.config.hash"] = configHash
};
```

**Purpose**: Enable container discovery, reconnection, and change detection.

### 2.2 Remote Container Discovery Service

**New Service: `IRemoteContainerDiscoveryService`**

```csharp
public interface IRemoteContainerDiscoveryService
{
    Task<List<RemoteContainerInfo>> ListRemoteContainersAsync(
        RemoteHostConfig remoteHost,
        bool includeAll = false);

    Task<RemoteContainerInfo?> FindContainerByProjectAsync(
        RemoteHostConfig remoteHost,
        string projectName);

    Task<RemoteContainerInfo?> FindContainerByIdAsync(
        RemoteHostConfig remoteHost,
        string containerIdOrPartial);

    Task<RemoteContainerDetails> GetContainerDetailsAsync(
        RemoteHostConfig remoteHost,
        string containerIdOrName);
}
```

**Models**:

```csharp
public class RemoteContainerInfo
{
    public string ContainerId { get; set; }
    public string ContainerName { get; set; }
    public string ProjectName { get; set; }
    public string ProjectPath { get; set; }
    public string VolumeName { get; set; }
    public string RemoteHostId { get; set; }
    public ContainerState State { get; set; }
    public DateTime CreatedAt { get; set; }
    public string WorkspaceFolder { get; set; }
}

public enum ContainerState
{
    Created, Running, Paused, Restarting, Removing, Exited, Dead
}
```

### 2.3 Command: `devcontainer list --remote`

**New Command: `DevcontainerContainersCommand`**

```bash
# List local containers
pks devcontainer list

# List remote containers
pks devcontainer list --remote hetzner

# Show all (including stopped)
pks devcontainer list --remote hetzner --all

# JSON output
pks devcontainer list --format json
```

**Output**: Table with project name, container ID, status, host, and age.

## Phase 3: Lifecycle Management & Reconnection (Week 4-5)

### 3.1 Remote Container Lifecycle Service

**New Service: `IRemoteContainerLifecycleService`**

```csharp
public interface IRemoteContainerLifecycleService
{
    Task<bool> StopContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName);
    Task<bool> StartContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName);
    Task<bool> RestartContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName);
    Task<Stream> GetContainerLogsAsync(RemoteHostConfig remoteHost, string containerIdOrName, ContainerLogsParameters parameters);
    Task<CommandExecutionResult> ExecInContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName, string[] command);
    Task<bool> RemoveContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName, bool removeVolumes = false);
    Task<DevcontainerSpawnResult> RebuildContainerAsync(RemoteHostConfig remoteHost, string containerIdOrName);
}
```

### 3.2 Reconnection Workflow

**Enhanced `DevcontainerConnectCommand`**:

```bash
# Connect to running container
pks devcontainer connect my-project --remote hetzner

# Connect by container ID
pks devcontainer connect a1b2c3d4 --remote hetzner

# Auto-restart if stopped
pks devcontainer connect my-project --remote hetzner --restart-if-stopped

# Interactive selection
pks devcontainer connect --remote hetzner
```

**Features**:
- Find containers by project name or ID
- Handle stopped containers (prompt or auto-restart)
- Interactive container selection if multiple found
- Generate correct VS Code remote URI

**VS Code Remote URI Format**:
```
vscode-remote://ssh-remote+{user}@{host}:{port}/workspaces/{project}
```

### 3.3 Lifecycle Commands

**New Commands**:

1. **`DevcontainerStopCommand`**
   ```bash
   pks devcontainer stop my-project --remote hetzner
   pks devcontainer stop a1b2c3d4 --remote hetzner --timeout 30
   ```

2. **`DevcontainerRestartCommand`**
   ```bash
   pks devcontainer restart my-project --remote hetzner
   ```

3. **`DevcontainerLogsCommand`**
   ```bash
   pks devcontainer logs my-project --remote hetzner
   pks devcontainer logs my-project --remote hetzner --follow --tail 100
   pks devcontainer logs my-project --remote hetzner --timestamps
   ```

4. **`DevcontainerExecCommand`**
   ```bash
   pks devcontainer exec my-project --remote hetzner "npm install"
   pks devcontainer exec my-project --remote hetzner --interactive "bash"
   ```

5. **`DevcontainerRemoveCommand`**
   ```bash
   pks devcontainer rm my-project --remote hetzner
   pks devcontainer rm my-project --remote hetzner --volumes --force
   ```

6. **`DevcontainerRebuildCommand`**
   ```bash
   pks devcontainer rebuild my-project --remote hetzner
   pks devcontainer rebuild my-project --remote hetzner --no-cache
   ```

## Phase 4: Integration & Polish (Week 6)

### Error Recovery Framework

```csharp
private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, int maxRetries = 3)
{
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            return await operation();
        }
        catch (SocketException) when (i < maxRetries - 1)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, i)));
        }
    }
    throw new Exception($"Operation failed after {maxRetries} retries");
}
```

### Prerequisites Validation

- Check Docker version on remote (18.06+ required)
- Validate SSH key permissions (`chmod 600`)
- Check remote disk space before spawn
- Verify network connectivity quality

## User Workflows

### Workflow 1: Basic Remote Development Session

```bash
# Day 1: Setup and spawn
pks remote add hetzner --host 192.168.1.100 --user root --key ~/.ssh/hetzner_rsa
pks remote test hetzner

cd /path/to/my-project
pks devcontainer spawn --remote hetzner
# VS Code opens, work continues...
# Close laptop, go home

# Day 2: Reconnect
pks devcontainer list --remote hetzner
pks devcontainer connect my-project --remote hetzner
# VS Code opens to same container, all state preserved
```

### Workflow 2: Container Troubleshooting

```bash
# Check container status
pks devcontainer list --remote hetzner

# View logs
pks devcontainer logs my-project --remote hetzner --tail 100

# Execute diagnostic commands
pks devcontainer exec my-project --remote hetzner "npm list"
pks devcontainer exec my-project --remote hetzner "df -h"

# Restart if needed
pks devcontainer restart my-project --remote hetzner

# Rebuild if configuration changed
pks devcontainer rebuild my-project --remote hetzner
```

### Workflow 3: Cleanup

```bash
# List all containers
pks devcontainer list --remote hetzner --all

# Remove old container
pks devcontainer rm old-project --remote hetzner --volumes
```

## Key Design Decisions

1. **Container Identification**: Support both container ID and project name
2. **Stopped Container Handling**: Prompt by default, `--restart-if-stopped` for automation
3. **Volume Cleanup**: Keep by default, require explicit `--volumes` flag
4. **Multi-User Safety**: Include username in container names to avoid conflicts
5. **Error Messages**: Actionable guidance with next steps
6. **SSH Tunneling**: Secure approach instead of exposing Docker API
7. **Label Namespace**: Use `pks.*` prefix for all PKS-managed labels
8. **Configuration Hash**: Store in container labels for change detection

## File Organization

```
src/
├── Commands/
│   ├── Devcontainer/
│   │   ├── DevcontainerSpawnCommand.cs          (ENHANCE - add --remote)
│   │   ├── DevcontainerConnectCommand.cs        (ENHANCE - remote support)
│   │   ├── DevcontainerContainersCommand.cs     (RENAME from ListCommand)
│   │   ├── DevcontainerStopCommand.cs           (NEW)
│   │   ├── DevcontainerRestartCommand.cs        (NEW)
│   │   ├── DevcontainerLogsCommand.cs           (NEW)
│   │   ├── DevcontainerExecCommand.cs           (NEW)
│   │   ├── DevcontainerRebuildCommand.cs        (NEW)
│   │   └── DevcontainerRemoveCommand.cs         (NEW)
│   │
│   └── Remote/
│       ├── RemoteAddCommand.cs                  (NEW)
│       ├── RemoteListCommand.cs                 (NEW)
│       ├── RemoteTestCommand.cs                 (NEW)
│       └── RemoteRemoveCommand.cs               (NEW)
│
├── Infrastructure/
│   └── Services/
│       ├── IDevcontainerSpawnerService.cs       (ENHANCE)
│       ├── DevcontainerSpawnerService.cs        (REFACTOR - Docker.DotNet)
│       │
│       ├── IRemoteSshService.cs                 (NEW)
│       ├── RemoteSshService.cs                  (NEW)
│       │
│       ├── IRemoteDockerClientFactory.cs        (NEW)
│       ├── RemoteDockerClientFactory.cs         (NEW)
│       │
│       ├── IRemoteContainerDiscoveryService.cs  (NEW)
│       ├── RemoteContainerDiscoveryService.cs   (NEW)
│       │
│       ├── IRemoteContainerLifecycleService.cs  (NEW)
│       ├── RemoteContainerLifecycleService.cs   (NEW)
│       │
│       └── Models/
│           └── DevcontainerModels.cs            (ENHANCE)
```

## Dependencies

**New NuGet Package**:
```xml
<PackageReference Include="SSH.NET" Version="2024.1.0" />
```

**Existing Packages** (already included):
```xml
<PackageReference Include="Docker.DotNet" Version="3.125.15" />
<PackageReference Include="System.Formats.Tar" /> <!-- Built-in .NET 10 -->
```

## Testing Strategy

### Unit Tests
- SSH service connection tests
- Container discovery by ID/name tests
- Lifecycle operation tests
- Label parsing tests

### Integration Tests
- End-to-end remote spawn
- Reconnection workflow
- Network interruption handling
- Concurrent remote operations

### Manual Testing Checklist
- [ ] SSH connection to remote host
- [ ] Remote spawn with file transfer
- [ ] Remote container listing
- [ ] Reconnect to running container
- [ ] Reconnect to stopped container with auto-restart
- [ ] Stop/restart/logs/exec/remove operations
- [ ] Multi-remote host management
- [ ] Error handling and recovery

## Success Criteria

### Functional Requirements
- ✅ Successfully spawn devcontainer on remote host
- ✅ List all PKS-managed containers on remote
- ✅ Connect to running remote container
- ✅ Auto-restart stopped containers
- ✅ Full lifecycle operations (stop, restart, logs, exec, remove)
- ✅ VS Code remote integration

### Non-Functional Requirements
- ✅ Remote spawn within 2x local spawn time
- ✅ Container list loads within 2 seconds
- ✅ Reconnection within 5 seconds
- ✅ Graceful network interruption handling
- ✅ Clear error messages with troubleshooting guidance

## Critical Files for Implementation

1. **`src/Infrastructure/Services/DevcontainerSpawnerService.cs`**
   - Refactor all subprocess Docker calls to Docker.DotNet
   - Implement `SpawnRemoteAsync` method

2. **`src/Infrastructure/Services/RemoteSshService.cs`**
   - SSH connection management and tunneling

3. **`src/Infrastructure/Services/RemoteDockerClientFactory.cs`**
   - Create SSH-tunneled Docker clients

4. **`src/Infrastructure/Services/RemoteContainerDiscoveryService.cs`**
   - Container listing and discovery

5. **`src/Infrastructure/Services/RemoteContainerLifecycleService.cs`**
   - All lifecycle operations implementation

6. **`src/Commands/Devcontainer/DevcontainerConnectCommand.cs`**
   - Enhanced reconnection workflow

7. **`src/Infrastructure/Services/Models/DevcontainerModels.cs`**
   - New models for remote containers

## Timeline

- **Week 1**: Phase 0 - Docker.DotNet migration
- **Week 2**: Phase 1 - SSH & remote infrastructure
- **Week 3**: Phase 2 - Remote spawning + discovery
- **Week 4-5**: Phase 3 - Lifecycle management + reconnection
- **Week 6**: Phase 4 - Integration, polish, testing

## Summary

This plan enables the complete remote development workflow:

1. **Spawn remotely** - `pks devcontainer spawn --remote hetzner`
2. **Close laptop** - Container keeps running on remote host
3. **Come back later** - `pks devcontainer connect my-project --remote hetzner`
4. **Full control** - Stop, restart, logs, exec, rebuild, remove

The implementation uses direct SSH connections for security, Docker.DotNet for consistency, and comprehensive labeling for discovery. All operations are thread-safe, crash-safe, and have zero impact on the user's local Docker environment.
