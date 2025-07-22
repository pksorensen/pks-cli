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

### Fixed
- ASCII command color validation - added robust validation for user input colors to prevent markup exceptions
- Deploy command spinner visibility - improved spinner display during deployment operations and coordination with progress bars  
- Agent command navigation flow - enhanced menu handling, error messaging, and user guidance
- Status command header truncation - made headers and layouts responsive to terminal width to prevent truncation in narrow terminals

### Documentation
- Added CONTRIBUTING.md with commit convention guidelines
- Created comprehensive semantic release documentation
- Updated command documentation with latest bug fixes and improvements

---

*Note: This changelog will be automatically updated by the semantic release process starting from the next release.*