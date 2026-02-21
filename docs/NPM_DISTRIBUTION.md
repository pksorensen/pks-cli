# npm Distribution Guide

This document provides comprehensive information about the npm distribution of PKS CLI.

## Overview

PKS CLI is distributed via npm as self-contained .NET binaries, enabling users to run the tool without installing the .NET SDK. This distribution strategy follows proven patterns from tools like esbuild, turbo, and prisma.

## Architecture

### Package Structure

The npm distribution consists of 7 packages:

1. **Main Wrapper Package** (`@pks-cli/pks`)
   - Detects platform and architecture
   - Downloads correct binary via optionalDependencies
   - Forwards all commands to platform-specific binary

2. **Platform-Specific Packages** (6 total)
   - `@pks-cli/pks-linux-x64` - Linux x64
   - `@pks-cli/pks-linux-arm64` - Linux ARM64
   - `@pks-cli/pks-osx-x64` - macOS Intel
   - `@pks-cli/pks-osx-arm64` - macOS Apple Silicon
   - `@pks-cli/pks-win-x64` - Windows x64
   - `@pks-cli/pks-win-arm64` - Windows ARM64

### How It Works

```
┌─────────────────────────────────────────────┐
│ @pks-cli/pks (Main Package)                │
│  - bin/pks.js (Platform detection)         │
│  - postinstall.js (Verification)           │
│  - optionalDependencies (6 platforms)      │
└─────────────────────────────────────────────┘
                    │
        ┌───────────┴───────────┐
        ▼                       ▼
┌──────────────────┐    ┌──────────────────┐
│ Platform Package │    │ Platform Package │
│  linux-x64       │    │  osx-arm64       │
│  - bin/pks       │    │  - bin/pks       │
└──────────────────┘    └──────────────────┘
```

### Platform Detection

The wrapper script (`bin/pks.js`) performs automatic platform detection:

```javascript
const { platform, arch } = process;
// Maps to: linux-x64, darwin-arm64, win32-x64, etc.
```

## Installation

### Global Installation

```bash
# Install stable release (latest)
npm install -g @pks-cli/pks

# Install release candidate (vnext branch)
npm install -g @pks-cli/pks@rc

# Install development version (develop branch)
npm install -g @pks-cli/pks@dev

# Use the tool
pks init MyProject
```

### npx Usage (No Installation)

```bash
# Run stable release
npx @pks-cli/pks init MyProject

# Run release candidate
npx @pks-cli/pks@rc init MyProject

# Run development version
npx @pks-cli/pks@dev init MyProject

# Specific version
npx @pks-cli/pks@1.0.0 --version
npx @pks-cli/pks@1.0.0-rc.1 --version
```

### Local Project Installation

```bash
# Add to project devDependencies
npm install --save-dev @pks-cli/pks

# Use in package.json scripts
{
  "scripts": {
    "init": "pks init",
    "deploy": "pks deploy"
  }
}
```

## Build Process

### Self-Contained Builds

PKS CLI uses .NET's self-contained deployment with `PublishSingleFile`:

```xml
<PropertyGroup Condition="'$(PublishSelfContained)' == 'true'">
  <SelfContained>true</SelfContained>
  <PublishSingleFile>true</PublishSingleFile>
  <PublishTrimmed>false</PublishTrimmed>
  <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  <EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
  <PublishReadyToRun>true</PublishReadyToRun>
  <EmbedTemplates>true</EmbedTemplates>
</PropertyGroup>
```

**Key Decisions:**
- **No Native AOT**: Preserves Spectre.Console.Cli reflection support
- **PublishTrimmed=false**: Ensures full framework compatibility
- **EmbedTemplates**: Templates are embedded as resources in binaries

### Build Script

Local builds use `scripts/publish-self-contained.sh`:

```bash
./scripts/publish-self-contained.sh 1.0.0 ./npm-dist
```

This script:
1. Builds all 6 platform binaries
2. Shows size information for each
3. Validates successful builds
4. Outputs to specified directory

## CI/CD Pipeline

### Workflow Architecture

```
semantic-release.yml (Orchestrator)
    │
    ├── detect-changes (Determines if CLI changed)
    │
    ├── release-npm.yml
    │   ├── semantic-release (Determines version)
    │   ├── build-npm-binaries.yml (Parallel builds)
    │   │   ├── linux-x64 (ubuntu-latest)
    │   │   ├── linux-arm64 (ubuntu-latest)
    │   │   ├── osx-x64 (macos-13)
    │   │   ├── osx-arm64 (macos-14)
    │   │   ├── win-x64 (windows-latest)
    │   │   └── win-arm64 (windows-latest)
    │   ├── create-npm-packages.yml
    │   │   ├── Download all binary artifacts
    │   │   ├── Copy binaries to npm packages
    │   │   ├── Update versions
    │   │   └── Create .tgz packages
    │   ├── publish-npm
    │   │   ├── Publish platform packages
    │   │   └── Publish main package
    │   └── create-github-release
    │       └── Create npm-v{version} release
    │
    └── release-cli.yml (Parallel NuGet release)
```

