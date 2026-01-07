# Docker Credentials & postStartCommand Issue

**Date**: 2026-01-07
**Issue**: Directory nonexistent error in `postStartCommand` when creating `~/.docker/config.json`
**Resolution**: Automatic fix in override config + credential forwarding enabled by default

---

## Problem Description

### The Error

When spawning devcontainers with templates that include this `postStartCommand`:

```json
{
  "postStartCommand": "sudo /usr/local/share/docker-init.sh && echo '{}' > ~/.docker/config.json"
}
```

The command fails with:

```
/bin/sh: 1: cannot create /home/node/.docker/config.json: Directory nonexistent
postStartCommand from devcontainer.json failed with exit code 2
```

### Root Cause

The `postStartCommand` runs **inside the devcontainer** after it starts. At this point:

1. The `~/.docker` directory doesn't exist yet
2. The command tries to write `config.json` directly without creating the parent directory
3. Shell fails with "Directory nonexistent" error

### Why This Works in VS Code

VS Code creates the `~/.docker` directory when it forwards Docker credentials from the host. Since credential forwarding happens **before** the `postStartCommand` runs, the directory exists and the command succeeds.

PKS CLI originally had credential forwarding **disabled by default**, so the directory was never created, causing the error.

---

## Solution

### 1. Automatic postStartCommand Fix (in Override Config)

PKS CLI now automatically fixes the `postStartCommand` in the override config by adding `mkdir -p ~/.docker &&` before any `~/.docker/config.json` operations:

**Original:**
```json
"postStartCommand": "sudo /usr/local/share/docker-init.sh && echo '{}' > ~/.docker/config.json"
```

**Fixed (in override config):**
```json
"postStartCommand": "sudo /usr/local/share/docker-init.sh && mkdir -p ~/.docker && echo '{}' > ~/.docker/config.json"
```

This ensures the directory exists before writing to it, preventing the error even if credential forwarding is disabled.

**Code Location**: `DevcontainerSpawnerService.cs` → `CreateOverrideConfigWithJsonElementAsync()` method

### 2. Docker Credential Forwarding Status

**Current Implementation (v1.2):**
- ❌ Full Docker credential forwarding is **not yet implemented**
- ✅ Empty `~/.docker/config.json` is created by postStartCommand to prevent errors
- ✅ The `--forward-docker-config` flag exists but currently has no effect

**Why Not Fully Implemented:**
- Docker credentials must be written to the user's home directory (`~/.docker`)
- The user's home directory doesn't exist at bootstrap time (before devcontainer starts)
- Writing to `/workspaces/workspace/.docker` (previous approach) created leftover directories in wrong location

**Workarounds for Docker Authentication:**
1. **Mounted Volumes** - Mount host `.docker` config directly in `devcontainer.json`:
   ```json
   "mounts": [
     "source=${localEnv:HOME}/.docker,target=/home/node/.docker,type=bind,consistency=cached"
   ]
   ```

2. **Manual Login** - Run `docker login` inside the devcontainer after it starts

3. **Custom postStartCommand** - Add logic to copy credentials from a mounted location

**Future Enhancement:**
Docker credential forwarding will be implemented in a future release by:
- Copying credentials to a temporary location in the volume
- Having postStartCommand copy them to `~/.docker` after container starts
- Or using Docker's built-in credential helper mounting

**Code Location**: `DevcontainerSpawnerService.cs` → `ForwardDockerCredentialsAsync()` method

---

## Technical Details

### Timeline of Events

**During Devcontainer Spawn:**

1. **Bootstrap container starts** (runs on host Docker)
2. **Source files copied** to Docker volume
3. **Override config created** with:
   - Fixed `postStartCommand` (adds `mkdir -p ~/.docker &&`)
   - Resolved `workspaceFolder` (replaces `${localWorkspaceFolderBasename}` with actual project name)
   - Removed `workspaceMount` (uses `--mount` flag instead)
4. **Devcontainer builds** from Dockerfile/image
5. **Devcontainer starts** with volume mounted at `/workspaces`
6. **postStartCommand runs** inside devcontainer
   - Creates `~/.docker` directory (from override config fix)
   - Creates empty `~/.docker/config.json` to prevent errors
   - Runs other initialization commands

### How the Fix Works

