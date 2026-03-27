# Contributing to PKS CLI

Thank you for contributing to PKS CLI! This guide covers the development workflow, release process, and conventions.

## Branch Strategy

PKS CLI uses a **single-branch model** with `main` as the only long-lived branch:

- All PRs target **main**
- Every push to main publishes **preview packages** automatically (`X.Y.Z-preview.{build}`)
- [Release Please](https://github.com/googleapis/release-please) creates **Release PRs** on main
- Merging a Release PR publishes **stable releases** to NuGet and npm

```
feature branch → PR to main → merge
                                 ↓
              CI publishes preview package (6.3.0-preview.42)
              Release Please updates the Release PR
                                 ↓
              When ready: merge the Release PR
                                 ↓
              Stable release published (6.3.0)
```

The Release PR is your gate. Features accumulate on main, Release Please keeps updating the PR with the changelog, and you merge it when you're ready to cut a release.

## Getting Started

```bash
# Clone the repository
git clone https://github.com/pksorensen/pks-cli.git
cd pks-cli

# Build and test
dotnet build PKS.CLI.sln
dotnet test

# Run locally without installing
cd src
dotnet run -- [command] [options]
```

## Making Changes

```bash
# 1. Create feature branch from main
git checkout main
git pull origin main
git checkout -b feat/my-feature

# 2. Develop with conventional commits
git commit -m "feat(init): add Blazor template support"

# 3. Push and open PR against main
git push origin feat/my-feature
gh pr create --base main --title "feat(init): add Blazor template support"
```

## Commit Message Convention

This project uses [Conventional Commits](https://www.conventionalcommits.org/) to drive automatic versioning and changelog generation.

### Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

| Type | Description | Version Bump |
|------|-------------|-------------|
| `feat` | New feature | Minor (1.0.0 → 1.1.0) |
| `fix` | Bug fix | Patch (1.0.0 → 1.0.1) |
| `perf` | Performance improvement | Patch |
| `docs` | Documentation only | No bump |
| `style` | Formatting, no code change | No bump |
| `refactor` | Code restructuring | No bump |
| `test` | Adding/modifying tests | No bump |
| `build` | Build system/dependencies | No bump |
| `ci` | CI configuration | No bump |
| `chore` | Maintenance | No bump |
| `revert` | Revert a commit | Patch |

### Breaking Changes

Breaking changes trigger a **major** version bump:

```bash
# Using ! after type/scope
feat!: change configuration file format

# Or using BREAKING CHANGE footer
feat: remove deprecated API endpoints

BREAKING CHANGE: The /api/v1/* endpoints have been removed.
Use /api/v2/* instead.
```

### Scopes

Scopes map to Release Please package components:

| Scope | Package | Example |
|-------|---------|---------|
| `cli` | pks-cli (main CLI) | `feat(cli): add status command` |
| `init` | CLI init command | `fix(init): handle spaces in project name` |
| `agent` | Agent management | `feat(agent): add list subcommand` |
| `mcp` | MCP integration | `fix(mcp): resolve SSE timeout` |
| `deploy` | Deployment features | `feat(deploy): add Coolify support` |
| `hooks` | Git hooks | `fix(hooks): correct validation logic` |
| `devcontainer` | DevContainer template | `feat(devcontainer): add Node.js support` |
| `claude-dotnet-9` | Claude .NET 9 template | `fix(claude-dotnet-9): update base image` |
| `claude-dotnet-10-full` | Claude .NET 10 template | `feat(claude-dotnet-10-full): add Aspire` |
| `pks-fullstack` | Fullstack template | `feat(pks-fullstack): add Blazor frontend` |
| `templates` | All templates | `fix(templates): normalize line endings` |

### Tips

1. Keep the subject line under 50 characters
2. Use imperative mood ("add feature" not "added feature")
3. Don't end the subject line with a period
4. Separate subject from body with a blank line
5. Use the body to explain *what* and *why*, not *how*
6. Reference issues in the footer (`Closes #42`)

## Pull Request Workflow

1. Open PRs against **main**
2. CI runs automatically: format check, build, tests, hooks validation
3. Get at least one review approval
4. Squash merge preferred for clean commit history
5. Delete your feature branch after merge

### CI Checks

Every PR must pass:

- `dotnet format --verify-no-changes` — code formatting
- `dotnet build --warnaserror` — clean build
- `dotnet test --filter "Category=Core&Reliability!=Unstable"` — core tests
- Hooks validation (PR only)

## How Release Please Works

PKS CLI uses Release Please in **manifest mode** with per-package versioning.

### Packages

| Package | Path | Tag Format | NuGet ID |
|---------|------|------------|----------|
| pks-cli | `src/` | `v6.2.0` | `pks-cli` |
| DevContainer | `templates/devcontainer/` | `devcontainer-v5.0.0` | `PKS.Templates.DevContainer` |
| Claude .NET 9 | `templates/claude-dotnet-9/` | `claude-dotnet-9-v4.0.0` | `PKS.Templates.ClaudeDotNet9` |
| Claude .NET 10 Full | `templates/claude-dotnet-10-full/` | `claude-dotnet-10-full-v5.0.0` | `PKS.Templates.ClaudeDotNet10.Full` |
| PksFullstack | `templates/pks-fullstack/` | `pks-fullstack-v4.1.0` | `PKS.Templates.PksFullstack` |

### Configuration Files

| File | Purpose |
|------|---------|
| `release-please-config.json` | Package definitions and release settings |
| `.release-please-manifest.json` | Per-package version tracking |
| `src/version.txt` | CLI version (managed by Release Please) |
| `templates/*/version.txt` | Template versions (managed by Release Please) |

### The Release Cycle

```
1. Merge PRs with conventional commits into main
       ↓
2. CI publishes preview NuGet packages (X.Y.Z-preview.{build})
       ↓
3. Release Please creates/updates a Release PR with changelog
       ↓
4. Review the Release PR — it previews the version bump and changelog
       ↓
5. Merge the Release PR when ready → triggers:
   • NuGet stable package publish
   • npm stable package publish (@latest tag)
   • GitHub Release creation
```

## Preview Packages

Every push to main automatically publishes preview packages, separate from Release Please:

- **NuGet**: `pks-cli` version `X.Y.Z-preview.{build_number}`
- Install with: `dotnet tool install -g pks-cli --prerelease`

These allow testing the latest code without waiting for a stable release.

## Testing

```bash
# Full test suite
dotnet test

# Core tests only (matches CI)
dotnet test --filter "Category=Core&Reliability!=Unstable"

# Format check
dotnet format --verify-no-changes PKS.CLI.sln
```

### Before Submitting a PR

1. Run `dotnet format PKS.CLI.sln` to fix formatting
2. Run `dotnet build --warnaserror` to check for warnings
3. Run `dotnet test` to ensure all tests pass
4. Verify commit messages follow conventional commit format

## Utility Scripts

| Script | Usage | Purpose |
|--------|-------|---------|
| `scripts/get-package-version.sh` | `./scripts/get-package-version.sh [scope]` | Read current version |
| `scripts/update-version.sh` | `./scripts/update-version.sh <version> [scope]` | Update version in csproj/version.txt |
| `scripts/sync-npm-versions.sh` | `./scripts/sync-npm-versions.sh` | Sync npm package versions |

Scopes: `cli`, `devcontainer`, `claude-dotnet-9`, `claude-dotnet-10-full`, `pks-fullstack`, `all`

## Questions?

Open an issue at [pksorensen/pks-cli](https://github.com/pksorensen/pks-cli/issues)!
