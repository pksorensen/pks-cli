# VS Code Launching in PKS CLI

**Date**: 2026-01-07
**Purpose**: Document how PKS CLI launches VS Code to connect to spawned devcontainers

---

## Overview

PKS CLI automatically launches VS Code and connects it to the spawned devcontainer using VS Code's remote container protocol. This document explains the mechanism, URI format, and how it differs from VS Code's native behavior.

---

## How It Works

### High-Level Flow

1. **Devcontainer spawns** via devcontainer CLI
2. **DevcontainerUpResult parsed** - extracts `containerId` and `remoteWorkspaceFolder`
3. **VS Code URI constructed** - creates special `vscode-remote://` URI
4. **VS Code launched** - opens with `--folder-uri` flag pointing to container

### Code Flow

```
SpawnLocalAsync()
  └─> devcontainer up (via devcontainer CLI)
      └─> ParseDevcontainerUpResult()
          ├─> Extract containerId
          └─> Extract remoteWorkspaceFolder
  └─> GetContainerVsCodeUriAsync()
      ├─> Hex-encode containerId
      └─> Construct vscode-remote:// URI
  └─> LaunchVsCodeAsync()
      └─> Execute: code --folder-uri "{uri}"
```

---

## VS Code URI Format

### Format Structure

```
vscode-remote://attached-container+{hexEncodedContainerId}{workspaceFolder}
```

### Components

1. **Protocol**: `vscode-remote://`
   - VS Code's remote development protocol
   - Tells VS Code to connect to a remote environment

2. **Connection Type**: `attached-container`
   - Specifies connection to an existing Docker container
   - Alternative: `dev-container+{hexEncodedPath}` (for workspace-based connections)

3. **Container ID**: `{hexEncodedContainerId}`
   - Container ID converted to lowercase hexadecimal
   - Example: Container `507abfd97b49` → hex-encoded

4. **Workspace Folder**: `{workspaceFolder}`
   - Path inside the container to open
   - Example: `/workspaces/test777`
   - Must match the actual workspace path in the container

### Example

**Container ID**: `507abfd97b49abcd1234567890abcdef...` (full 64-char ID)
**Workspace Folder**: `/workspaces/test777`

**Constructed URI**:
```
vscode-remote://attached-container+353037616266643937623439616263643132...2f776f726b7370616365732f7465737437373
```

**Breakdown**:
- `vscode-remote://` - Protocol
- `attached-container+` - Connection type
- `353037616266643937623439...` - Hex-encoded container ID
- No separator between container ID and workspace folder (appended directly)
- Workspace folder path included in hex encoding

---

## Implementation Details

### 1. Container ID Retrieval

**Source**: `devcontainer up` command output (JSON)

```json
{
  "outcome": "success",
  "containerId": "507abfd97b49abcd1234567890abcdef...",
  "remoteWorkspaceFolder": "/workspaces/test777"
}
```

**Parsing**: `DevcontainerSpawnerService.cs:2213-2233`

```csharp
var upResult = JsonSerializer.Deserialize<DevcontainerUpResult>(jsonLine);
var containerId = upResult.ContainerId;
var remoteWorkspaceFolder = upResult.RemoteWorkspaceFolder;
```

### 2. URI Construction

**Code**: `DevcontainerSpawnerService.cs:926-948`

```csharp
public async Task<string> GetContainerVsCodeUriAsync(string containerId, string workspaceFolder)
{
    // Hex encode the container ID for VS Code remote URI
    var hexEncodedContainerId = Convert.ToHexString(Encoding.UTF8.GetBytes(containerId)).ToLowerInvariant();

    // Format: vscode-remote://attached-container+{hexEncodedContainerId}/{workspaceFolder}
    var uri = $"vscode-remote://attached-container+{hexEncodedContainerId}{workspaceFolder}";

    return uri;
}
```

**Key Points**:
- Uses `Convert.ToHexString()` for encoding
- Converts to lowercase (VS Code requirement)
- Appends workspace folder directly (no separator)

### 3. VS Code Launch

