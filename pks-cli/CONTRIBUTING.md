# Contributing to PKS CLI

## Commit Message Convention

This project uses [Conventional Commits](https://www.conventionalcommits.org/) to enable automatic versioning and release notes generation.

### Commit Message Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- **feat**: A new feature (triggers minor version bump)
- **fix**: A bug fix (triggers patch version bump)
- **docs**: Documentation only changes
- **style**: Changes that do not affect code functionality (formatting, missing semicolons, etc.)
- **refactor**: Code changes that neither fix bugs nor add features
- **perf**: Performance improvements (triggers patch version bump)
- **test**: Adding or modifying tests
- **build**: Changes to the build system or dependencies
- **ci**: Changes to CI configuration files and scripts
- **chore**: Other changes that don't modify src or test files
- **revert**: Reverts a previous commit (triggers patch version bump)

### Breaking Changes

Breaking changes trigger a major version bump. They can be indicated in two ways:

1. Add `BREAKING CHANGE:` in the commit footer:
   ```
   feat: remove deprecated API endpoints
   
   BREAKING CHANGE: The /api/v1/* endpoints have been removed.
   Use /api/v2/* instead.
   ```

2. Add `!` after the type/scope:
   ```
   feat!: change configuration file format
   ```

### Examples

#### Feature
```
feat(init): add support for Blazor templates

Added new Blazor WebAssembly and Blazor Server templates
to the project initialization options.
```

#### Bug Fix
```
fix(mcp): resolve connection timeout in SSE transport

Increased default timeout from 30s to 60s to handle
slower network connections.

Fixes #123
```

#### Breaking Change
```
feat(api)!: change authentication method to OAuth 2.0

BREAKING CHANGE: Basic authentication is no longer supported.
All API requests must now use OAuth 2.0 tokens.

Migration guide: https://docs.example.com/migration
```

#### Documentation
```
docs(readme): update installation instructions

Added instructions for macOS and Windows users.
```

### Scope Examples

Common scopes for PKS CLI:

- **init**: Project initialization command
- **agent**: Agent management features
- **mcp**: Model Context Protocol integration
- **deploy**: Deployment features
- **hooks**: Git hooks integration
- **prd**: Product Requirements Document features
- **cli**: General CLI functionality
- **templates**: Project templates
- **docs**: Documentation
- **tests**: Test suite
- **ci**: CI/CD pipeline

### Tips

1. Keep the subject line under 50 characters
2. Use the imperative mood ("add feature" not "added feature")
3. Don't end the subject line with a period
4. Separate subject from body with a blank line
5. Use the body to explain what and why, not how
6. Reference issues and PRs in the footer

## Pull Request Guidelines

1. **For new features**: Create branches from relevant `release/*` branch
2. **For hotfixes**: Create branches from `main` or create new `release/*` branch
3. Name branches descriptively: `feat/add-blazor-templates` or `fix/connection-timeout`
4. Ensure all tests pass before submitting PR
5. Update documentation if needed
6. Follow the commit message convention for all commits

## Development Workflow

### Standard Feature Development

1. **Choose target release**: Create or checkout `release/X.Y.Z` branch
2. **Create feature branch**: `git checkout -b feat/amazing-feature`
3. **Develop and commit**: Using conventional commit format
4. **Push feature branch**: `git push origin feat/amazing-feature`
5. **Open PR to release branch**: `feat/amazing-feature` → `release/X.Y.Z`
6. **Test pre-release**: Use generated `vX.Y.Z-pre.N` package
7. **Production release**: Merge `release/X.Y.Z` → `main` when ready

### Hotfix Workflow

1. **Create release branch**: `git checkout -b release/X.Y.Z` from main
2. **Create hotfix branch**: `git checkout -b fix/critical-issue`
3. **Fix and commit**: Using conventional commit format
4. **PR to release branch**: Test with pre-release
5. **PR to main**: Deploy hotfix to production

See [docs/BRANCHING-STRATEGY.md](docs/BRANCHING-STRATEGY.md) for detailed workflow information.

## Semantic Release Process

This project uses automated semantic release:

1. Commits to `main` trigger the release workflow
2. Commit messages are analyzed to determine version bump
3. Version is automatically updated in all project files
4. CHANGELOG.md is generated/updated
5. GitHub release is created with release notes
6. NuGet packages are published (if configured)

### Version Bumping Rules

| Commit Type | Version Bump |
|-------------|--------------|
| `feat` | Minor (1.0.0 → 1.1.0) |
| `fix` | Patch (1.0.0 → 1.0.1) |
| `perf` | Patch (1.0.0 → 1.0.1) |
| `BREAKING CHANGE` | Major (1.0.0 → 2.0.0) |
| Others | No bump |

## Questions?

Feel free to open an issue if you have questions about contributing!