### Multi-Platform Builds

GitHub Actions matrix strategy builds all 6 platforms in parallel:

```yaml
strategy:
  fail-fast: false
  matrix:
    include:
      - os: ubuntu-latest
        rid: linux-x64
      - os: macos-14
        rid: osx-arm64
      # ... etc
```

### Version Management

Versions are synchronized across:
- npm packages (npm-v1.0.0 tags)
- NuGet packages (v1.0.0 tags)
- Git tags are independent

Script: `scripts/sync-npm-versions.sh`

## Release Channels

PKS CLI npm distribution follows the same branching/channel strategy as NuGet releases:

| Branch | Channel | Version Format | npm Tag | Usage |
|--------|---------|----------------|---------|-------|
| **main** | Stable | `1.0.0` | `latest` | `npm install @pks-cli/pks` |
| **vnext** | Release Candidate | `1.0.0-rc.1` | `rc` | `npm install @pks-cli/pks@rc` |
| **develop** | Development | `1.0.0-dev.1` | `dev` | `npm install @pks-cli/pks@dev` |

### Using Different Channels

```bash
# Stable release (production)
npm install -g @pks-cli/pks@latest
npx @pks-cli/pks init MyProject

# Release candidate (pre-production testing)
npm install -g @pks-cli/pks@rc
npx @pks-cli/pks@rc init MyProject

# Development (bleeding edge)
npm install -g @pks-cli/pks@dev
npx @pks-cli/pks@dev init MyProject
```

### Version Lifecycle

1. **Development** (`develop` branch)
   - `1.0.0-dev.1`, `1.0.0-dev.2`, etc.
   - Active development and experimental features
   - May be unstable

2. **Release Candidate** (`vnext` branch)
   - `1.0.0-rc.1`, `1.0.0-rc.2`, etc.
   - Feature-complete, stabilization phase
   - Ready for testing before stable release

3. **Stable** (`main` branch)
   - `1.0.0`, `1.1.0`, `2.0.0`, etc.
   - Production-ready releases
   - Recommended for most users

## Semantic Release Configuration

### `.releaserc.npm.json`

```json
{
  "tagFormat": "npm-v${version}",
  "branches": [
    "main",
    {
      "name": "vnext",
      "prerelease": "rc"
    },
    {
      "name": "develop",
      "prerelease": "dev"
    }
  ],
  "plugins": [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    "@semantic-release/exec",
    "@semantic-release/changelog",
    "@semantic-release/git",
    "@semantic-release/github"
  ]
}
```

**Key Features:**
- Separate tag format: `npm-v1.0.0` vs `v1.0.0` (NuGet)
- Independent versioning from NuGet distribution
- Multi-channel support (latest/rc/dev)
- Changelog at `npm/CHANGELOG.md`
- Conventional commit parsing

## Publishing

### Prerequisites

1. **npm Account**: Organization `@pks-cli` must exist
2. **npm Token**: `NPM_TOKEN` secret configured in GitHub
3. **Permissions**: Publishing rights for all packages

### Manual Publishing

```bash
# Build binaries
./scripts/publish-self-contained.sh 1.0.0

# Create npm packages
cd npm

# Publish platform packages first
for platform in linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64 win-arm64; do
  cd pks-cli-$platform
  npm publish --access public
  cd ..
done

# Publish main package last
cd pks-cli
npm publish --access public
```

### Automated Publishing (CI/CD)

Releases are automated via GitHub Actions:
1. Push to main/vnext/develop branch
2. CI detects CLI changes
3. Semantic-release determines version
4. Binaries built in parallel
5. Packages created and published
6. GitHub release created with npm-v{version} tag

## Testing

### Unit Tests

Located in `tests/Npm/`:
- `NpmPackageStructureTests.cs` - Package.json validation
- `NpmWrapperTests.cs` - Platform detection logic
- `SelfContainedBuildTests.cs` - Build output validation

Run tests:
```bash
dotnet test --filter Category=Npm
```

### Integration Tests

Located in `tests/Integration/Npm/`:
- `NpmInstallTests.cs` - Real npm install/npx tests

**Note**: Integration tests require published packages and are skipped by default.

### Local Testing

Test packages locally before publishing:

```bash
# Build self-contained binaries
./scripts/publish-self-contained.sh 1.0.0

# Create packages
./scripts/create-npm-packages.js

# Install from local .tgz
npm install -g dist/pks-cli-pks-1.0.0.tgz

# Test the installation
pks --version
```

## Troubleshooting

### Binary Not Found

**Symptom:** "PKS CLI binary not found" error after installation

**Solutions:**
```bash
# Force reinstall
npm install -g @pks-cli/pks --force

# Manually install platform package
npm install -g @pks-cli/pks-linux-x64  # Your platform

# Check npm prefix
npm config get prefix
```

### Unsupported Platform

**Symptom:** "Unsupported platform" error

**Solution:** Use .NET global tool instead:
```bash
dotnet tool install -g pks-cli
```

### Postinstall Failures

**Symptom:** Warnings during installation

**Common Causes:**
- Corporate proxy blocking downloads
- optionalDependencies skipped
- Incorrect npm configuration

