# Devcontainer Rebuild in PKS CLI vs VS Code

**Date**: 2026-01-07
**Purpose**: Document how to rebuild devcontainers and how VS Code detects configuration changes

---

## TL;DR

### PKS CLI Rebuild

**Currently**: ❌ No dedicated rebuild command

**Workaround**: Spawn a new container with `--force` flag:
```bash
pks devcontainer spawn /path/to/project --force --volume-name same-volume-name
```

**Future**: 🔄 Rebuild command planned (see Future Enhancements)

### VS Code Rebuild

**How it works**: VS Code watches `.devcontainer/devcontainer.json` and prompts to rebuild when changes are detected.

**Mechanism**: File system watcher + configuration hash comparison

---

## Current PKS CLI Approach

### No Rebuild Command Yet

PKS CLI **does not have a dedicated rebuild command** as of v1.2.

**Available commands**:
- `pks devcontainer spawn` - Spawn new container
- `pks devcontainer list` - List containers
- `pks devcontainer containers` - Show container details
- `pks devcontainer connect` - Connect to existing container
- `pks devcontainer validate` - Validate devcontainer.json

**Missing**: `pks devcontainer rebuild`

### Workaround: Spawn with --force

If you need to rebuild after changing `devcontainer.json`, use the spawn command with `--force`:

```bash
# Original spawn
pks devcontainer spawn /path/to/project --volume-name my-project-vol

# After changing devcontainer.json, rebuild by forcing new spawn
pks devcontainer spawn /path/to/project --volume-name my-project-vol --force
```

**What happens**:
1. PKS CLI asks: "Existing container found. Create a new container anyway?"
2. If you say yes (or use `--force` to skip prompt):
   - New container is created
   - Old container is left running (not removed)
   - New container gets new container ID but same volume name

**Trade-offs**:
- ❌ Old container left behind (manual cleanup needed)
- ❌ Need to remember volume name
- ✅ Files preserved in volume
- ✅ Can compare old vs new container

### Manual Cleanup

After forcing a new spawn, you'll have multiple containers:

```bash
# List containers for the project
pks devcontainer list

# Or use docker directly
docker ps -a --filter label=devcontainer.local.folder=my-project

# Remove old container
docker rm -f <old-container-id>
```

---

## How VS Code Detects Configuration Changes

### Change Detection Mechanism

VS Code's Dev Containers extension uses **file system watchers** to monitor `.devcontainer/devcontainer.json` for changes.

**Process**:
1. **VS Code connects** to container
2. **File watcher established** on `.devcontainer/` directory
3. **Configuration read** and hash computed
4. **User edits** `devcontainer.json`
5. **File watcher fires** change event
6. **Configuration re-read** and new hash computed
7. **Hashes compared** - if different, prompt to rebuild
8. **User clicks "Rebuild"** - VS Code runs rebuild process

### Evidence from Logs

From the VS Code logs, we can see:

**1. Configuration Tracking via Labels**:
```
--id-label devcontainer.config_file=/workspaces/bankdata/.devcontainer/devcontainer.json
```

VS Code labels containers with the config file path, allowing it to track which config was used to build each container.

**2. Configuration Reading**:
```
Start: Run in container: cat /workspaces/bankdata/.devcontainer/devcontainer.json
```

VS Code reads the configuration file multiple times during connection to detect changes.

**3. File Watcher Environment Variable**:
```
"DOTNET_USE_POLLING_FILE_WATCHER": "true"
```

VS Code sets file watcher environment variables, indicating it uses file system watching.

### What VS Code Watches

**Primary files**:
- `.devcontainer/devcontainer.json` - Main configuration
- `.devcontainer/Dockerfile` - Build instructions (if referenced)
- `.devcontainer/docker-compose.yml` - Compose configuration (if used)

**Indirect files**:
- Any files referenced by `"dockerfile"` or `"dockerComposeFile"` properties

**NOT watched** (require manual rebuild):
- Changes inside the Dockerfile that don't change the file itself
- Changes to build context files (need explicit rebuild)
- Changes to base images (need rebuild with `--no-cache`)

### Rebuild Prompt Triggers

VS Code shows "Rebuild Container" prompt when:

1. **devcontainer.json modified** - Any change to configuration
2. **Dockerfile modified** - If referenced in devcontainer.json
3. **docker-compose.yml modified** - If using compose
4. **Feature versions changed** - Updated feature references
5. **Extension recommendations changed** - New extensions listed

**Does NOT trigger automatic prompt**:
- Changes inside running container (files in workspace)
- Changes to environment variables at runtime
- Changes to mounted volumes
- Changes to running processes

---

## VS Code Rebuild Process

### What "Rebuild Container" Does

When you click "Rebuild Container" in VS Code:

