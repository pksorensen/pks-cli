# Semantic Release Documentation

## Overview

PKS CLI uses [semantic-release](https://github.com/semantic-release/semantic-release) to automate the release process. This system:

- Analyzes commit messages to determine version bumps
- Updates version numbers across all project files
- Generates changelogs automatically
- Creates GitHub releases with release notes
- Publishes packages to NuGet (when configured)

## How It Works

### 1. Commit Analysis

When commits are pushed to `main`, the semantic-release workflow:
1. Analyzes all commits since the last release
2. Determines the type of version bump needed
3. Generates release notes from commit messages

### 2. Version Bumping

Based on commit types:
- `feat:` → Minor version (1.0.0 → 1.1.0)
- `fix:` → Patch version (1.0.0 → 1.0.1)
- `perf:` → Patch version (1.0.0 → 1.0.1)
- `BREAKING CHANGE:` → Major version (1.0.0 → 2.0.0)

### 3. Release Process

1. **Version Update**: Updates all .csproj files with new version
2. **Changelog**: Updates CHANGELOG.md with changes
3. **Git Commit**: Commits version changes with `[skip ci]`
4. **Git Tag**: Creates version tag (e.g., `v1.2.3`)
5. **GitHub Release**: Creates release with notes and artifacts
6. **NuGet Publish**: Publishes packages (if API key is set)

## Configuration

### GitHub Secrets

Required secrets in repository settings:

```yaml
GITHUB_TOKEN: (automatically provided by GitHub Actions)
NUGET_API_KEY: (optional, for publishing to NuGet)
```

### Release Configuration

The release process is configured in `.releaserc.json`:

```json
{
  "branches": [
    "main",
    {"name": "release/*", "prerelease": true},
    {"name": "beta", "prerelease": true}
  ],
  "plugins": [
    "@semantic-release/commit-analyzer",
    "@semantic-release/release-notes-generator",
    "@semantic-release/changelog",
    "@semantic-release/exec",
    "@semantic-release/git",
    "@semantic-release/github"
  ]
}
```

## Commit Message Format

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Examples

#### Feature Release (Minor)
```bash
git commit -m "feat(init): add support for Blazor templates"
# Results in: 1.0.0 → 1.1.0
```

#### Bug Fix Release (Patch)
```bash
git commit -m "fix(mcp): resolve connection timeout issue"
# Results in: 1.1.0 → 1.1.1
```

#### Breaking Change (Major)
```bash
git commit -m "feat!: change configuration format

BREAKING CHANGE: Configuration now uses YAML instead of JSON"
# Results in: 1.1.1 → 2.0.0
```

## Workflow Triggers

The semantic release workflow runs:
- On push to `main` branch
- On push to `release/*` branches (as pre-release)
- Manual trigger via GitHub Actions UI

## Pre-releases

### Release Branch Strategy

PKS CLI uses **release branches** for pre-release management:

1. **Create release branch**: `release/1.0.0`
2. **Create feature branches**: `feat/semantic-release`
3. **PR to release branch**: Creates `v1.0.0-pre.1`, `v1.0.0-pre.2`, etc.
4. **Merge to main**: Creates final `v1.0.0` when ready

### Pre-release Workflow

```bash
# Start new release cycle
git checkout main
git checkout -b release/1.0.0

# Create feature
git checkout -b feat/new-feature
git commit -m "feat(scope): implement new feature"

# PR: feat/new-feature → release/1.0.0
# Result: Creates v1.0.0-pre.1

# Add more features
git checkout -b fix/bug-fix
git commit -m "fix(scope): resolve critical bug"

# PR: fix/bug-fix → release/1.0.0  
# Result: Creates v1.0.0-pre.2

# Ready for production
# PR: release/1.0.0 → main
# Result: Creates v1.0.0 (final release)
```

### Benefits

- **Quality Assurance**: Test pre-releases before production
- **Team Collaboration**: Multiple developers can test pre-release packages
- **User Feedback**: Early adopters can provide feedback on pre-releases
- **Rollback Safety**: Issues in pre-releases don't affect main branch

## Manual Release

To trigger a release manually:

1. Go to Actions → Semantic Release
2. Click "Run workflow"
3. Select branch and run

## Troubleshooting

### No Release Created

Check if commits follow conventional format:
```bash
# Valid
feat: add new feature
fix: resolve bug

# Invalid
Add new feature
Fixed bug
```

### Version Not Updated

Ensure:
1. Commits are conventional format
2. Changes warrant a release (not just chore/docs)
3. Workflow has write permissions

### NuGet Publish Failed

1. Verify `NUGET_API_KEY` is set in secrets
2. Check package doesn't already exist
3. Ensure package metadata is valid

## Best Practices

1. **Commit Messages**: Always use conventional format
2. **Breaking Changes**: Clearly document in commit body
3. **Scopes**: Use consistent scopes (init, agent, mcp, etc.)
4. **PR Titles**: Use conventional format for squash merges
5. **Testing**: Ensure CI passes before merging to main

## Release Notes Format

Release notes are automatically generated:

```markdown
## 🚀 Features
- **init**: add support for Blazor templates (#123)
- **mcp**: implement SSE transport mode (#124)

## 🐛 Bug Fixes
- **deploy**: fix timeout during large deployments (#125)
- **hooks**: resolve path issue on Windows (#126)

## 📚 Documentation
- update installation guide for macOS users (#127)

## BREAKING CHANGES
- Configuration format changed from JSON to YAML
```

## Integration with Development

### Local Testing

Test commit analysis locally:
```bash
npm install
npx semantic-release --dry-run --no-ci
```

### Branch Protection

Recommended branch protection for `main`:
- Require PR reviews
- Require status checks (build, test)
- Include administrators
- Allow force pushes (for semantic-release bot)

## Version Locations

Versions are updated in:
- `src/pks-cli.csproj`
- `templates/**/*.csproj`
- `CHANGELOG.md`
- GitHub releases
- Git tags

## FAQ

**Q: Can I trigger a release without code changes?**
A: Yes, use an empty commit:
```bash
git commit --allow-empty -m "chore: trigger release"
```

**Q: How do I release a specific version?**
A: Semantic release determines versions automatically. For specific versions, create a manual release.

**Q: What if I need to fix a release?**
A: Create a new commit with the fix. Avoid modifying existing releases.

**Q: How do I do a hotfix?**
A: Create a hotfix branch from the tag, fix, then merge to main.

## Additional Resources

- [Semantic Release Documentation](https://semantic-release.gitbook.io/)
- [Conventional Commits](https://www.conventionalcommits.org/)
- [Semantic Versioning](https://semver.org/)