**Code**: `DevcontainerSpawnerService.cs:1143-1172`

```csharp
private async Task<bool> LaunchVsCodeAsync(string uri, string vsCodePath)
{
    var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = vsCodePath,
            Arguments = $"--folder-uri \"{uri}\"",
            UseShellExecute = true,
            CreateNoWindow = false
        }
    };

    process.Start();
    return true;
}
```

**Command Executed**:
```bash
code --folder-uri "vscode-remote://attached-container+353037616266643937623439...2f776f726b7370616365732f74657374373737"
```

### 4. VS Code Detection

**Code**: `DevcontainerSpawnerService.cs:851-919`

Checks for VS Code in this order:
1. `code` (VS Code standard)
2. `code-insiders` (VS Code Insiders)
3. Windows paths: `%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd`

**Priority**: Standard VS Code preferred over Insiders

---

## Bootstrap Container vs Direct Workflow

PKS CLI supports two workflows for spawning devcontainers:

### Bootstrap Container Workflow (Default)

**When**: `--no-bootstrap` flag is NOT used (default behavior)

**Flow**:
```
1. Create Docker volume
2. Start bootstrap container (with Docker socket and volume mounted)
3. Copy source files to volume (via bootstrap container)
4. Fix file ownership (chown to remoteUser)
5. Create override config (with resolved workspaceFolder)
6. Run devcontainer up (inside bootstrap container)
7. Get containerId from result
8. Launch VS Code with attached-container URI
```

**URI Construction**: Uses `GetContainerVsCodeUriAsync()` with actual container ID

**Code**: `DevcontainerSpawnerService.cs:288-291`

```csharp
if (options.UseBootstrapContainer)
{
    uri = await GetContainerVsCodeUriAsync(upResult.ContainerId, upResult.RemoteWorkspaceFolder);
}
```

### Direct Workflow (Legacy)

**When**: `--no-bootstrap` flag is used

**Flow**:
```
1. Create Docker volume
2. Copy files directly to volume
3. Run devcontainer up directly (not in bootstrap)
4. Get containerId from result
5. Launch VS Code with dev-container URI
```

**URI Construction**: Uses `ConstructVsCodeUri()` with workspace path

**Code**: `DevcontainerSpawnerService.cs:1127-1141`

```csharp
private string ConstructVsCodeUri(string bootstrapPath, string remoteWorkspaceFolder)
{
    // Hex encode bootstrap path
    var hexEncodedPath = Convert.ToHexString(Encoding.UTF8.GetBytes(bootstrapPath)).ToLowerInvariant();

    // Format: vscode-remote://dev-container+{hexEncodedPath}{remoteWorkspaceFolder}
    return $"vscode-remote://dev-container+{hexEncodedPath}{remoteWorkspaceFolder}";
}
```

**Note**: This uses `dev-container+` instead of `attached-container+`

---

## Important Considerations

### 1. workspaceFolder Must Be Correct

**Critical**: The `remoteWorkspaceFolder` in the URI must match the actual workspace path in the container.

**Problem We Solved**:
- Override config was removing `workspaceFolder` property
- Devcontainer CLI defaulted to `/` (root directory)
- VS Code opened at `/` instead of `/workspaces/test777`

**Solution**: `DevcontainerSpawnerService.cs:1729-1736`
```csharp
// Replace workspaceFolder with actual value (resolve ${localWorkspaceFolderBasename})
if (property.Name == "workspaceFolder")
{
    var actualWorkspaceFolder = $"/workspaces/{projectName}";
    writer.WritePropertyName(property.Name);
    writer.WriteStringValue(actualWorkspaceFolder);
    continue;
}
```

This ensures `remoteWorkspaceFolder` from devcontainer CLI output is `/workspaces/test777`, not `/`.

### 2. Container Must Be Running

**Requirement**: The container must be running when VS Code tries to connect.

**PKS CLI Behavior**:
- Devcontainer is started via `devcontainer up`
- Container is running before `LaunchVsCodeAsync()` is called
- VS Code connects to the already-running container

