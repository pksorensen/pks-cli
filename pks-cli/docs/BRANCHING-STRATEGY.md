# Branching Strategy and Release Workflow

This document describes the branching strategy and release workflow for PKS CLI.

## Overview

PKS CLI uses a **release branch strategy** with semantic versioning and automated pre-releases to ensure production quality while enabling rapid iteration.

## Branch Structure

```
main                    (production releases: v1.0.0, v1.1.0, v2.0.0)
├── release/1.0.0      (pre-releases: v1.0.0-pre.1, v1.0.0-pre.2)
│   ├── feat/add-mcp   (feature branch)
│   ├── feat/add-agent (feature branch)
│   └── fix/timeout    (bugfix branch)
├── release/1.1.0      (next version pre-releases)
└── develop            (dev releases: v0.0.0-dev.1, v0.0.0-dev.2)
```

## Workflow

### 1. Feature Development

**Create feature branches for new work:**
```bash
# From main, create release branch for next version
git checkout -b release/1.0.0

# Create feature branch from release branch
git checkout -b feat/semantic-release

# Work on feature
git commit -m "feat(release): implement semantic release automation"
git commit -m "docs(release): add branching strategy documentation" 

# Push feature branch
git push origin feat/semantic-release
```

### 2. Pre-Release Testing

**Merge features into release branch:**
```bash
# Create PR: feat/semantic-release → release/1.0.0
# After merge, automatic pre-release is triggered
```

**Pre-release behavior:**
- Commits to `release/1.0.0` → creates `v1.0.0-pre.1`, `v1.0.0-pre.2`, etc.
- Each commit increments the pre-release number
- Published to NuGet as pre-release packages
- GitHub releases marked as "Pre-release"

### 3. Production Release

**When release branch is ready:**
```bash
# Create PR: release/1.0.0 → main
# After merge, final release is created: v1.0.0
```

**Production release behavior:**
- Merge to `main` → creates final version `v1.0.0`
- Published to NuGet as stable release
- Full GitHub release with complete changelog

## Version Strategy

### Pre-Release Versioning

Release branches use **predictable pre-release versioning**:

| Branch | First Commit | Second Commit | Third Commit |
|--------|-------------|---------------|--------------|
| `release/1.0.0` | `v1.0.0-pre.1` | `v1.0.0-pre.2` | `v1.0.0-pre.3` |
| `release/1.1.0` | `v1.1.0-pre.1` | `v1.1.0-pre.2` | `v1.1.0-pre.3` |
| `release/2.0.0` | `v2.0.0-pre.1` | `v2.0.0-pre.2` | `v2.0.0-pre.3` |

### Final Release Versioning

When merged to `main`, the final version is determined by:
- **Branch name**: `release/1.0.0` → `v1.0.0`
- **Semantic analysis**: Commit types since last release may influence version
- **Breaking changes**: May bump major version regardless of branch name

## Practical Examples

### Example 1: New Feature (Minor Release)

```bash
# Start new release cycle
git checkout main
git pull origin main
git checkout -b release/1.1.0

# Create feature
git checkout -b feat/new-template-engine
git commit -m "feat(templates): add Liquid template engine support"
git push origin feat/new-template-engine

# PR: feat/new-template-engine → release/1.1.0
# After merge: Creates v1.1.0-pre.1

# Add more features
git checkout -b feat/template-validation
git commit -m "feat(templates): add template validation"
# PR → release/1.1.0
# After merge: Creates v1.1.0-pre.2

# Ready for production
# PR: release/1.1.0 → main
# After merge: Creates v1.1.0 (final)
```

### Example 2: Hotfix

```bash
# Create hotfix from main
git checkout main
git checkout -b release/1.0.1

# Fix critical bug
git checkout -b fix/critical-security-issue
git commit -m "fix(auth): resolve OAuth token validation vulnerability"

# PR: fix/critical-security-issue → release/1.0.1
# After merge: Creates v1.0.1-pre.1

# Quick verification and release
# PR: release/1.0.1 → main
# After merge: Creates v1.0.1 (final)
```

### Example 3: Breaking Change

```bash
# Start major version
git checkout main
git checkout -b release/2.0.0

# Implement breaking change
git checkout -b feat/new-config-format
git commit -m "feat!: migrate configuration from JSON to YAML

BREAKING CHANGE: Configuration files must now use YAML format.
See migration guide in docs/MIGRATION.md"

# PR → release/2.0.0
# After merge: Creates v2.0.0-pre.1
```

## Benefits

### 🎯 **Quality Assurance**
- Pre-releases allow thorough testing before production
- Multiple team members can test pre-release packages
- Issues discovered in pre-release don't affect main

### 🚀 **Fast Iteration**
- Feature branches enable parallel development
- Pre-releases provide immediate feedback
- No waiting for "release day"

### 📦 **Package Management**
- Pre-release packages available in NuGet
- Users can opt into pre-releases for early access
- Stable users unaffected by pre-release churn

### 🔄 **Automation**
- Zero manual version management
- Automatic changelog generation
- Consistent release notes format

## Branch Protection Rules

### Recommended Settings

**Main Branch:**
- Require PR reviews (1+ reviewers)
- Require status checks (build, test, semantic-release)
- Include administrators
- Allow semantic-release bot to push

**Release Branches:**
- Require PR reviews (1+ reviewers)
- Require status checks (build, test)
- Allow force pushes (for semantic-release)

## Team Workflow

### For Feature Development

1. **Create feature branch** from target release branch
2. **Develop and test** locally
3. **Create PR** to release branch
4. **Code review** and approval
5. **Merge to release** → automatic pre-release
6. **Test pre-release** packages
7. **Repeat** until release branch ready

### For Release Management

1. **Monitor pre-releases** for stability
2. **Coordinate with team** on release readiness
3. **Create PR** from release to main
4. **Final review** and approval
5. **Merge to main** → automatic production release

### For Hotfixes

1. **Create release branch** from main (e.g., `release/1.0.1`)
2. **Fix issue** in feature branch
3. **PR to release branch** → pre-release for testing
4. **Verify fix** in pre-release
5. **PR to main** → hotfix release

## Commands Cheat Sheet

```bash
# Start new release cycle
git checkout main && git pull
git checkout -b release/X.Y.Z

# Create feature
git checkout -b feat/feature-name
# ... work ...
git commit -m "feat(scope): description"

# Create PR to release branch
gh pr create --base release/X.Y.Z --title "feat: ..."

# When release ready
gh pr create --base main --title "Release X.Y.Z"

# Check releases
gh release list

# Install pre-release
dotnet tool install -g pks-cli --version X.Y.Z-pre.N --prerelease
```

## Integration with Issue Tracking

Link commits to issues for better traceability:

```bash
git commit -m "feat(init): add Blazor template support

Implements new Blazor WebAssembly and Server templates
with proper project structure and dependencies.

Closes #42"
```

This creates a complete audit trail from issue → feature → pre-release → production.

## Troubleshooting

### Pre-release Not Created
- Check branch name format: `release/X.Y.Z`
- Verify conventional commit format
- Check GitHub Actions logs

### Wrong Version Number
- Semantic-release analyzes commits to determine version
- Branch name provides base version for pre-releases
- Breaking changes may override branch version

### NuGet Publish Failed
- Check `NUGET_API_KEY` in repository secrets
- Verify package doesn't already exist
- Review package metadata for errors

This strategy ensures high quality releases while maintaining development velocity! 🚀