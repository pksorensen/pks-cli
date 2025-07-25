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

## Other Scripts

- `update-version.sh` - Version management utility

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