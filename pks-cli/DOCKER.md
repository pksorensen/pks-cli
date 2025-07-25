# PKS CLI Docker Distribution

This document describes how to build, publish, and use the PKS CLI as a Docker image.

## Quick Start

### Using the Published Image

```bash
# Pull and run the latest version
docker run --rm -it registry.kjeldager.io/si14agents/cli:latest

# Run a specific command
docker run --rm -it registry.kjeldager.io/si14agents/cli:latest init MyProject

# Mount current directory for project creation
docker run --rm -it -v $(pwd):/workspace -w /workspace \
  registry.kjeldager.io/si14agents/cli:latest init MyProject
```

### Using the Convenience Script

**Linux/macOS:**
```bash
# Use local image (after building)
./scripts/docker-run.sh --help

# Use registry image
USE_REGISTRY=true ./scripts/docker-run.sh init MyProject

# Set specific version
PKS_CLI_VERSION=1.0.0 USE_REGISTRY=true ./scripts/docker-run.sh status
```

**Windows (PowerShell):**
```powershell
# Use local image (after building)
.\scripts\docker-run.ps1 --help

# Use registry image
$env:USE_REGISTRY="true"; .\scripts\docker-run.ps1 init MyProject

# Set specific version
$env:PKS_CLI_VERSION="1.0.0"; $env:USE_REGISTRY="true"; .\scripts\docker-run.ps1 status
```

**Windows (Command Prompt):**
```cmd
REM Use local image (after building)
scripts\docker-run.bat --help

REM Use registry image (set environment variables first)
set USE_REGISTRY=true
scripts\docker-run.bat init MyProject
```

## Building and Publishing

### Prerequisites

- Docker installed and running
- Access to `registry.kjeldager.io` (for publishing)
- Docker login credentials for the registry

### Build Process

1. **Build the Docker image:**
   
   **Linux/macOS:**
   ```bash
   ./scripts/docker-build.sh
   # Or with a specific tag
   ./scripts/docker-build.sh v1.0.0
   ```
   
   **Windows (PowerShell):**
   ```powershell
   .\scripts\docker-build.ps1
   # Or with a specific tag
   .\scripts\docker-build.ps1 v1.0.0
   ```
   
   **Windows (Command Prompt):**
   ```cmd
   scripts\docker-build.bat
   REM Or with a specific tag
   scripts\docker-build.bat v1.0.0
   ```

2. **Test the built image:**
   ```bash
   docker run --rm -it pks-cli:latest --help
   ```

3. **Login to registry:**
   ```bash
   docker login registry.kjeldager.io
   ```

4. **Publish to registry:**
   
   **Linux/macOS:**
   ```bash
   ./scripts/docker-publish.sh
   # Or with a specific tag
   ./scripts/docker-publish.sh v1.0.0
   ```
   
   **Windows (PowerShell):**
   ```powershell
   .\scripts\docker-publish.ps1
   # Or with a specific tag
   .\scripts\docker-publish.ps1 v1.0.0
   ```
   
   **Windows (Command Prompt):**
   ```cmd
   scripts\docker-publish.bat
   REM Or with a specific tag
   scripts\docker-publish.bat v1.0.0
   ```

## Docker Image Details

### Available Image Variants

PKS CLI offers two Docker image variants optimized for different use cases:

#### Standard Alpine Image (Recommended)
- **Build Stage**: `mcr.microsoft.com/dotnet/sdk:8.0`
- **Runtime Stage**: `mcr.microsoft.com/dotnet/aspnet:8.0-alpine`
- **Size**: ~165-200MB (67% smaller than the original 500MB)
- **Use case**: General use with good balance of size and compatibility
- **Note**: Uses ASP.NET Core runtime due to ModelContextProtocol.AspNetCore dependency

#### Minimal Alpine Image (Ultra-lightweight)
- **Build Stage**: `mcr.microsoft.com/dotnet/sdk:8.0-alpine`
- **Runtime Stage**: `alpine:3.18` (no .NET runtime, self-contained)
- **Size**: ~50-80MB (ultra-minimal)
- **Use case**: CI/CD pipelines, resource-constrained environments

