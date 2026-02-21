# Docker Credentials & postStartCommand Issue

**Date**: 2026-01-07 (Updated: 2026-01-07)
**Issue**: Permission denied errors and misunderstanding about Docker credential forwarding
**Resolution**: File ownership fixes + documented VS Code credential forwarding behavior

**See also**: [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) for complete analysis

---

## Problem Description

### Issue 1: VS Code Opens at Wrong Location

When spawning devcontainers, VS Code opened at root `/` instead of `/workspaces/{projectName}`.

**Root Cause**: Override config was removing the `workspaceFolder` property, causing devcontainer CLI to default to `/`.

### Issue 2: Permission Denied Errors

When trying to create files in the spawned devcontainer:

```
Error: EACCES: permission denied, mkdir '/workspaces/test777/src'
Error: EACCES: permission denied, open '/workspaces/test777/.devcontainer/devcontainer.json'
```

**Root Cause**: Files copied by `docker cp` were owned by `root:root`, but the devcontainer runs as user `node` (uid=1000).

### Issue 3: Misunderstanding Docker Credential Forwarding

Initial assumption: VS Code copies host Docker credentials by default, we should do the same.

**Actual truth**: VS Code uses a special IPC-based credential helper that:
- Only works within VS Code terminal (not with `docker exec`)
- Requires the VS Code extension
- Cannot be replicated by other tools

See [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) for complete analysis.

### Issue 4: Ineffective postStartCommand Security Wipe

**Discovery**: Template's `echo '{}' > ~/.docker/config.json` in postStartCommand was intended to wipe VS Code's credential helper for security.

**Problem**: VS Code creates/overwrites `~/.docker/config.json` **AFTER** postStartCommand completes, making the wipe ineffective.

**Timeline**:
1. postStartCommand runs: `echo '{}' > ~/.docker/config.json` ✅
2. VS Code connects
3. VS Code overwrites: `{"credsStore": "dev-containers-xxx"}` ❌

**Solution**: Use `"dev.containers.dockerCredentialHelper": false` setting instead of postStartCommand wipe.

---

## Solution

### 1. Fix workspaceFolder Resolution

**Problem**: Override config was removing `workspaceFolder`, causing VS Code to open at root `/`.

**Solution**: Resolve the `${localWorkspaceFolderBasename}` variable to actual project name in override config:

```json
"workspaceFolder": "/workspaces/test777"
```

**Code Location**: `DevcontainerSpawnerService.cs:1729-1736` → `CreateOverrideConfigWithJsonElementAsync()` method

### 2. Fix File Ownership (Permission Denied)

**Problem**: Files copied by `docker cp` were owned by `root:root`, but devcontainer runs as `node` (uid=1000).

**Solution**: Added `FixFileOwnershipAsync()` method that:
1. Reads `remoteUser` from devcontainer.json (e.g., "node")
2. Maps user to UID:GID (node → 1000:1000, vscode → 1000:1000, root → 0:0)
3. Runs `chown -R 1000:1000 /workspaces/{projectName}` in bootstrap container

**Code Location**:
- `DevcontainerSpawnerService.cs:1537` → Call in `CopyFilesToBootstrapVolumeAsync()`
- `DevcontainerSpawnerService.cs:1559-1620` → `FixFileOwnershipAsync()` implementation

### 3. Removed postStartCommand Modification

**Problem**: We were automatically modifying templates' postStartCommand to add `mkdir -p ~/.docker &&`.

**Why This Was Wrong**: Template authors control their configuration. We shouldn't modify it.

**Solution**: Removed the automatic modification. Templates should handle their own requirements.

**Code Location**: `DevcontainerSpawnerService.cs:1738-1759` → Removed code block

### 4. Fix Ineffective postStartCommand Security Wipe

**Problem**: Template was trying to wipe VS Code's credential helper with `echo '{}' > ~/.docker/config.json` in postStartCommand, but VS Code overwrites it after connecting.

**Solution**:
1. **Removed** ineffective `echo '{}' > ~/.docker/config.json` from postStartCommand
2. **Added** proper security setting to template:
   ```json
   "dev.containers.dockerCredentialHelper": false
   ```
3. **Updated** template documentation to explain the security measure

**Code Location**: `templates/pks-fullstack/content/.devcontainer/devcontainer.json`
- Line 61: Added `"dev.containers.dockerCredentialHelper": false`
- Line 82: Removed `&& echo '{}' > ~/.docker/config.json` from postStartCommand

### 5. Docker Credential Forwarding - No Implementation

**Decision**: Do NOT implement automatic Docker credential forwarding.

**Why**:
- VS Code uses a special IPC-based credential helper (not file copying)
- We cannot replicate this without the VS Code extension
- Template authors should choose their credential strategy
- Volume mounts are superior for this use case

**Recommendation**: Use volume mounts in templates:
```json
"mounts": [
  "source=${localEnv:HOME}/.docker,target=/home/node/.docker,type=bind,consistency=cached"
]
```