### 3. Docker Context

**Requirement**: VS Code must use the same Docker context as PKS CLI.

**How It Works**:
- PKS CLI uses Docker socket at `/var/run/docker.sock` (or Windows equivalent)
- VS Code uses the same Docker context (from user's environment)
- Both see the same containers

### 4. Cross-Platform Considerations

**Windows**:
- Uses `cmd.exe` wrapper for `code` command
- Quotes paths differently
- Docker socket path: `//./pipe/docker_engine` or WSL path

**Linux/macOS**:
- Direct execution of `code` command
- Docker socket: `/var/run/docker.sock`

**Code**: `DevcontainerSpawnerService.cs:1186-1192`
```csharp
// On Windows, we need different handling for different commands:
// - devcontainer: Run directly - its .cmd wrapper handles bash/Git Bash
// - code/code-insiders: Wrap in cmd.exe for PATH resolution
string actualFileName = fileName;
string actualArguments = arguments;
```

---

## VS Code Connection Process

### What Happens After Launch

1. **VS Code starts** with `--folder-uri` parameter
2. **Dev Containers extension activates** (recognizes `vscode-remote://` URI)
3. **Extension connects to Docker** using local Docker socket
4. **Inspects container** with the decoded container ID
5. **Installs VS Code Server** inside container (if not already present)
6. **Establishes connection** between VS Code UI and VS Code Server
7. **Opens workspace folder** at the specified path

### Environment Variables Set by VS Code

When connected, VS Code sets these environment variables in the container:

```bash
REMOTE_CONTAINERS=true
REMOTE_CONTAINERS_IPC=/tmp/vscode-remote-containers-ipc-05933d97-b7ce-48a7-88c3-9d9bad17b18a.sock
REMOTE_CONTAINERS_SOCKETS=["/tmp/vscode-ssh-auth-...","/tmp/.X11-unix/X0",...]
REMOTE_CONTAINERS_DISPLAY_SOCK=/tmp/.X11-unix/X0
```

These are NOT set by PKS CLI - they're set by VS Code during connection.

### Credential Helper Creation

**Important**: VS Code creates `~/.docker/config.json` with credential helper **AFTER** connection:

```json
{
  "credsStore": "dev-containers-05933d97-b7ce-48a7-88c3-9d9bad17b18a"
}
```

**Timeline**:
1. PKS CLI spawns container
2. postStartCommand runs
3. VS Code connects
4. VS Code creates credential helper ← **This step**

**To Prevent**: Use `"dev.containers.dockerCredentialHelper": false` in template

---

## Troubleshooting

### Issue: VS Code Opens at Wrong Directory

**Symptoms**:
- VS Code opens at root `/` instead of project directory
- File explorer shows system directories (bin, boot, etc.)

**Cause**: `workspaceFolder` in override config was removed

**Solution**: Keep `workspaceFolder` in override config with resolved value
- See: `DevcontainerSpawnerService.cs:1729-1736`

### Issue: VS Code Doesn't Launch

**Check**:
1. Is `code` in PATH?
   ```bash
   which code
   code --version
   ```

2. Is container running?
   ```bash
   docker ps --filter id={containerId}
   ```

3. Check PKS CLI logs for VS Code detection:
   ```
   VS Code detected at: /usr/local/bin/code
   Launching VS Code with URI: vscode-remote://...
   ```

### Issue: Permission Denied in VS Code

**Symptoms**:
- Cannot create files/directories
- Error: `EACCES: permission denied`

**Cause**: Files owned by `root:root`, container runs as `node` (uid=1000)

**Solution**: PKS CLI now automatically fixes ownership
- See: `DevcontainerSpawnerService.cs:1559-1620` (FixFileOwnershipAsync)

### Issue: VS Code Can't Connect to Container

**Symptoms**:
- VS Code shows "Could not connect to container"
- Connection timeout

**Check**:
1. Docker daemon running
2. Container still running (not crashed)
3. VS Code Dev Containers extension installed
4. Docker context matches between PKS CLI and VS Code

---

## Comparison with VS Code Native Behavior

### Native VS Code (Reopen in Container)

**Flow**:
```
1. VS Code reads devcontainer.json
2. Builds/pulls image
3. Starts container with workspace bind-mounted
4. Connects to container
5. Opens workspace
```

**URI Format**: `vscode-remote://dev-container+{hexEncodedWorkspacePath}`

**Key Difference**: Uses workspace path encoding, not container ID

### PKS CLI Approach

**Flow**:
```
1. PKS CLI spawns devcontainer (via devcontainer CLI)
2. Copies files to Docker volume
3. Fixes file ownership
4. Gets running container ID
5. Launches VS Code with container ID URI
```

**URI Format**: `vscode-remote://attached-container+{hexEncodedContainerId}{workspaceFolder}`

**Key Difference**: Uses container ID encoding, workspace in volume

### Why PKS CLI Uses Container ID

**Advantages**:
1. **Works with volumes** - no need for bind mounts
2. **Container already running** - faster connection
3. **Bootstrap workflow** - compatible with bootstrap container approach
4. **Explicit container targeting** - no ambiguity about which container

**Trade-offs**:
- Requires parsing devcontainer CLI output
- Slightly more complex URI construction
- Container must be running before VS Code launch

---

## Code Reference

### Key Methods

| Method | Location | Purpose |
|--------|----------|---------|
| `LaunchVsCodeAsync()` | `DevcontainerSpawnerService.cs:1143` | Launches VS Code process |
| `GetContainerVsCodeUriAsync()` | `DevcontainerSpawnerService.cs:926` | Constructs URI for bootstrap workflow |
| `ConstructVsCodeUri()` | `DevcontainerSpawnerService.cs:1127` | Constructs URI for direct workflow |
| `CheckVsCodeInstallationAsync()` | `DevcontainerSpawnerService.cs:851` | Detects VS Code installation |
| `ParseDevcontainerUpResult()` | `DevcontainerSpawnerService.cs:2213` | Parses devcontainer CLI output |

### Key Properties

| Property | Model | Purpose |
|----------|-------|---------|
| `LaunchVsCode` | `DevcontainerSpawnOptions` | Whether to launch VS Code (default: true) |
| `ContainerId` | `DevcontainerUpResult` | Container ID from devcontainer CLI |
| `RemoteWorkspaceFolder` | `DevcontainerUpResult` | Workspace path inside container |
| `VsCodeUri` | `DevcontainerSpawnResult` | Constructed URI (for debugging) |

---

## Future Enhancements

### Potential Improvements

1. **Custom VS Code executable** - Allow users to specify VS Code path
2. **VS Code arguments** - Support additional VS Code flags (e.g., `--disable-extensions`)
3. **Wait for connection** - Wait for VS Code Server to be ready before returning
4. **Connection validation** - Verify VS Code successfully connected
5. **Alternative editors** - Support other editors with remote capabilities (Cursor, etc.)

### Known Limitations

1. **No connection validation** - PKS CLI doesn't verify VS Code connected successfully
2. **Assumes default Docker context** - Doesn't handle custom Docker contexts
3. **Windows path handling** - May have edge cases with special characters
4. **No retry logic** - If VS Code launch fails, no automatic retry

---

## Summary

### Key Takeaways

1. **PKS CLI uses `attached-container` URI format** with hex-encoded container ID
2. **VS Code is launched after container is running** - not before
3. **Workspace folder must be correct** in override config for proper VS Code opening
4. **Bootstrap workflow is preferred** - better file ownership handling
5. **VS Code creates credential helper after connection** - not during spawn

### Related Documentation

- [DEVCONTAINER_REBUILD.md](DEVCONTAINER_REBUILD.md) - How to rebuild containers and change detection
- [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) - VS Code credential helper behavior
- [DOCKER_CREDENTIALS_ISSUE.md](DOCKER_CREDENTIALS_ISSUE.md) - Issues and solutions
- [DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md](DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md) - Override config investigation

---

*This document reflects the implementation and behavior as of 2026-01-07.*