**Full Rebuild**:
1. **Stops container** (gracefully)
2. **Removes container** (keeps volumes)
3. **Rebuilds image** (if Dockerfile changed)
4. **Creates new container** with updated configuration
5. **Starts container**
6. **Reconnects VS Code**

**Rebuild vs Restart**:
- **Rebuild** - Full teardown and rebuild (slow, thorough)
- **Restart** - Just restarts container (fast, no config changes)

### VS Code Rebuild Command

From VS Code Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`):

```
Dev Containers: Rebuild Container
Dev Containers: Rebuild Container Without Cache
Dev Containers: Rebuild and Reopen in Container
```

**Options**:
- **Rebuild Container** - Rebuild with cache
- **Without Cache** - Fresh build (no cached layers)
- **Rebuild and Reopen** - Rebuild + reconnect VS Code

---

## Implementing Rebuild in PKS CLI

### Proposed Design

**Command**: `pks devcontainer rebuild`

**Behavior**:
1. **Find existing container** by project path or volume name
2. **Read current devcontainer.json** from container
3. **Compare with local devcontainer.json** (detect changes)
4. **Stop container** gracefully
5. **Remove container** (keep volume)
6. **Run devcontainer up** with updated configuration
7. **Launch VS Code** (if `--launch-vscode` flag, default: true)

### Command Syntax

```bash
# Rebuild current directory's devcontainer
pks devcontainer rebuild

# Rebuild specific project
pks devcontainer rebuild /path/to/project

# Rebuild without cache (fresh build)
pks devcontainer rebuild --no-cache

# Rebuild but don't launch VS Code
pks devcontainer rebuild --no-launch-vscode

# Force rebuild even if no changes detected
pks devcontainer rebuild --force
```

### Implementation Approach

**Option 1: Use devcontainer CLI rebuild** (Recommended)

```bash
devcontainer up --workspace-folder /path/to/project --remove-existing-container
```

**Pros**:
- ✅ Leverages devcontainer CLI's rebuild logic
- ✅ Handles all edge cases (features, cache, etc.)
- ✅ Maintains compatibility with VS Code

**Cons**:
- ⚠️ Requires devcontainer CLI to support rebuild (it does)
- ⚠️ Less control over rebuild process

**Option 2: Manual rebuild orchestration**

```csharp
1. Find container (by project path or volume name)
2. docker stop <container-id>
3. docker rm <container-id>
4. docker rmi <image-id> (if --no-cache)
5. devcontainer up (spawn new container)
```

**Pros**:
- ✅ Full control over each step
- ✅ Can add custom logic (cleanup, validation, etc.)
- ✅ Better progress reporting

**Cons**:
- ❌ More code to maintain
- ❌ Risk of missing edge cases
- ❌ Harder to keep in sync with VS Code behavior

### Code Structure

**New files**:
- `src/Commands/Devcontainer/DevcontainerRebuildCommand.cs` - Rebuild command
- `src/Infrastructure/Services/IDevcontainerSpawnerService.cs` - Add `RebuildAsync()` method

**Modified files**:
- `src/Infrastructure/Services/DevcontainerSpawnerService.cs` - Implement rebuild logic
- `src/Program.cs` - Register rebuild command

**Example implementation**:

```csharp
public class DevcontainerRebuildCommand : DevcontainerCommand<DevcontainerRebuildCommand.Settings>
{
    public class Settings : DevcontainerSettings
    {
        [CommandArgument(0, "[PROJECT_PATH]")]
        [Description("Path to project directory (defaults to current directory)")]
        public string? ProjectPath { get; set; }

        [CommandOption("--no-cache")]
        [Description("Rebuild without using cached layers")]
        public bool NoCache { get; set; }