**See**: [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) for complete analysis and alternatives.

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
| **File Ownership** | Correct (VS Code handles) | ❌ root:root | ✅ Correct (chown to remoteUser) |
| **postStartCommand** | Not modified | ❌ Auto-modified | ✅ Not modified |
| **Credential Forwarding** | ✅ IPC-based helper (default enabled) | ❌ Not implemented | ✅ Not implemented (by design)* |
| **Credential Helper Security** | Can be disabled via setting | ⚠️ Ineffective postStartCommand wipe | ✅ Disabled via setting** |
| **Private Registries** | Works in VS Code terminal only*** | ❌ Requires setup | ✅ Template-controlled**** |
| **Template Respect** | ✅ Not modified | ❌ Auto-modified postStartCommand | ✅ Not modified |

\* See [DOCKER_CREDENTIAL_FORWARDING.md](DOCKER_CREDENTIAL_FORWARDING.md) - we don't replicate VS Code's IPC approach
\*\* pks-fullstack template uses `"dev.containers.dockerCredentialHelper": false` for security
\*\*\* VS Code's credential helper only works in VS Code terminal, not with `docker exec` or SSH
\*\*\*\* Templates can use volume mounts for Docker authentication (works everywhere)

---

## Related Files

**Core Implementation:**
- `src/Infrastructure/Services/DevcontainerSpawnerService.cs` - Override config generation, file ownership fix
  - Lines 1729-1736: workspaceFolder resolution
  - Lines 1537: File ownership fix call
  - Lines 1559-1620: `FixFileOwnershipAsync()` implementation
- `src/Infrastructure/Services/Models/DevcontainerModels.cs` - `ForwardDockerConfig` property (unused)
- `src/Commands/Devcontainer/DevcontainerSpawnCommand.cs` - Command-line flag handling

**Templates:**
- `templates/pks-fullstack/content/.devcontainer/devcontainer.json` - Example template
- `templates/pks-fullstack/content/.devcontainer/CLAUDE.md` - Firewall documentation

**Documentation:**
- `docs/DOCKER_CREDENTIAL_FORWARDING.md` - **Complete analysis of Docker credential forwarding**
- `docs/DOCKER_CREDENTIALS_ISSUE.md` - This document (issue tracking and solutions)
- `docs/DEVCONTAINER_OVERRIDE_CONFIG_ANALYSIS.md` - Override config investigation

---

## Testing

To verify the fixes work:

```bash
# Spawn a new devcontainer
pks devcontainer spawn /path/to/project --volume-name test-fix-vol

# After spawn completes and VS Code opens, check inside the container:
docker exec $(docker ps -q --filter "label=devcontainer.local.volume=test-fix-vol") bash -c "
  echo 'Current user:' && whoami &&
  echo 'File ownership:' && ls -la /workspaces/test-project &&
  echo 'Working directory in VS Code:' && pwd
"

# Try creating a file inside VS Code to verify permissions
# (Open terminal in VS Code and run):
touch /workspaces/test-project/test-file.txt
mkdir /workspaces/test-project/test-dir
```

**Expected Results:**
- ✅ VS Code opens at `/workspaces/{projectName}` (not root `/`)
- ✅ Files owned by `node:node` (1000:1000), not `root:root`
- ✅ Can create/edit files without permission errors
- ✅ postStartCommand completes successfully
- ✅ No `/workspaces/workspace/.docker` leftover directories

---

## Migration Notes

### For Users

**Action Required for v1.2:**

1. **File Permissions Fixed**: Re-spawn existing devcontainers to get correct file ownership
   - Old containers have files owned by `root:root`
   - New containers will have files owned correctly (e.g., `node:node`)

2. **postStartCommand Not Modified**: Templates' postStartCommand is respected
   - If your template has issues, fix the template (don't rely on auto-fixes)
   - Templates should create required directories themselves

3. **Docker Credentials**: Use volume mounts if you need Docker authentication
   ```json
   "mounts": [
     "source=${localEnv:HOME}/.docker,target=/home/node/.docker,type=bind,consistency=cached"
   ]
   ```

### For Template Authors

**Best Practices for v1.2:**

1. **Create directories explicitly** in postStartCommand:
   ```json
   "postStartCommand": "mkdir -p ~/.docker && echo '{}' > ~/.docker/config.json"
   ```

2. **Use volume mounts** for Docker authentication:
   ```json
   "mounts": [
     "source=${localEnv:HOME}/.docker,target=/home/node/.docker,type=bind,consistency=cached"
   ]
   ```

3. **Test with `docker exec`**: Don't rely on VS Code-specific features like IPC credential helpers

4. **Don't expect auto-fixes**: PKS CLI respects your template as-is

---

## Future Considerations

### Decisions Made

1. **No automatic credential forwarding** - Templates should use volume mounts
2. **No postStartCommand modification** - Respect template authors' intentions
3. **File ownership always fixed** - Bootstrap container chowns files to remoteUser

### Known Limitations

1. **VS Code IPC credential helper** - Cannot be replicated by PKS CLI (requires VS Code extension)
2. **File ownership detection** - Uses hardcoded UID mapping (1000:1000 for common users)
3. **Concurrent spawns** - Safe with unique volume names
4. **Cross-platform paths** - Uses `Path.GetTempPath()` for compatibility

### Not Planned

1. ❌ **Automatic credential forwarding** - Use volume mounts instead
2. ❌ **postStartCommand auto-fixes** - Templates should be correct
3. ❌ **VS Code IPC replication** - Impossible without VS Code extension

---

*This document reflects the investigation and resolution as of 2026-01-07.*
