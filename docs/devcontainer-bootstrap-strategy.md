# PKS CLI Bootstrap Container Strategy

## Problem
Windows `cmd.exe` cannot execute bash scripts in `initializeCommand`. VS Code solves this by running the entire devcontainer build process inside a Linux bootstrap container.

## VS Code's Approach

1. **Build Bootstrap Container** (Alpine Linux with devcontainer CLI)
2. **Run Bootstrap Container** with volume mounted: `docker run -d --mount type=volume,src=<volume>,dst=/workspaces vsc-volume-bootstrap sleep infinity`
3. **Execute Inside Bootstrap**:
   - Clone repository (if needed)
   - Run devcontainer CLI from within container
   - `initializeCommand` executes with `/bin/sh` (Linux)
4. **Build Actual DevContainer** from within bootstrap
5. **Exit Bootstrap**, connect to actual container

## PKS CLI Implementation

### Bootstrap Container Image

Create `bootstrap.Dockerfile`:
```dockerfile
FROM mcr.microsoft.com/devcontainers/base:0-alpine-3.20

# Install essential tools
RUN apk add --no-cache \
    bash \
    git \
    nodejs \
    npm \
    docker-cli \
    docker-cli-buildx \
    docker-cli-compose

# Install devcontainer CLI globally
RUN npm install -g @devcontainers/cli

# Set working directory
WORKDIR /workspaces
```

### Workflow Changes

**Current** (Windows fails with bash):
```
Windows → devcontainer up → cmd.exe /c bash-script → FAILS
```

**New** (Cross-platform bootstrap):
```
Any OS → Start Bootstrap Container
      → Copy files to volume
      → Run devcontainer CLI in container → /bin/sh -c bash-script → SUCCESS
      → Build actual devcontainer
      → Exit bootstrap
      → Connect to devcontainer
```

### Code Changes

**DevcontainerSpawnerService.cs**:

```csharp
public async Task<DevcontainerSpawnResult> SpawnLocalAsync(DevcontainerSpawnOptions options)
{
    // Use bootstrap container approach for all platforms
    // This ensures consistent behavior and bash support everywhere
    return await SpawnWithBootstrapContainerAsync(options);
}

private async Task<DevcontainerSpawnResult> SpawnWithBootstrapContainerAsync(DevcontainerSpawnOptions options)
{
    try
    {
        // 1. Build/pull bootstrap image
        await EnsureBootstrapImageAsync();

        // 2. Create volume
        var volumeName = options.VolumeName;
        await CreateVolumeAsync(volumeName, options.ProjectName);

        // 3. Start bootstrap container
        var bootstrapContainerId = await StartBootstrapContainerAsync(volumeName);

        // 4. Copy files to volume via bootstrap container
        await CopyFilesToVolumeAsync(bootstrapContainerId, options.ProjectPath, "/workspaces/" + options.ProjectName);

        // 5. Run devcontainer up INSIDE bootstrap container
        var result = await RunDevcontainerUpInBootstrapAsync(
            bootstrapContainerId,
            $"/workspaces/{options.ProjectName}",
            options.ProjectName
        );

        // 6. Cleanup bootstrap container
        await StopAndRemoveContainerAsync(bootstrapContainerId);

        // 7. Launch VS Code to actual devcontainer
        if (options.LaunchVsCode)
        {
            await LaunchVsCodeAsync(result.ContainerId, result.WorkspaceFolder);
        }

        return result;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to spawn devcontainer with bootstrap");
        // Cleanup on failure
        throw;
    }
}

private async Task EnsureBootstrapImageAsync()
{
    // Check if image exists
    var imageExists = await ImageExistsAsync("pks-devcontainer-bootstrap:latest");

    if (!imageExists)
    {
        _logger.LogInformation("Building PKS bootstrap container image...");

        // Build from embedded Dockerfile or pull from registry
        await BuildBootstrapImageAsync();
    }
}

private async Task<string> StartBootstrapContainerAsync(string volumeName)
{
    var args = new[]
    {
        "run", "-d",
        "--mount", $"type=volume,src={volumeName},dst=/workspaces",
        "-v", "/var/run/docker.sock:/var/run/docker.sock", // Docker-in-Docker
        "--security-opt", "label=disable",
        "pks-devcontainer-bootstrap:latest",
        "sleep", "infinity"
    };

    var output = await RunCommandAsync("docker", string.Join(" ", args));
    return output.Trim(); // Container ID
}

private async Task CopyFilesToVolumeAsync(string containerId, string sourcePath, string destPath)
{
    // Use docker cp to copy files into running container
    await RunCommandAsync("docker", $"cp \"{sourcePath}/.\" {containerId}:{destPath}");
}

private async Task<DevcontainerSpawnResult> RunDevcontainerUpInBootstrapAsync(
    string bootstrapContainerId,
    string workspaceFolder,
    string projectName)
{
    // Execute devcontainer up inside the bootstrap container
    var devcontainerCommand = $"devcontainer up --workspace-folder {workspaceFolder}";

    var output = await RunCommandAsync(
        "docker",
        $"exec {bootstrapContainerId} /bin/sh -c \"{devcontainerCommand}\""
    );

    // Parse JSON output from devcontainer CLI
    var result = ParseDevcontainerUpOutput(output);

    return new DevcontainerSpawnResult
    {
        Success = true,
        ContainerId = result.ContainerId,
        VolumeName = result.VolumeName,
        WorkspaceFolder = workspaceFolder,
        VsCodeUri = GenerateVsCodeUri(result.ContainerId, workspaceFolder)
    };
}
```

## Benefits

1. **Cross-Platform**: Works identically on Windows, macOS, Linux
2. **Bash Support**: `initializeCommand` with bash syntax works everywhere
3. **Consistent with VS Code**: Users get same behavior in both tools
4. **Docker-in-Docker**: Bootstrap container can build images using host Docker
5. **No Host Dependencies**: Only requires Docker, no bash/WSL on Windows

## Testing Strategy

1. **Windows**: Test bash scripts in `initializeCommand`
2. **macOS**: Verify no regression
3. **Linux**: Verify no regression
4. **Complex Scripts**: Test heredocs, pipes, conditionals
5. **Large Projects**: Test performance with many files

## Migration Path

**Phase 1**: Implement bootstrap approach (new feature)
**Phase 2**: Make it default for all platforms (breaking change communicated)
**Phase 3**: Remove old direct execution path

## Performance Considerations

- **Bootstrap Image Build**: ~30 seconds first time, cached afterwards
- **Container Startup**: ~1-2 seconds
- **File Copy**: Depends on project size (use `.dockerignore` to exclude node_modules, etc.)
- **devcontainer up**: Same as before (runs inside container)

**Total Overhead**: ~5-10 seconds for small projects, ~30 seconds first run

## Future Enhancements

1. **Pre-built Bootstrap Images**: Publish to Docker Hub/GitHub Container Registry
2. **Optimized File Copy**: Use tar streams for faster transfer
3. **Persistent Bootstrap**: Keep bootstrap container running for multiple operations
4. **Custom Bootstrap Images**: Allow users to specify their own bootstrap image
