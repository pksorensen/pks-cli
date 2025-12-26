## [1.2.0-rc.1](https://github.com/pksorensen/pks-cli/compare/v1.1.0...v1.2.0-rc.1) (2025-12-26)


### 🚀 Features

* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* add initializeCommand to set up environment variables in devcontainer templates ([4d20fc4](https://github.com/pksorensen/pks-cli/commit/4d20fc4f35b9e4dbba3594a6ad571eb7900aa995))
* Add PKS Fullstack DevContainer template with comprehensive setup ([5c4bf23](https://github.com/pksorensen/pks-cli/commit/5c4bf23abd95aa83f35c797192ecc7f3dc43bb0f))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* Enhance semantic release process with detailed release summary generation ([78103d3](https://github.com/pksorensen/pks-cli/commit/78103d3d18b0feae7ed8d521aae424fd170c0962))
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### 🐛 Bug Fixes

* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* clean up bloated CHANGELOG entries ([43f2854](https://github.com/pksorensen/pks-cli/commit/43f285405b9641082eb2ee33855648b5801bf664))
* CLI workflow should only use CLI tags (v*), not template tags ([cc8d9c6](https://github.com/pksorensen/pks-cli/commit/cc8d9c6b195c4e3a928b5ea86dc8765e35e672d8))
* convert semver to numeric format for AssemblyVersion ([8e23f1b](https://github.com/pksorensen/pks-cli/commit/8e23f1b4a50e46d7b7d09939ca64e36d490dcfec))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* embed version in assemblies during pack by rebuilding with version properties ([ae25c7a](https://github.com/pksorensen/pks-cli/commit/ae25c7a04d1baba64eeb36681225d5232c1b19f1))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* fetch tags in checkout to allow semantic-release to find previous releases ([c3f15e7](https://github.com/pksorensen/pks-cli/commit/c3f15e7a1a9228f7f042665497f4fa1eaf9009d5))
* hide chore commits from release notes to prevent bloated messages ([d0be154](https://github.com/pksorensen/pks-cli/commit/d0be154ccdc25b201f30fc9c72cd5e694bac5d11))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove old monolithic semantic-release config to avoid confusion ([5d203ce](https://github.com/pksorensen/pks-cli/commit/5d203cee1b19608581aca5cc7976d118824a33cf))
* remove successCmd that was aborting pre-release creation ([5b800a2](https://github.com/pksorensen/pks-cli/commit/5b800a2b8cdca32c9bf6ca574136c078e09a4a58))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))

# Changelog - PKS CLI

All notable changes to the PKS CLI tool will be documented in this file.

The CLI follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) and uses [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/).

For template-specific changes, see their respective CHANGELOG files:
- [DevContainer Template](../templates/devcontainer/CHANGELOG.md)
- [Claude .NET 9 Template](../templates/claude-dotnet-9/CHANGELOG.md)
- [Claude .NET 10 Full Template](../templates/claude-dotnet-10-full/CHANGELOG.md)
- [PKS Fullstack Template](../templates/pks-fullstack/CHANGELOG.md)

## [1.2.0-rc.9] - Latest

See releases for detailed changelog.
