## [1.2.0-rc.10](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.9...v1.2.0-rc.10) (2025-12-26)

### 🐛 Bug Fixes

* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))

## [1.2.0-rc.9](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.8...v1.2.0-rc.9) (2025-12-26)

### 🚀 Features

* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))

## [1.2.0-rc.8](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.7...v1.2.0-rc.8) (2025-12-25)

### 🚀 Features

* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))

## [1.2.0-rc.7](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.6...v1.2.0-rc.7) (2025-12-25)

### 🚀 Features

* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))

## [1.2.0-rc.6](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.5...v1.2.0-rc.6) (2025-12-25)

### 🚀 Features

* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))

## [1.2.0-rc.5](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.4...v1.2.0-rc.5) (2025-12-24)

### 🐛 Bug Fixes

* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))

## [1.2.0-rc.4](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.3...v1.2.0-rc.4) (2025-12-24)

### 🚀 Features

* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))

### 🐛 Bug Fixes

* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))

# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Semantic release automation with conventional commits
- Automated version management across all project files
- Changelog generation from commit messages
- GitHub release creation with artifacts
- NuGet package publishing support

### Changed
- Updated GitHub Actions workflows to support semantic versioning

### Documentation
- Added CONTRIBUTING.md with commit convention guidelines
- Created comprehensive semantic release documentation

---

*Note: This changelog will be automatically updated by the semantic release process starting from the next release.*
