# PKS CLI Docker Image Size Comparison

## Size Optimization Results

| Image Variant | Base Image | Size | Reduction | Use Case |
|---------------|------------|------|-----------|----------|
| **Original** | `mcr.microsoft.com/dotnet/aspnet:8.0` | ~500MB | - | Full ASP.NET runtime (oversized for CLI) |
| **Alpine Standard** | `mcr.microsoft.com/dotnet/runtime:8.0-alpine` | ~150-200MB | **60-70%** | Recommended for general use |
| **Alpine Minimal** | `alpine:3.18` (self-contained) | ~50-80MB | **80-90%** | Ultra-lightweight for CI/CD |

## Key Optimizations Applied

### 1. Base Image Selection
- **Original**: ASP.NET runtime (unnecessary for CLI tools)
- **Optimized**: .NET runtime only or pure Alpine Linux

### 2. Linux Distribution
- **Original**: Ubuntu-based (~70MB base)
- **Optimized**: Alpine Linux (~5MB base)

### 3. Package Management
- **Original**: `apt-get` with full package cache
- **Optimized**: `apk` with `--no-cache` flag

### 4. Deployment Strategy
- **Standard**: Framework-dependent deployment
- **Minimal**: Self-contained deployment (no .NET runtime needed)

## Build Commands

### Standard Alpine Image (Recommended)
```powershell
# Windows
.\scripts\docker-build.ps1

# Linux/macOS  
./scripts/docker-build.sh
```

### Minimal Alpine Image
```powershell
# Windows
.\scripts\docker-build.ps1 -Minimal

# Linux/macOS
./scripts/docker-build.sh --minimal
```

## Size Verification

After building, check the actual sizes:

```bash
# List all PKS CLI images with sizes
docker images pks-cli --format "table {{.Repository}}:{{.Tag}}\t{{.Size}}"

# Compare with registry images
docker images registry.kjeldager.io/si14agents/cli --format "table {{.Repository}}:{{.Tag}}\t{{.Size}}"
```

## Performance Impact

### Startup Time
- **Alpine images**: Faster startup due to smaller size
- **Self-contained**: Slightly slower first-run due to native compilation

### Pull Time
- **Original**: ~2-3 minutes on slow connections
- **Alpine Standard**: ~30-60 seconds  
- **Alpine Minimal**: ~15-30 seconds

### Resource Usage
- **Memory**: All variants use similar runtime memory
- **Disk**: Significant savings in container storage
- **Network**: Faster image distribution and deployment

## Recommendations

### For Development
Use **Alpine Standard** - good balance of size and compatibility:
```bash
docker run --rm -it registry.kjeldager.io/si14agents/cli:latest
```

### For CI/CD Pipelines
Use **Alpine Minimal** for fastest deployment:
```bash
docker run --rm -it registry.kjeldager.io/si14agents/cli:latest-minimal
```

### For Production
Choose based on your infrastructure:
- **Alpine Standard**: Better compatibility, easier debugging
- **Alpine Minimal**: Maximum efficiency, resource constrained environments

## Technical Details

### Standard Alpine Dockerfile Features
- Multi-stage build with .NET SDK and Alpine runtime
- Package installation: `git`, `curl`, `wget`, `unzip`, `bash`
- Framework-dependent deployment
- Standard .NET runtime optimizations

### Minimal Alpine Dockerfile Features  
- Self-contained single-file deployment
- Trimmed assemblies with `PublishTrimmed=true`
- Minimal package set: `git`, `curl`, `ca-certificates`
- No .NET runtime dependency
- ReadyToRun disabled for smaller size

### Security Considerations
Both variants maintain security best practices:
- Non-root user execution
- Minimal attack surface
- Regular Alpine Linux security updates
- No unnecessary tools or libraries