**postStartCommand Fix**: Automatically adds `mkdir -p ~/.docker &&` before any `~/.docker/config.json` operations
- Ensures directory exists in the correct location (user's home directory)
- Prevents "Directory nonexistent" errors
- Works even without credential forwarding

**workspaceFolder Fix**: Resolves template variables to actual paths
- Template: `"workspaceFolder": "/workspaces/${localWorkspaceFolderBasename}"`
- Override: `"workspaceFolder": "/workspaces/test666"` (actual project name)
- Ensures VS Code opens in the correct directory instead of root `/`

Together, these fixes ensure the spawn succeeds and VS Code opens correctly in all scenarios:
- ✅ VS Code opens at `/workspaces/{projectName}` (not root `/`)
- ✅ No leftover `/workspaces/workspace/.docker` directories
- ✅ postStartCommand completes successfully

---

## VS Code Comparison

| Aspect | VS Code | PKS CLI (Before) | PKS CLI (After v1.2) |
|--------|---------|------------------|----------------------|
| **Mount Point** | `/workspaces/{projectName}` | ❌ Root `/` | ✅ `/workspaces/{projectName}` |
| **.docker Directory** | Created by credential forwarding | ❌ Not created | ✅ Created by postStartCommand |
| **postStartCommand** | Succeeds (dir exists) | ❌ Fails (no dir) | ✅ Succeeds (auto-fixed) |
| **Credential Forwarding** | ✅ Copies host credentials | ❌ Not implemented | ⚠️ Not yet implemented* |
| **Private Registries** | Works by default | ❌ Requires manual setup | ⚠️ Requires manual setup* |
| **Leftover Directories** | None | ❌ `/workspaces/workspace/.docker` | ✅ None |

\* Full credential forwarding planned for future release. Use mounted volumes as workaround.

---

## Related Files

**Core Implementation:**
- `src/Infrastructure/Services/DevcontainerSpawnerService.cs` - Override config generation with postStartCommand fix
- `src/Infrastructure/Services/Models/DevcontainerModels.cs` - `ForwardDockerConfig` default value
- `src/Commands/Devcontainer/DevcontainerSpawnCommand.cs` - Command-line flag handling

**Templates:**
- `templates/pks-fullstack/content/.devcontainer/devcontainer.json` - Example with problematic postStartCommand
- `templates/pks-fullstack/content/.devcontainer/CLAUDE.md` - Firewall documentation (related context)

**Documentation:**
- `docs/DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md` - Override config investigation
- `docs/DOCKER_CREDENTIALS_ISSUE.md` - This document

---

## Testing

To verify the fix works:

```bash
# Test with default behavior (forwarding enabled)
pks devcontainer spawn /path/to/test-project --volume-name test-vol-1

# Test with forwarding explicitly disabled
pks devcontainer spawn /path/to/test-project --volume-name test-vol-2 --no-forward-docker-config

# Check that devcontainer starts successfully in both cases
docker ps | grep test-vol

# Verify .docker directory exists inside devcontainer
docker exec <container-id> ls -la ~/.docker
```

**Expected Results:**
- ✅ Both scenarios succeed without "Directory nonexistent" error
- ✅ `~/.docker/config.json` exists in devcontainer
- ✅ `postStartCommand` completes successfully

---

## Migration Notes

### For Users

**No action required** - the fix is automatic and improves the default behavior.

If you previously used `--forward-docker-config` explicitly, you can now omit it (it's the default).

If you don't want credential forwarding, use `--no-forward-docker-config`.

### For Template Authors

**No changes needed** to existing templates. The override config automatically fixes the `postStartCommand`.

However, if you're creating new templates, consider:

**Best Practice:**
```json
{
  "postStartCommand": "sudo /usr/local/share/docker-init.sh && mkdir -p ~/.docker && echo '{}' > ~/.docker/config.json"
}
```

**Why:** Explicit directory creation makes the template more robust, though PKS CLI now handles this automatically.

---

## Future Considerations

### Potential Improvements

1. **Mount host .docker as volume** - Avoid copying by mounting directly (like VS Code does for some scenarios)
2. **Selective credential forwarding** - Allow specifying which credentials to forward (per-registry)
3. **Credential encryption** - Encrypt credentials in transit/storage for added security

### Known Limitations

1. **Credential updates** - If host Docker credentials change, devcontainer needs rebuild to pick up changes
2. **Windows path handling** - Uses `Path.GetTempPath()` which handles cross-platform paths correctly
3. **Concurrent spawns** - Same project concurrent spawns safe (unique volume names prevent conflicts)

---

*This document reflects the investigation and resolution as of 2026-01-07.*
