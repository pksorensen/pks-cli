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

### 2. Docker Credential Forwarding Now Default

To match VS Code behavior, Docker credential forwarding is now **enabled by default**:

**Default Behavior:**
- ✅ Forward Docker credentials from host to devcontainer (matches VS Code)
- ✅ Creates `~/.docker` directory with proper permissions
- ✅ Enables private registry access without additional configuration

**Command-Line Flags:**
```bash
# Default (credential forwarding enabled)
pks devcontainer spawn /path/to/project

# Explicitly enable (redundant, already default)
pks devcontainer spawn /path/to/project --forward-docker-config

# Disable credential forwarding
pks devcontainer spawn /path/to/project --no-forward-docker-config

# Use custom Docker config location
pks devcontainer spawn /path/to/project --docker-config-path /custom/path/config.json
```

**Code Location**: `DevcontainerModels.cs` → `ForwardDockerConfig` property (default: `true`)

---

## Technical Details

### Timeline of Events

**During Devcontainer Spawn:**

1. **Bootstrap container starts** (runs on host Docker)
2. **Source files copied** to Docker volume
3. **Override config created** with fixed `postStartCommand`
4. **Docker credentials forwarded** (if enabled, now default)
   - Creates `/workspaces/workspace/.docker/config.json` in volume
   - This happens **before** devcontainer starts
5. **Devcontainer builds** from Dockerfile/image
6. **Devcontainer starts** with volume mounted
7. **postStartCommand runs** inside devcontainer
   - Now has `mkdir -p ~/.docker &&` prefix (from override config)
   - Directory creation succeeds
   - `echo '{}' > ~/.docker/config.json` succeeds

### Why Both Fixes Are Needed

**Fix #1 (postStartCommand)**: Safety net - ensures directory exists even if forwarding is disabled

**Fix #2 (Default forwarding)**: Matches VS Code behavior and provides better out-of-box experience

Together, these fixes ensure the spawn succeeds in all scenarios:
- ✅ With credential forwarding (default)
- ✅ With `--no-forward-docker-config`
- ✅ With invalid/missing host Docker config

---

## VS Code Comparison

| Aspect | VS Code | PKS CLI (Before) | PKS CLI (After) |
|--------|---------|------------------|-----------------|
| **Default Behavior** | Forward credentials | No forwarding | ✅ Forward credentials |
| **.docker Directory** | Created by forwarding | Not created | ✅ Created by forwarding |
| **postStartCommand** | Succeeds (dir exists) | ❌ Fails (no dir) | ✅ Succeeds (fixed command) |
| **Private Registries** | Works by default | ❌ Requires flag | ✅ Works by default |
| **Disable Option** | N/A (always on) | Default behavior | `--no-forward-docker-config` |

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
