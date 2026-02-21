# PKS CLI Scripts

This folder contains various utility scripts for building, publishing, and running PKS CLI.

## Docker Scripts

### Cross-Platform Compatibility

Each Docker operation has scripts for different platforms:

| Operation | Linux/macOS | Windows PowerShell | Windows Batch |
|-----------|-------------|-------------------|---------------|
| Build     | `docker-build.sh` | `docker-build.ps1` | `docker-build.bat` |
| Publish   | `docker-publish.sh` | `docker-publish.ps1` | `docker-publish.bat` |
| Run       | `docker-run.sh` | `docker-run.ps1` | `docker-run.bat` |

### Usage Examples

**Windows PowerShell:**
```powershell
# Build image
.\scripts\docker-build.ps1

# Build with specific tag
.\scripts\docker-build.ps1 v1.0.0

# Publish to registry
.\scripts\docker-publish.ps1 v1.0.0

# Run with registry image
$env:USE_REGISTRY="true"
.\scripts\docker-run.ps1 init MyProject
```

**Windows Command Prompt:**
```cmd
REM Build image
scripts\docker-build.bat

REM Build with specific tag  
scripts\docker-build.bat v1.0.0

REM Run with registry image
set USE_REGISTRY=true
scripts\docker-run.bat init MyProject
```

**Linux/macOS:**
```bash
# Build image
./scripts/docker-build.sh

# Build with specific tag
./scripts/docker-build.sh v1.0.0

# Run with registry image
USE_REGISTRY=true ./scripts/docker-run.sh init MyProject
```

### Testing Scripts

**PowerShell Syntax Validation:**
```powershell
.\scripts\test-docker-scripts.ps1
```

This script validates that all PowerShell scripts have correct syntax.

## Version Management Scripts

PKS CLI uses a per-template semantic release strategy. These scripts enable independent versioning and release management for the CLI and each template package.

### detect-changes.sh

Detects which packages have changes since their last release.

**Usage:**
```bash
./scripts/detect-changes.sh
```

**Output:**
```json
{
  "cli": true,
  "devcontainer": false,
  "claude-dotnet-9": true,
  "claude-dotnet-10-full": false,
  "pks-fullstack": true
}
```

**How it works:**
- Compares each package's path against its last git tag
- Tag patterns:
  - CLI: `v*` (e.g., v1.2.0)
  - Templates: `<template-name>-v*` (e.g., devcontainer-v1.0.5)
- Returns JSON with boolean change status for each package
- Handles initial release (no tags) gracefully

**Exit codes:**
- 0: Success
- Non-zero: Error occurred

### get-package-version.sh

Extracts the current version from a package's .csproj file.

**Usage:**
```bash
./scripts/get-package-version.sh <package-scope>
```

**Package scopes:**
- `cli` - Main PKS CLI application
- `devcontainer` - DevContainer template
- `claude-dotnet-9` - Claude .NET 9 template
- `claude-dotnet-10-full` - Claude .NET 10 Full template
- `pks-fullstack` - PKS Fullstack template

**Examples:**
```bash
# Get CLI version
./scripts/get-package-version.sh cli
# Output: 1.2.0

# Get devcontainer template version
./scripts/get-package-version.sh devcontainer
# Output: 1.0.5

# Error handling for invalid scope
./scripts/get-package-version.sh invalid
# Error: Invalid package scope: invalid
```

**Exit codes:**
- 0: Success, version printed to stdout
- 1: Invalid scope or version not found

### update-version.sh

Updates version in one or all package .csproj files.

**Usage:**
```bash
./scripts/update-version.sh <version> [scope]
```

**Parameters:**
- `version` - Version to set (e.g., 1.2.0, 1.2.0-rc.1)
- `scope` - Optional. Package to update (default: all)

**Available scopes:**
- `all` - Update all packages (default)
- `cli` - Update only CLI
- `devcontainer` - Update only devcontainer template
- `claude-dotnet-9` - Update only claude-dotnet-9 template
- `claude-dotnet-10-full` - Update only claude-dotnet-10-full template
- `pks-fullstack` - Update only pks-fullstack template