        [CommandOption("--no-launch-vscode")]
        [Description("Don't automatically launch VS Code after rebuild")]
        public bool NoLaunchVsCode { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // 1. Find existing container
        var existing = await _spawnerService.FindExistingContainerAsync(projectPath);

        if (existing == null)
        {
            DisplayError("No existing container found. Use 'pks devcontainer spawn' instead.");
            return 1;
        }

        // 2. Stop container
        await _spawnerService.StopContainerAsync(existing.ContainerId);

        // 3. Remove container (keep volume)
        await _spawnerService.RemoveContainerAsync(existing.ContainerId, keepVolume: true);

        // 4. Rebuild (spawn with same volume)
        var result = await _spawnerService.SpawnLocalAsync(new DevcontainerSpawnOptions
        {
            ProjectPath = projectPath,
            VolumeName = existing.VolumeName,
            LaunchVsCode = !settings.NoLaunchVsCode,
            NoCache = settings.NoCache
        });

        return result.Success ? 0 : 1;
    }
}
```

---

## Comparison: PKS CLI vs VS Code

### Feature Matrix

| Feature | VS Code | PKS CLI (Current) | PKS CLI (Proposed) |
|---------|---------|-------------------|--------------------|
| **Rebuild Command** | ✅ Yes | ❌ No | 🔄 Planned |
| **Change Detection** | ✅ Automatic (file watcher) | ❌ No | 🔄 Planned |
| **Rebuild Prompt** | ✅ Yes | ❌ No | 🔄 Planned |
| **Stop + Remove** | ✅ Yes | ⚠️ Manual | ✅ Yes |
| **Volume Preservation** | ✅ Yes | ✅ Yes | ✅ Yes |
| **No Cache Option** | ✅ Yes | ❌ No | ✅ Yes |
| **VS Code Reconnect** | ✅ Automatic | ❌ Manual | ✅ Automatic |

### Workaround Comparison

**Today (No rebuild command)**:
```bash
# Manual rebuild process
pks devcontainer spawn /path/to/project --force --volume-name existing-vol
docker rm -f old-container-id  # Manual cleanup
```

**Future (With rebuild command)**:
```bash
# Automated rebuild process
pks devcontainer rebuild /path/to/project
# Handles everything: stop, remove, rebuild, reconnect
```

---

## Best Practices

### When to Rebuild

**Always rebuild when**:
- Changed `devcontainer.json` configuration
- Changed `Dockerfile` referenced by devcontainer
- Changed feature versions or added new features
- Changed extension recommendations
- Changed environment variables in devcontainer.json

**May not need rebuild**:
- Changed files in workspace (changes already in container)
- Changed mounted volumes (remount on restart)
- Changed runtime environment variables (can set manually)

### Rebuild vs Restart vs Recreate

**Restart** (Fast):
- Container stops and starts
- No rebuilding of image
- Configuration unchanged
- Use when: Debugging restart issues

**Rebuild** (Medium):
- Container removed and recreated
- Image rebuilt if Dockerfile changed
- Configuration updated
- Use when: Changed devcontainer.json

**Rebuild without cache** (Slow):
- Full rebuild from scratch
- No cached Docker layers
- Fresh pull of base images
- Use when: Debugging build issues, want latest base image

---

## Future Enhancements

### Planned Features

1. **`pks devcontainer rebuild` command** - Automated rebuild process
2. **Change detection** - Compare current config with container config
3. **Rebuild confirmation** - Prompt before rebuilding (unless `--force`)
4. **Progress reporting** - Show rebuild progress with steps
5. **Cleanup options** - Remove old containers automatically

### Advanced Features (Future)

1. **Configuration diff** - Show what changed between configs
2. **Selective rebuild** - Rebuild only if specific properties changed
3. **Parallel rebuild** - Rebuild multiple projects concurrently
4. **Rebuild strategies** - Fast rebuild, full rebuild, incremental rebuild
5. **Rebuild history** - Track rebuild operations and timing

---

## Troubleshooting

### Issue: "No existing container found"

**Cause**: Container was removed or never spawned

**Solution**: Use `pks devcontainer spawn` instead of rebuild

### Issue: "Rebuild failed with exit code 1"

**Cause**: Error in devcontainer.json or Dockerfile

**Solution**:
1. Check syntax of devcontainer.json
2. Run `pks devcontainer validate` to find errors
3. Check Docker build logs for Dockerfile issues

### Issue: "Files lost after rebuild"

**Cause**: Volume not preserved or wrong volume used

**Solution**: Ensure `--volume-name` matches existing volume

---

## Summary

### Current State (v1.2)

- ❌ No dedicated rebuild command
- ⚠️ Workaround: `pks devcontainer spawn --force`
- ⚠️ Manual cleanup required

### VS Code Behavior

- ✅ File watcher detects changes
- ✅ Prompts to rebuild automatically
- ✅ Full rebuild process handled

### Recommended Approach Today

```bash
# 1. Make changes to devcontainer.json

# 2. Find your volume name
pks devcontainer list

# 3. Force respawn with same volume
pks devcontainer spawn /path/to/project --force --volume-name your-volume-name

# 4. Clean up old container
docker ps -a  # Find old container ID
docker rm -f old-container-id
```

### Coming Soon

```bash
# Simple rebuild (planned)
pks devcontainer rebuild

# Rebuild without cache (planned)
pks devcontainer rebuild --no-cache
```

---

## Related Documentation

- [VSCODE_LAUNCHING.md](VSCODE_LAUNCHING.md) - How PKS CLI launches VS Code
- [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) - Docker credential behavior
- [DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md](DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md) - Override config details

---

*This document reflects the current state and planned enhancements as of 2026-01-07.*
