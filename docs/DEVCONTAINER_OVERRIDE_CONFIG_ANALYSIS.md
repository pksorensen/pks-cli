# DevContainer Override Config Analysis

**Date**: 2026-01-06
**Issue**: PKS CLI devcontainer spawning fails with sudo errors, while VS Code succeeds with same templates
**Investigation**: Deep analysis of `--override-config` behavior and docker-in-docker feature metadata processing
**Status**: Multiple approaches tested, source code examined, root cause identified but solution remains elusive

---

## Quick Reference for Future Sessions

If you're picking up this investigation, here are the key facts:

### What We Know for Certain

1. **Override config REPLACES, not merges** ([source code proof](#critical-discovery-1-override-config-replaces-base-config-no-merging))
   - When `--override-config` is used, the CLI reads ONLY that file, ignoring the base devcontainer.json
   - No merging happens at the file level

2. **`privileged` comes from feature metadata** ([source code proof](#critical-discovery-2-privileged-flag-comes-from-feature-metadata))
   - The docker-in-docker feature contributes `privileged: true` as metadata
   - This metadata is merged into the final config during image build
   - For this to work, features must be in the config that gets read

3. **VS Code uses override-config successfully** ([log analysis](#what-vs-code-does))
   - VS Code creates `/tmp/devcontainer-<uuid>.json` inside bootstrap container
   - File is deleted after use, so we can't see its contents
   - VS Code extension is closed source

4. **Our full copy with JsonElement doesn't work** ([test results](#all-approaches-tested))
   - Even preserving exact JSON structure with `JsonElement.WriteTo()` fails
   - Features are present in our override config
   - But `--privileged` flag is NOT applied to docker run command

### The Mystery

**Why does VS Code's override config work when ours doesn't?**

Both should contain a full copy of properties including features. Possible explanations:
- Subtle copying differences we haven't found
- VS Code preprocessing we can't see (closed source)
- Additional CLI flags VS Code uses
- Different CLI version with patches

### What to Try Next

See [Next Investigation Steps](#next-investigation-steps-recommended-order) for detailed recommendations.

Top priority: **Verify our override config is valid and compare it byte-by-byte with the base config.**

---

## Executive Summary

The root cause of the sudo failure is that the `--override-config` flag, when used with a full copy of devcontainer.json properties, **interferes with feature metadata processing**. Specifically, the docker-in-docker feature's automatic `--privileged` flag is not being applied.

**Key Finding**: When we stop using `--override-config` and modify devcontainer.json in place, the `--privileged` flag IS correctly applied by the docker-in-docker feature.

---

## Background: The Original Problem

### Symptoms
```bash
sudo: a terminal is required to read the password
sudo: a password is required
/bin/sh: 1: cannot create /home/node/.docker/config.json: Directory nonexistent
```

### Initial Hypothesis (INCORRECT)
We initially thought the templates needed to explicitly add `--privileged` to runArgs because VS Code was doing so.

### Actual Root Cause
The devcontainer CLI **should** automatically add `--privileged` when the docker-in-docker feature is present, but PKS CLI's use of `--override-config` was preventing this automatic behavior.

---

## Understanding --override-config

### Official Documentation

According to the [DevContainers spec](https://github.com/devcontainers/spec/blob/main/docs/specs/devcontainer-reference.md), configuration merging follows these rules:

- **Arrays** (like `runArgs`): Union of values
- **Objects** (like environment vars): Key-based merge
- **Simple values** (like `name`): Last source wins
- **Booleans** (like `init`, `privileged`): true if any source is true

When the order matters, **devcontainer.json is considered last**, meaning properties from the override config can be superseded by the main devcontainer.json.

### CLI Help Text
```bash
--override-config    devcontainer.json path to override any devcontainer.json
                     in the workspace folder (or built-in configuration).
                     This is required when there is no devcontainer.json otherwise.
```

### Expected vs Actual Behavior

| Aspect | Expected | Actual with Full Copy | Actual with Empty Override |
|--------|----------|----------------------|---------------------------|
| **Merge behavior** | Properties merge intelligently | ✅ Works | ❌ Missing required properties |
| **Feature metadata** | Features processed from base config | ❌ Not processed | ❌ Not processed |
| **Privileged flag** | Auto-added by docker-in-docker | ❌ Not added | ❌ Not added |
| **workspaceMount removal** | Should allow override | ✅ Works | ✅ Works |

### Critical Discovery

**The problem**: Using `--override-config` (even empty) **prevents the devcontainer CLI from properly processing feature metadata** from the base devcontainer.json.

This means:
- The docker-in-docker feature's `"privileged": true` metadata is not applied
- The `--privileged` flag is never added to the docker run command
- Sudo fails because the container doesn't have necessary capabilities

---

## What VS Code Does

### From VS Code Logs Analysis

**Log file**: `/workspaces/pks-cli/logs/vscode-start001.log`

#### Bootstrap Container Creation (Line 180)
```bash
docker run -d --mount type=volume,src=claude-pks-cli,dst=/workspaces \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --security-opt label=disable \
  vsc-volume-bootstrap sleep infinity
```

#### DevContainer CLI Invocation (Line 310)
```bash
node /root/.vscode-remote-containers/dist/dev-containers-cli-0.434.0/dist/spec-node/devContainersSpecCLI.js up \
  --workspace-folder /workspaces/pks-cli \
  --config /workspaces/pks-cli/.devcontainer/devcontainer.json \
  --override-config /tmp/devcontainer-8acb00b8-a32e-4da2-9aa0-baf2d452992e.json \
  --mount type=volume,source=claude-pks-cli,target=/workspaces,external=true \
  --mount type=volume,source=vscode,target=/vscode,external=true \
  --include-configuration --include-merged-configuration
```

#### Final Container Run (Line 426)
```bash
docker run --sig-proxy=false -a STDOUT -a STDERR \
  --cap-add=NET_ADMIN --cap-add=NET_RAW \
  --env-file .devcontainer/.env \
  --privileged \
  ...
```

### Key Observations

1. **VS Code DOES use `--override-config`** ✓
2. **VS Code DOES use `--workspace-folder`** ✓ (path inside bootstrap container)
3. **The `--privileged` flag IS applied** ✓
4. **Initial sudo errors occur** but postStartCommand succeeds

### Why Does VS Code Work?

**Unknown**: We don't know what VS Code puts in its override config file (`/tmp/devcontainer-8acb00b8-a32e-4da2-9aa0-baf2d452992e.json`).

**Hypothesis**: VS Code's override config might be:
- Minimal (only overriding specific properties)
- Structured differently to preserve feature metadata
- Using a different devcontainer CLI version with better merge support

**Evidence**: VS Code successfully processes docker-in-docker feature metadata despite using `--override-config`.

---

## What PKS CLI Does (Before Fix)

### Original Approach
```csharp
// Copy ALL properties except workspaceMount/workspaceFolder
foreach (var property in root.EnumerateObject())
{
    if (property.Name == "workspaceMount" || property.Name == "workspaceFolder")
        continue;

    overrideConfig[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText())!;
}
```

### Problems with This Approach

1. **Copying `runArgs`**: Locks in the runArgs before features can modify them
2. **Copying `features`**: May prevent feature metadata from being processed correctly
3. **JSON Serialization**: `JsonSerializer.Deserialize<object>()` may lose type information
4. **Merge Order**: Override config properties may block feature-added properties

### Test Results

| Configuration | --privileged Applied? | Sudo Works? | Notes |
|--------------|---------------------|------------|-------|
| Full copy override | ❌ No | ❌ No | Feature metadata not processed |
| Empty override `{}` | ❌ No | ❌ No | CLI error: missing dockerfile |
| Minimal override `{workspaceMount: null}` | ❌ No | ❌ No | CLI error: missing dockerfile |
| No override + workspace-folder | ✅ **Yes!** | ⚠️ Different error | Bind mount error |

---

## Critical Test Result

### Test: No Override Config, In-Place Modification

**Approach**:
1. Modify devcontainer.json in place to remove `workspaceMount`/`workspaceFolder`
2. Don't use `--override-config`
3. Use `--workspace-folder` parameter

**Docker Run Command Generated**:
```bash
docker run --sig-proxy=false -a STDOUT -a STDERR \
  --cap-add=NET_ADMIN --cap-add=NET_RAW \
  --env-file .devcontainer/.env \
  --privileged \   # ✅ FLAG IS PRESENT!
  ...
```

**Result**:
- ✅ `--privileged` flag IS applied!
- ✅ Feature metadata IS processed correctly!
- ❌ Bind mount error: `/workspaces/test-final-fix` doesn't exist on host

**Error**:
```
docker: Error response from daemon: invalid mount config for type "bind":
bind source path does not exist: /workspaces/test-final-fix
```

### Analysis

The `--workspace-folder` CLI parameter causes devcontainer CLI to create a **bind mount** for that path:
```bash
--mount type=bind,source=/workspaces/test-final-fix,target=/workspaces/test-final-fix
```

This path exists **inside the bootstrap container** but **not on the host**, causing the bind mount to fail.

---

## The CLI Requirement Dilemma

### DevContainer CLI Requirement

The devcontainer CLI requires **EITHER**:
- `--workspace-folder` parameter, OR
- `--override-config` parameter

Without one of these, the CLI returns:
```
Missing required argument: workspace-folder or override-config
```

### The Catch-22

| Option | Pros | Cons |
|--------|------|------|
| **--workspace-folder** | ✅ Feature metadata processed<br>✅ --privileged applied | ❌ Creates bind mounts<br>❌ Path doesn't exist on host |
| **--override-config (full copy)** | ✅ No bind mount issues<br>✅ Can remove workspaceMount | ❌ Blocks feature metadata<br>❌ --privileged not applied |
| **--override-config (empty)** | ✅ Should preserve features | ❌ CLI error: missing dockerfile<br>❌ Merge doesn't work |
| **--override-config (minimal)** | ✅ Should preserve features | ❌ CLI error: missing dockerfile<br>❌ Setting null doesn't work |

---

## Potential Solutions

### Option 1: Use --workspace-folder with --mount-workspace-git-root false

**Theory**: Maybe there's a CLI flag to prevent the automatic bind mount.

**Status**: Not tested. The `--mount-workspace-git-root` flag exists but may not prevent the workspace bind mount.

### Option 2: Fix the Override Config Serialization

**Theory**: The problem might be how we serialize/deserialize the override config. Using `JsonSerializer.Deserialize<object>()` might lose structure.

**Approach**:
```csharp
// Instead of Deserialize<object>, use JsonElement with Utf8JsonWriter
using var stream = new MemoryStream();
using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

writer.WriteStartObject();
foreach (var property in root.EnumerateObject())
{
    if (property.Name == "workspaceMount" || property.Name == "workspaceFolder")
        continue;

    writer.WritePropertyName(property.Name);
    property.Value.WriteTo(writer);  // Preserves exact JSON structure
}
writer.WriteEndObject();
```

**Status**: **TESTED - FAILED** ❌

**Test Date**: 2026-01-06

**Result**: Using JsonElement with `property.Value.WriteTo(writer)` to preserve exact JSON structure still blocks feature metadata processing. The docker run command generated did NOT include the `--privileged` flag:

```bash
docker run --sig-proxy=false -a STDOUT -a STDERR ...
  --cap-add=NET_ADMIN --cap-add=NET_RAW  # From devcontainer.json runArgs
  --tty --interactive --env-file .devcontainer/.env ...
  # ❌ NO --privileged flag!
```

**Conclusion**: The problem is not with JSON serialization/deserialization losing structure. Even preserving the exact JSON structure with JsonElement, the devcontainer CLI still does not process feature metadata correctly when using `--override-config` with a full copy of properties.

### Option 3: Inspect VS Code's Override Config

**Approach**:
1. Run VS Code devcontainer spawn
2. Before it deletes the temp override config, copy it
3. Analyze what VS Code actually puts in the override
4. Replicate the same structure in PKS CLI

**Status**: Would require accessing the bootstrap container during VS Code spawn.

### Option 4: Use --config Without Override

**Theory**: If we modify devcontainer.json in place and only use `--config`, maybe we don't need override or workspace-folder.

**Status**: Tested - CLI requires one of them.

### Option 5: Create Symbolic Link or Bind Mount in Bootstrap

**Theory**: Create `/workspaces/test-final-fix` as a symlink to the volume mount inside the bootstrap container before running devcontainer up.

**Approach**:
```bash
ln -s /workspaces/workspace /workspaces/test-final-fix
```

**Status**: Not tested. May not work for Docker bind mounts.

---

## Comparison: VS Code vs PKS CLI

### Similarities
- ✅ Both use bootstrap containers
- ✅ Both use Docker socket mounting
- ✅ Both use volume-based workflows
- ✅ Both use `devcontainer up` command
- ✅ Both use `--override-config`
- ✅ Both use `--workspace-folder`
- ✅ Both use `--include-configuration` and `--include-merged-configuration`

### Differences

| Aspect | VS Code | PKS CLI |
|--------|---------|---------|
| **Override config content** | Unknown (not visible in logs) | Full copy of all properties |
| **--privileged applied** | ✅ Yes | ❌ No (with override) |
| **Sudo errors** | ⚠️ Initial errors, but succeeds | ❌ Complete failure |
| **Bind mount issues** | ✅ No issues | ❌ Fails with workspace-folder |

### Critical Unknown

**What does VS Code put in its override config?**

Without knowing this, we can't replicate VS Code's working approach. The override config file is created in `/tmp/` and likely deleted after use.

---

## Conclusions

### What We Know

1. **--override-config DOES work** (VS Code proves this)
2. **Feature metadata CAN be processed** with override configs (VS Code proves this)
3. **Our implementation is different** from VS Code's
4. **The issue is in HOW we create the override config**, not the concept itself

### What We Don't Know

1. **What VS Code puts in the override config**
2. **Why our full copy blocks feature metadata**
3. **Why our empty/minimal override fails validation**
4. **The exact merge algorithm** the devcontainer CLI uses

### What We Confirmed

1. ✅ **Removing --override-config allows --privileged to be applied**
2. ✅ **The docker-in-docker feature DOES set privileged metadata**
3. ✅ **In-place modification of devcontainer.json works**
4. ❌ **But we still need --override-config or --workspace-folder per CLI requirement**
5. ❌ **Using JsonElement to preserve JSON structure doesn't help** (tested 2026-01-06)
   - Even with exact JSON structure preservation, `--override-config` with full property copy still blocks feature metadata
   - The problem is not JSON serialization artifacts

---

## Recommendations

### Short-term Fix (Pragmatic)

**Add `--privileged` to template runArgs** as we originally did. This works but:
- ❌ Modifies templates (not ideal)
- ✅ Fixes the immediate problem
- ✅ Matches what VS Code's final docker run shows
- ⚠️ Workaround rather than root cause fix

### Long-term Solution (Proper)

**Investigate VS Code's override config** by:
1. Modifying VS Code extension or using strace to capture the override config content
2. Replicating the exact structure in PKS CLI
3. Understanding why VS Code's approach preserves feature metadata

### Alternative Approach

**Use devcontainer CLI in "workspace mode"** instead of bootstrap mode:
- Build and run devcontainer from host directly
- May avoid the override config issue entirely
- Would require different architecture for PKS CLI

---

## References

### Specifications and Documentation
- [DevContainers Specification](https://containers.dev/implementors/spec/)
- [DevContainer Reference](https://github.com/devcontainers/spec/blob/main/docs/specs/devcontainer-reference.md)
- [DevContainer Features](https://github.com/devcontainers/spec/blob/main/docs/specs/devcontainer-features.md)
- [Docker-in-Docker Feature](https://github.com/devcontainers/features/blob/main/src/docker-in-docker/devcontainer-feature.json)

### Source Code Repositories
- [devcontainers/cli](https://github.com/devcontainers/cli) - Open source devcontainer CLI (examined for this investigation)
- [microsoft/vscode-remote-release](https://github.com/microsoft/vscode-remote-release) - Issue tracking for VS Code remote extensions (closed source)
- [VS Code Dev Containers Extension](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers)

### Key Source Files Examined
- `src/spec-node/configContainer.ts` - Config reading logic showing override replaces base
- `src/spec-node/imageMetadata.ts` - Feature metadata processing and privileged flag merging
- `src/spec-node/devContainersSpecCLI.ts` - CLI argument parsing and command handlers

### Related Issues
- [CLI Issue #91: Privileged flag regression](https://github.com/devcontainers/cli/issues/91)

---

## Next Steps

1. ~~**Attempt Option 2**: Fix JSON serialization to preserve structure~~ ✅ **COMPLETED** - Failed (2026-01-06)
2. **Attempt Option 3**: Capture VS Code's override config for analysis - **HIGHEST PRIORITY**
   - This is the critical unknown that could unlock the solution
   - Need to intercept/copy VS Code's override config before it's deleted
3. **Attempt Option 5**: Create symbolic link workaround for bind mount
4. **Consider pragmatic fix**: Add --privileged to templates if investigation stalls
   - User preference is to NOT modify templates, so this is last resort

---

## Source Code Analysis (2026-01-06)

### Examining the devcontainers/cli Source Code

Since VS Code's extension is closed source, we cloned and examined the **open source devcontainers/cli** repository to understand how `--override-config` actually works.

**Repository**: https://github.com/devcontainers/cli

### Critical Discovery 1: Override Config REPLACES Base Config (No Merging)

**Location**: `src/spec-node/configContainer.ts:82-84`

```typescript
export async function readDevContainerConfigFile(cliHost: CLIHost, workspace: Workspace | undefined,
    configFile: URI, mountWorkspaceGitRoot: boolean, output: Log,
    consistency?: BindMountConsistency, overrideConfigFile?: URI) {

    const documents = createDocuments(cliHost);
    const content = await documents.readDocument(overrideConfigFile ?? configFile);  // ❗ KEY LINE
    // ... rest of function
}
```

The `??` (nullish coalescing) operator means:
- If `overrideConfigFile` exists → **read ONLY the override config**
- If `overrideConfigFile` is undefined → read the base config

**Implication**: The override config **COMPLETELY REPLACES** the base config. There is NO merging at the file level.

This means VS Code's override config must contain ALL necessary properties, including the `features` object.

### Critical Discovery 2: `privileged` Flag Comes from Feature Metadata

**Location**: `src/spec-node/imageMetadata.ts:156-199`

```typescript
export function mergeConfiguration(config: DevContainerConfig, imageMetadata: ImageMetadataEntry[]): MergedDevContainerConfig {
    const reversed = imageMetadata.slice().reverse();
    const copy = { ...config };
    replaceProperties.forEach(property => delete (copy as any)[property]);
    const merged: MergedDevContainerConfig = {
        ...copy,
        init: imageMetadata.some(entry => entry.init),
        privileged: imageMetadata.some(entry => entry.privileged),  // ❗ KEY LINE
        capAdd: unionOrUndefined(imageMetadata.map(entry => entry.capAdd)),
        // ... other properties
    };
    return merged;
}
```

**Key Insight**: The `privileged: true` setting comes from **imageMetadata**, which is populated by feature processing. The docker-in-docker feature adds `privileged: true` to its metadata, which then gets merged into the final config.

**Flow**:
1. Features are processed from the devcontainer config
2. Each feature can contribute metadata (docker-in-docker adds `privileged: true`)
3. Metadata is collected into `imageMetadata` array
4. `mergeConfiguration()` merges feature metadata with the base config
5. Final config has `privileged: true` in the docker run command

### Critical Discovery 3: Features Must Be in the Config

**Location**: `src/spec-node/imageMetadata.ts:290-315`

```typescript
export function getDevcontainerMetadata(baseImageMetadata: SubstitutedConfig<ImageMetadataEntry[]>,
    devContainerConfig: SubstitutedConfig<DevContainerConfig>,
    featuresConfig: FeaturesConfig | undefined,
    // ...
): SubstitutedConfig<ImageMetadataEntry[]> {

    const featureRaw = featuresConfig?.featureSets.map(featureSet =>
        featureSet.features.map(feature => ({
            id: featureSet.sourceInformation.userFeatureId,
            ...pick(feature, effectivePickFeatureProperties),
        }))).flat() || [];

    const raw = [
        ...baseImageMetadata.raw,
        ...featureRaw,  // ❗ Features contribute metadata here
        pick(devContainerConfig.raw, effectivePickDevcontainerProperties),
    ].filter(config => Object.keys(config).length);
    // ...
}
```

**Implication**: For the docker-in-docker feature to contribute its `privileged: true` metadata, the `features` object must be present in the config that gets read.

Since override-config replaces the base config entirely, **if the override config doesn't contain the features object, features won't be processed, and privileged won't be set**.

### The Mystery: Why Does VS Code Work?

Given our findings:
1. ✅ Override config completely replaces base config (no merging)
2. ✅ Features must be in the config for metadata processing
3. ✅ VS Code uses override-config and it works

**Conclusion**: VS Code's override config must contain a **full copy** of all properties including features.

**But then why doesn't our full copy work?**

Possible explanations:
1. **Subtle copying differences**: We might be copying something incorrectly in a way that breaks feature processing (but JsonElement preserves exact structure...)
2. **VS Code preprocessing**: The closed-source VS Code extension might be doing additional preprocessing we can't see
3. **Different CLI versions**: VS Code might use a different/patched version of the devcontainer CLI
4. **Additional CLI flags**: VS Code might be passing additional flags that affect feature processing

### Source Code References

**devcontainers/cli Repository Structure**:
- `src/spec-node/configContainer.ts` - Configuration reading and workspace handling
- `src/spec-node/imageMetadata.ts` - Feature metadata processing and merging
- `src/spec-node/singleContainer.ts` - Single container devcontainer implementation
- `src/spec-node/devContainersSpecCLI.ts` - CLI argument parsing and command handling

**Key Functions**:
- `readDevContainerConfigFile()` - Reads config, chooses override if present
- `mergeConfiguration()` - Merges feature metadata into final config
- `getDevcontainerMetadata()` - Extracts and processes feature metadata

**VS Code Extension Status**:
- **ms-vscode-remote.remote-containers** extension is **CLOSED SOURCE**
- Feedback/issues tracked at: https://github.com/microsoft/vscode-remote-release
- Extension code is proprietary, not available for inspection

---

## Investigation Summary (2026-01-06)

### All Approaches Tested

| # | Approach | --privileged Applied? | Result | Notes |
|---|----------|---------------------|--------|-------|
| 1 | Full copy override (Deserialize<object>) | ❌ No | FAILED | Blocks feature metadata |
| 2 | Empty override config `{}` | ❌ No | FAILED | CLI error: missing dockerfile |
| 3 | Minimal override (workspaceMount: null) | ❌ No | FAILED | CLI error: missing dockerfile |
| 4 | No override + workspace-folder | ✅ Yes | FAILED | Bind mount error (path not on host) |
| 5 | JsonElement override (preserves structure) | ❌ No | FAILED | Still blocks feature metadata |
| 6 | workspace-folder + mount-workspace-git-root false | ❌ No | FAILED | Still has bind mount + no --privileged |

### Key Learnings

1. **Feature metadata processing blocked**: Any approach using `--override-config` with copied properties (even with perfect JSON structure preservation) blocks the docker-in-docker feature's `privileged` metadata from being applied.

2. **CLI requirements conflict**: The devcontainer CLI requires EITHER `--workspace-folder` OR `--override-config`, but:
   - `--workspace-folder` causes bind mount errors (path inside bootstrap doesn't exist on host)
   - `--override-config` with properties blocks feature metadata processing

3. **VS Code's secret**: VS Code successfully uses `--override-config` + `--workspace-folder` together, but we don't know what's in their override config file (created in `/tmp/` with UUID name, deleted after use).

4. **JSON serialization not the issue**: Using `JsonElement.WriteTo()` to preserve exact JSON structure didn't help - the problem is not serialization artifacts.

### Current State

**Templates**: UNCHANGED (per user preference)

**PKS CLI**: Multiple approaches tested, none successful

**Critical Unknown**: What VS Code puts in its override config file. This remains the key to understanding why VS Code works and PKS CLI doesn't.

### Remaining Options

Given all failed attempts and source code analysis, paths forward:

1. **Option A (Difficult)**: Capture VS Code's actual override config content
   - Requires intercepting VS Code's temp file before deletion
   - Methods: Modify VS Code extension, use strace/dtrace, or add debug logging to devcontainer CLI
   - Most likely to provide the correct solution
   - **New insight**: We now know the override must contain features, so we can compare if ours does

2. **Option B (Investigate Further)**: Debug why our full copy doesn't work
   - **New approach**: Add extensive logging to see if features are being processed
   - Compare the exact JSON structure of our override vs the base config
   - Test with a simplified devcontainer.json (minimal features) to isolate the issue
   - Verify that our copied JSON is valid and parseable by devcontainer CLI

3. **Option C (Test with devcontainer CLI directly)**: Bypass PKS CLI
   - Create an override config file manually with full copy
   - Run devcontainer CLI directly from command line with our override config
   - This isolates whether the problem is in our C# code or the override config itself

4. **Option D (Pragmatic - Against User Preference)**: Add `--privileged` to template runArgs
   - User explicitly stated: "I would prefer us not changing the devcontainer.json files"
   - Would fix the immediate problem but is a workaround, not a root cause fix
   - Templates work in VS Code without this modification

5. **Option E (Architectural)**: Redesign PKS CLI to not use bootstrap container pattern
   - Would require significant architectural changes
   - May avoid the override config issue entirely
   - Large scope of work

### Next Investigation Steps (Recommended Order)

Based on source code findings, recommended next steps:

1. **Verify our override config is valid JSON and contains features** ⭐ HIGHEST PRIORITY
   ```bash
   # Test if our generated override config can be read by devcontainer CLI
   cat /path/to/override-config.json | jq .  # Verify valid JSON
   devcontainer up --override-config /path/to/override-config.json --workspace-folder /path
   ```

2. **Add debug logging to see what's being read**
   - Modify our C# code to log the exact override config contents
   - Check if features object is present and correct in our override config

3. **Create minimal test case**
   - Simplest possible devcontainer.json with just docker-in-docker feature
   - Test if our override config approach works with minimal config
   - Gradually add complexity to find what breaks

4. **Compare our override config structure with base config**
   - Output both as formatted JSON
   - Use diff to find any structural differences
   - Check for serialization artifacts

---

---

## 🎉 **FINAL SOLUTION FOUND** (2026-01-06)

### The Working Approach

After extensive testing, we found the solution that makes `--override-config` work correctly with feature metadata processing:

**Key Changes:**
1. ✅ Use `JsonElement` with `Utf8JsonWriter` to preserve exact JSON structure
2. ✅ Copy ALL properties including `features` to override config
3. ✅ Remove `workspaceMount` and `workspaceFolder` properties
4. ✅ Use `--override-config` **WITHOUT** `--workspace-folder`
5. ✅ Use `--mount` flag for volume mounting instead

### The Critical Discovery

The problem wasn't with the override config itself - it was the combination of `--workspace-folder` + `--override-config`:
- Using `--workspace-folder` causes devcontainer CLI to create a **bind mount**
- The bind mount path exists inside the bootstrap container but NOT on the host
- This causes "bind source path does not exist" errors

**Solution**: Use `--override-config` alone (satisfies CLI requirement) and rely on `--mount` flag for volume mounting.

### Verification

Tested with minimal devcontainer containing docker-in-docker feature:
```bash
docker inspect <container-id> | jq '.[0].HostConfig.Privileged'
# Returns: true ✅

docker exec <container-id> docker --version
# Returns: Docker version 29.1.3-1 ✅
```

### Implementation

**Code Location**: `DevcontainerSpawnerService.cs`

**Key Method**: `CreateOverrideConfigWithJsonElementAsync()`
- Uses `Utf8JsonWriter` to write JSON
- Calls `property.Value.WriteTo(writer)` to preserve structure
- Skips `workspaceMount` and `workspaceFolder` properties

**Command Pattern**:
```bash
devcontainer up \
  --config /path/to/devcontainer.json \
  --override-config /path/to/override-config.json \  # Override with all props except workspace*
  --mount type=volume,source=<vol>,target=/workspaces,external=true \
  --id-label devcontainer.local.folder=<name> \
  --id-label devcontainer.local.volume=<vol> \
  --update-remote-user-uid-default off \
  --include-configuration \
  --include-merged-configuration
```

**Note**: NO `--workspace-folder` flag!

### Why This Works

From source code analysis (`src/spec-node/configContainer.ts:84`):
```typescript
const content = await documents.readDocument(overrideConfigFile ?? configFile);
```

The `??` operator means override completely replaces base config. Our override config contains:
- ✅ All properties from base config (including `features`)
- ✅ Exact JSON structure (via `JsonElement.WriteTo()`)
- ❌ No `workspaceMount` or `workspaceFolder` (removed)

Features are processed → metadata includes `privileged: true` → flag applied to docker run! ✨

### Templates

**No changes needed** - templates remain unchanged as user requested. The fix is entirely in PKS CLI code.

---

*This document reflects the complete investigation and final solution as of 2026-01-06.*