**Examples:**
```bash
# Update all packages to 1.2.0
./scripts/update-version.sh 1.2.0

# Update only CLI to 1.2.0-rc.1
./scripts/update-version.sh 1.2.0-rc.1 cli

# Update only devcontainer template to 1.0.5
./scripts/update-version.sh 1.0.5 devcontainer
```

**What it updates:**
- `<Version>` tag
- `<PackageVersion>` tag (if present)
- `<AssemblyVersion>` tag (if present)
- `<FileVersion>` tag (if present)

**Exit codes:**
- 0: Success
- 1: Invalid parameters or file not found

### Version Management Workflow

**1. Check for changes:**
```bash
# Detect which packages changed
CHANGES=$(./scripts/detect-changes.sh)
echo "$CHANGES"

# Parse JSON (using jq)
CLI_CHANGED=$(echo "$CHANGES" | jq -r '.cli')
DEVCONTAINER_CHANGED=$(echo "$CHANGES" | jq -r '.devcontainer')
```

**2. Get current versions:**
```bash
# Get current CLI version
CLI_VERSION=$(./scripts/get-package-version.sh cli)
echo "Current CLI version: $CLI_VERSION"

# Get current template version
TEMPLATE_VERSION=$(./scripts/get-package-version.sh devcontainer)
echo "Current devcontainer version: $TEMPLATE_VERSION"
```

**3. Update versions:**
```bash
# Update CLI to new version
./scripts/update-version.sh 1.2.1 cli

# Update specific template
./scripts/update-version.sh 1.0.6 devcontainer

# Update all packages
./scripts/update-version.sh 1.3.0 all
```

**4. Create release tags:**
```bash
# Tag CLI release
git tag v1.2.1
git push origin v1.2.1

# Tag template release
git tag devcontainer-v1.0.6
git push origin devcontainer-v1.0.6
```

### CI/CD Integration

These scripts are designed for semantic-release workflows:

```yaml
# Example GitHub Actions workflow
- name: Detect changes
  id: changes
  run: |
    CHANGES=$(./scripts/detect-changes.sh)
    echo "changes=$CHANGES" >> $GITHUB_OUTPUT

- name: Release CLI if changed
  if: fromJSON(steps.changes.outputs.changes).cli == true
  run: |
    VERSION=$(npx semantic-release --dry-run | grep -oP 'v\K[0-9.]+')
    ./scripts/update-version.sh $VERSION cli
    # Build and publish CLI

- name: Release devcontainer if changed
  if: fromJSON(steps.changes.outputs.changes).devcontainer == true
  run: |
    VERSION=$(npx semantic-release --dry-run | grep -oP 'v\K[0-9.]+')
    ./scripts/update-version.sh $VERSION devcontainer
    # Build and publish template
```

## Environment Variables

- `USE_REGISTRY` - Use registry image instead of local (`true`/`false`)
- `PKS_CLI_VERSION` - Specify image version (default: `latest`)

## Registry Information

- **Registry**: `registry.kjeldager.io`
- **Repository**: `si14agents/cli`
- **Tags**: `latest`, version-specific (e.g., `v1.0.0`)

## Troubleshooting

### PowerShell Execution Policy

If you get execution policy errors on Windows:

```powershell
# Check current policy
Get-ExecutionPolicy

# Set policy for current user (recommended)
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser

# Or run with bypass (one-time)
powershell -ExecutionPolicy Bypass -File .\scripts\docker-build.ps1
```

### Docker Issues

1. **Docker not found**: Ensure Docker Desktop is installed and running
2. **Registry login**: Run `docker login registry.kjeldager.io` before publishing
3. **Permission denied**: On Linux/macOS, ensure scripts are executable: `chmod +x scripts/*.sh`

For more detailed Docker usage, see [../DOCKER.md](../DOCKER.md).