**Solutions:**
```bash
# Disable strict SSL (if behind corporate proxy)
npm config set strict-ssl false

# Ensure optional dependencies are installed
npm install --no-optional=false

# Use registry mirror if needed
npm config set registry https://registry.npmjs.org/
```

### Permission Errors (Linux/macOS)

**Symptom:** EACCES errors during global install

**Solution:**
```bash
# Fix npm permissions (recommended)
mkdir ~/.npm-global
npm config set prefix '~/.npm-global'
export PATH=~/.npm-global/bin:$PATH

# Or use sudo (not recommended)
sudo npm install -g @pks-cli/pks
```

## Comparison: npm vs .NET Tool

| Feature | npm Distribution | .NET Global Tool |
|---------|------------------|------------------|
| **Installation** | `npm install -g @pks-cli/pks` | `dotnet tool install -g pks-cli` |
| **Prerequisites** | Node.js 18+ | .NET 10 SDK |
| **Binary Size** | ~50-70MB | ~10MB + runtime |
| **Startup Time** | Fast (pre-compiled) | Fast (JIT) |
| **Updates** | `npm update -g @pks-cli/pks` | `dotnet tool update -g pks-cli` |
| **Run without install** | `npx @pks-cli/pks` | `dotnet tool run pks-cli` |
| **Prerelease Access** | `npm install -g @pks-cli/pks@rc` | `dotnet tool install -g pks-cli --prerelease` |
| **Channel Support** | `@latest`, `@rc`, `@dev` | Stable + `--prerelease` flag |
| **Platform Support** | 6 platforms | All .NET platforms |
| **Template Access** | Embedded | File-based |
| **CI/CD Friendly** | ✅ Excellent | ✅ Excellent |
| **Offline Support** | ✅ Yes (cached) | ⚠️ Requires runtime |
| **Best For** | Non-.NET users | .NET developers |

## Advantages of npm Distribution

1. **Zero .NET SDK Dependency**
   - Users don't need .NET installed
   - Ideal for non-.NET developers
   - Simplified CI/CD setup

2. **Self-Contained Binaries**
   - Single-file executables
   - No runtime dependencies
   - Fast startup with ReadyToRun

3. **Automatic Platform Selection**
   - npm installs correct binary automatically
   - No manual platform detection needed

4. **Familiar Ecosystem**
   - npm/npx are widely known
   - Integrates with existing npm workflows
   - Standard package.json integration

5. **Dual Distribution Strategy**
   - Complements NuGet distribution
   - Reaches broader audience
   - Maintains feature parity

## Maintenance

### Adding New Platforms

To add a new platform (e.g., `linux-musl-x64`):

1. Update `scripts/publish-self-contained.sh`:
   ```bash
   PLATFORMS=("linux-x64" "linux-arm64" "linux-musl-x64" ...)
   ```

2. Create package directory:
   ```bash
   mkdir npm/pks-cli-linux-musl-x64
   ```

3. Add package.json with correct os/cpu constraints

4. Update CI/CD workflows (build matrix)

5. Update main package optionalDependencies

### Updating Dependencies

Main package dependencies are minimal:
- None currently (pure Node.js built-ins)

Platform packages have no dependencies (just binaries).

### Version Updates

Automated via semantic-release, but manual updates possible:

```bash
# Update all package versions
./scripts/sync-npm-versions.sh 1.2.3

# Verify versions
grep -r '"version"' npm/*/package.json
```

## Future Enhancements

Potential improvements for future releases:

1. **Binary Size Optimization**
   - Investigate Native AOT (requires Spectre.Console.Cli replacement)
   - Template compression
   - Selective trimming

2. **Additional Platforms**
   - Alpine Linux (musl)
   - FreeBSD
   - Other architectures

3. **Enhanced CI/CD**
   - Automated integration tests
   - Beta channel distribution
   - Release candidate workflow

4. **Download Optimization**
   - CDN distribution
   - Incremental updates
   - Background downloads

5. **Verification**
   - Binary signing
   - Checksum validation
   - SBOM generation

## Resources

- [npm Organization](https://www.npmjs.com/org/pks-cli)
- [Main Package](https://www.npmjs.com/package/@pks-cli/pks)
- [GitHub Releases](https://github.com/pksorensen/pks-cli/releases?q=npm-v)
- [Issue Tracker](https://github.com/pksorensen/pks-cli/issues)

## Contributing

To contribute to npm distribution:

1. Test locally before pushing
2. Follow semantic commit conventions
3. Update documentation for changes
4. Ensure CI/CD passes
5. Verify all 6 platforms build successfully

See [CONTRIBUTING.md](../CONTRIBUTING.md) for general guidelines.

## Support

For npm distribution issues:
1. Check [Troubleshooting](#troubleshooting) section
2. Search [existing issues](https://github.com/pksorensen/pks-cli/issues)
3. Create new issue with:
   - Platform information
   - npm version (`npm --version`)
   - Node.js version (`node --version`)
   - Error messages and logs

---

**Last Updated:** January 2026
**Distribution Version:** 1.0.0