### Building Different Variants

**Standard Alpine (default):**
```powershell
# Windows
.\scripts\docker-build.ps1

# Linux/macOS
./scripts/docker-build.sh
```

**Minimal Alpine:**
```powershell
# Windows
.\scripts\docker-build.ps1 -Minimal

# Linux/macOS
./scripts/docker-build.sh --minimal
```

### Image Contents

**Standard Alpine:**
- .NET 8 runtime
- PKS CLI application
- Git, curl, wget, unzip, bash
- Alpine Linux base (~5MB)

**Minimal Alpine:**
- Self-contained PKS CLI executable
- Git, curl, ca-certificates only
- Pure Alpine Linux base (~5MB)

### Security Features
- Non-root user (`pksuser`)
- Minimal attack surface
- No unnecessary packages
- Alpine Linux security updates

### Size Optimization
The Alpine-based images achieve dramatic size reduction through:
- Alpine Linux base (5MB vs Ubuntu's 70MB+)
- .NET runtime Alpine variant (vs ASP.NET runtime)
- Minimal package installation
- Self-contained deployment option for ultra-minimal variant

## Usage Patterns

### Development Environment

```bash
# Create an alias for convenience
alias pks-docker='docker run --rm -it -v $(pwd):/workspace -w /workspace registry.kjeldager.io/si14agents/cli:latest'

# Use it like the native CLI
pks-docker init MyProject --template api
pks-docker status
pks-docker deploy
```

### CI/CD Integration

```yaml
# Example GitHub Actions step
- name: Initialize project with PKS CLI
  run: |
    docker run --rm -v ${{ github.workspace }}:/workspace -w /workspace \
      registry.kjeldager.io/si14agents/cli:latest \
      init ${{ github.event.repository.name }} --template api
```

### Docker Compose

```yaml
version: '3.8'
services:
  pks-cli:
    image: registry.kjeldager.io/si14agents/cli:latest
    volumes:
      - .:/workspace
      - ~/.gitconfig:/home/pksuser/.gitconfig:ro
    working_dir: /workspace
    command: ["--help"]
```

## Volume Mounts

### Recommended Mounts

1. **Project Directory:**
   ```bash
   -v $(pwd):/workspace -w /workspace
   ```

2. **Git Configuration:**
   ```bash
   -v ~/.gitconfig:/home/pksuser/.gitconfig:ro
   ```

3. **SSH Keys (if needed):**
   ```bash
   -v ~/.ssh:/home/pksuser/.ssh:ro
   ```

### Example with All Mounts

```bash
docker run --rm -it \
  -v $(pwd):/workspace \
  -v ~/.gitconfig:/home/pksuser/.gitconfig:ro \
  -v ~/.ssh:/home/pksuser/.ssh:ro \
  -w /workspace \
  registry.kjeldager.io/si14agents/cli:latest \
  init MyProject
```

## Environment Variables

### Configuration Variables

- `PKS_CLI_VERSION`: Specify image version (default: `latest`)
- `USE_REGISTRY`: Use registry image instead of local (default: `false`)
- `DOTNET_ENABLE_DIAGNOSTICS`: Enable .NET diagnostics (default: `0`)

### Usage Examples

```bash
# Use specific version
PKS_CLI_VERSION=v1.0.0 ./docker-run.sh --help

# Force registry usage
USE_REGISTRY=true ./docker-run.sh init MyProject
```

## Troubleshooting

### Common Issues

1. **Cross-Platform Build Errors:**
   
   **NETSDK1047 - Missing Linux target:**
   ```
   error NETSDK1047: Assets file doesn't have a target for 'net8.0/linux-x64'
   ```
   **Solution**: Fixed in Dockerfile - uses `linux-musl-x64` runtime for Alpine Linux.

   **NETSDK1094 - ReadyToRun not supported:**
   ```
   error NETSDK1094: Unable to optimize assemblies for performance: a valid runtime package was not found
   ```
   **Solution**: Fixed in Dockerfile - `PublishReadyToRun=false` for Alpine Linux compatibility.

2. **Permission Denied:**
   ```bash
   # Ensure scripts are executable
   chmod +x docker-*.sh
   ```

3. **Registry Login Issues:**
   ```bash
   # Login to registry
   docker login registry.kjeldager.io
   ```

4. **Image Not Found:**
   ```bash
   # Pull the image manually
   docker pull registry.kjeldager.io/si14agents/cli:latest
   ```

5. **Volume Mount Issues:**
   ```bash
   # Use absolute paths
   docker run --rm -it -v /absolute/path:/workspace registry.kjeldager.io/si14agents/cli:latest
   ```

### Debugging

```bash
# Run with shell access
docker run --rm -it --entrypoint /bin/bash registry.kjeldager.io/si14agents/cli:latest

# Check image contents
docker run --rm registry.kjeldager.io/si14agents/cli:latest ls -la /app

# View logs
docker logs <container-id>
```

## Registry Information

- **Registry**: `registry.kjeldager.io`
- **Repository**: `si14agents/cli`
- **Tags**: `latest`, version-specific tags (e.g., `v1.0.0`)

### Available Tags

- `latest` - Latest stable release
- `v1.0.0` - Specific version releases
- `main` - Development builds (if configured)

## Scripts Reference

### docker-build scripts
**Linux/macOS:**
```bash
# Build with default tag
./scripts/docker-build.sh

# Build with custom tag
./scripts/docker-build.sh v1.0.0
```

**Windows:**
```powershell
# PowerShell
.\scripts\docker-build.ps1 v1.0.0
```
```cmd
REM Command Prompt
scripts\docker-build.bat v1.0.0
```

### docker-publish scripts
**Linux/macOS:**
```bash
# Publish with default tag
./scripts/docker-publish.sh

# Publish with custom tag
./scripts/docker-publish.sh v1.0.0
```

**Windows:**
```powershell
# PowerShell
.\scripts\docker-publish.ps1 v1.0.0
```
```cmd
REM Command Prompt
scripts\docker-publish.bat v1.0.0
```

### docker-run scripts
**Linux/macOS:**
```bash
# Run with local image
./scripts/docker-run.sh --help

# Run with registry image
USE_REGISTRY=true ./scripts/docker-run.sh init MyProject

# Run with specific version
PKS_CLI_VERSION=v1.0.0 USE_REGISTRY=true ./scripts/docker-run.sh status
```

**Windows:**
```powershell
# PowerShell
.\scripts\docker-run.ps1 --help
$env:USE_REGISTRY="true"; .\scripts\docker-run.ps1 init MyProject
```
```cmd
REM Command Prompt
scripts\docker-run.bat --help
set USE_REGISTRY=true
scripts\docker-run.bat init MyProject
```

## Best Practices

1. **Always use specific tags in production**
2. **Mount only necessary directories**
3. **Use read-only mounts for configuration files**
4. **Regularly update to latest versions**
5. **Test locally before publishing**
6. **Use multi-stage builds for optimal image size**
7. **Follow semantic versioning for tags**

## Integration Examples

### Makefile Integration

```makefile
.PHONY: docker-build docker-publish docker-run

docker-build:
	./scripts/docker-build.sh $(VERSION)

docker-publish: docker-build
	./scripts/docker-publish.sh $(VERSION)

docker-run:
	USE_REGISTRY=true ./scripts/docker-run.sh $(ARGS)
```

### Shell Functions

```bash
# Add to ~/.bashrc or ~/.zshrc
pks-docker() {
    docker run --rm -it \
        -v $(pwd):/workspace \
        -v ~/.gitconfig:/home/pksuser/.gitconfig:ro \
        -w /workspace \
        registry.kjeldager.io/si14agents/cli:latest "$@"
}
```