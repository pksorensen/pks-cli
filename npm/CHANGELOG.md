# npm Distribution Changelog

All notable changes to the npm distribution of PKS CLI will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-rc.5](https://github.com/pksorensen/pks-cli/compare/npm-v1.0.0-rc.4...npm-v1.0.0-rc.5) (2026-01-08)


### Bug Fixes

* **ci:** support workflow_dispatch events in npm publish workflow ([63b5bd7](https://github.com/pksorensen/pks-cli/commit/63b5bd78782e8facdd8d69b6014b32087dcc53c0))

## [1.0.0-rc.4](https://github.com/pksorensen/pks-cli/compare/npm-v1.0.0-rc.3...npm-v1.0.0-rc.4) (2026-01-07)


### Features

* Add PKS CLI binary execution script for platform-specific binaries ([af1f4d2](https://github.com/pksorensen/pks-cli/commit/af1f4d2c5b4e87690db4190446e2857104ce958c))


### Bug Fixes

* Rename binaries in npm package creation workflow ([0c0d719](https://github.com/pksorensen/pks-cli/commit/0c0d719a3e10233a6fc2c9ae4e5dc98f99f19119))
* Update macOS versions in build workflow and add debug outputs in release workflow ([886f658](https://github.com/pksorensen/pks-cli/commit/886f658b4a9aeff0b4ed80c2bb4989c7398e4dfb))

## [1.0.0-rc.3](https://github.com/pksorensen/pks-cli/compare/npm-v1.0.0-rc.2...npm-v1.0.0-rc.3) (2026-01-07)


### Bug Fixes

* Update binary names in build workflow to pks-cli ([8e8dd80](https://github.com/pksorensen/pks-cli/commit/8e8dd805f5e9a6957380fb80b35b072229c0903a))

## [1.0.0-rc.2](https://github.com/pksorensen/pks-cli/compare/npm-v1.0.0-rc.1...npm-v1.0.0-rc.2) (2026-01-07)


### Features

* Enhance semantic-release step to output release status and version ([0d0a841](https://github.com/pksorensen/pks-cli/commit/0d0a841e7b22c4228607dfaab21ad25c9ad40fc5))

## 1.0.0-rc.1 (2026-01-07)


### ⚠ BREAKING CHANGES

* Legacy MCP command interface removed in favor of SDK-based hosting

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Devcontainer templates now distributed as NuGet packages

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* The old hooks system with smart dispatcher has been removed.
Use 'pks hooks init' to configure Claude Code integration instead.

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Restructures command organization and service registration for 1.0.0 architecture

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Establishes new testing infrastructure for enterprise-grade quality assurance

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Initial release of PKS CLI framework

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>

* refactor(mcp)\!: remove obsolete command and service files ([c7de171](https://github.com/pksorensen/pks-cli/commit/c7de1712c672ea829b69dc581437af1c725bdf7a))


### Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* Add cleanup step for failed releases in npm workflow ([063abfc](https://github.com/pksorensen/pks-cli/commit/063abfc31457bb9d616992f2cd28f7d45f17a7c0))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* add core devcontainer interfaces and data models ([a4ee872](https://github.com/pksorensen/pks-cli/commit/a4ee8721a6f0bda25e193ce304817c387128cee1))
* add CreatedFiles alias to InitializationResult ([37ce512](https://github.com/pksorensen/pks-cli/commit/37ce512863e7c3c4be2c3f010dbccf09c0c4c179))
* add devcontainer development environment ([6ca9d3f](https://github.com/pksorensen/pks-cli/commit/6ca9d3fd4d244ef231828081d1d9b543e4560ad4))
* add devcontainer feature system ([754c83a](https://github.com/pksorensen/pks-cli/commit/754c83ac08b1e41ca593ab36d5243a9a4f2e07fc))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* add GitHub App authorization and expert guidance agents ([0a35fed](https://github.com/pksorensen/pks-cli/commit/0a35fed104af8c79e23995d431930ecc1ac05da6))
* add hooks configuration and documentation for Claude Code ([7ec7222](https://github.com/pksorensen/pks-cli/commit/7ec72221ae2d40658c266f86bb0f54c693065a31))
* add initializeCommand to set up environment variables in devcontainer templates ([4d20fc4](https://github.com/pksorensen/pks-cli/commit/4d20fc4f35b9e4dbba3594a6ad571eb7900aa995))
* add Model Context Protocol (MCP) integration ([b7e9adf](https://github.com/pksorensen/pks-cli/commit/b7e9adf0ed23447ce9948d2c4d79ef9eb6087258))
* add NuGet-based devcontainer template system ([39d4465](https://github.com/pksorensen/pks-cli/commit/39d44654fd9f7e36771315815a8e6e1c1c41c41c))
* Add PKS Fullstack DevContainer template with comprehensive setup ([5c4bf23](https://github.com/pksorensen/pks-cli/commit/5c4bf23abd95aa83f35c797192ecc7f3dc43bb0f))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add semantic-release configurations for new templates and update paths ([6ce5889](https://github.com/pksorensen/pks-cli/commit/6ce588946eac4811858a92c81738fcb4d388536b))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* Enhance npm release workflow with optional NPM_TOKEN input and Trusted Publishers support ([bf9915c](https://github.com/pksorensen/pks-cli/commit/bf9915ce0e2552c0a0aabcb2cbc7d10951d81803))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* Enhance semantic release process with detailed release summary generation ([78103d3](https://github.com/pksorensen/pks-cli/commit/78103d3d18b0feae7ed8d521aae424fd170c0962))
* enhance test infrastructure and project organization ([9174fdf](https://github.com/pksorensen/pks-cli/commit/9174fdff86e9ca4b372a9423a2c651cd043441c2))
* implement AI-powered PRD tools with comprehensive management ([43e97b7](https://github.com/pksorensen/pks-cli/commit/43e97b7c973e5b2bf6a8bea597b542e5d93098fa)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4)
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* implement Claude Code hooks integration with smart dispatcher ([6003082](https://github.com/pksorensen/pks-cli/commit/6003082168bbffaf4ac8cded4d6db49e3a6522d8)), closes [#1](https://github.com/pksorensen/pks-cli/issues/1)
* implement comprehensive agent framework with communication system ([55cb45e](https://github.com/pksorensen/pks-cli/commit/55cb45e852f0b1179600a43b8e1f88acdc2c16b0)), closes [#3](https://github.com/pksorensen/pks-cli/issues/3)
* implement comprehensive business logic in core services ([fadd428](https://github.com/pksorensen/pks-cli/commit/fadd428331e941c720ce34014a5e88aea48b9450))
* implement comprehensive command system with Spectre.Console ([75fe8f7](https://github.com/pksorensen/pks-cli/commit/75fe8f716ad2b13346b9325307285ff998586bee))
* implement comprehensive requirements gathering workflow and status tracking ([dc8eae9](https://github.com/pksorensen/pks-cli/commit/dc8eae96cc7a1184e56e97a83a8b22541ce88451))
* implement comprehensive TDD foundation and CI/CD pipeline ([9963e6e](https://github.com/pksorensen/pks-cli/commit/9963e6ee6c3a58cd6ed705888a8f87a115632903))
* implement comprehensive template system for project initialization ([8aaf44e](https://github.com/pksorensen/pks-cli/commit/8aaf44e6c2073a15d8c349449e40ff533b79d1c0))
* implement comprehensive testing infrastructure and DevContainer ([7175073](https://github.com/pksorensen/pks-cli/commit/7175073752d483f2061b10931b80da46c97386aa))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* implement core devcontainer services ([44b52ff](https://github.com/pksorensen/pks-cli/commit/44b52ffbff44a67205a405ba3be68fdbec1f7d07))
* implement core PKS CLI infrastructure ([48c0f36](https://github.com/pksorensen/pks-cli/commit/48c0f368c8c0585527dea321e0bb1efde4810f90))
* implement core test infrastructure improvements ([16f5d06](https://github.com/pksorensen/pks-cli/commit/16f5d06c5f35be1230d3f63246d60e17a8416152))
* implement devcontainer CLI commands ([176c57d](https://github.com/pksorensen/pks-cli/commit/176c57d9f97b6c4075ebde70d73d849f03f4085f))
* implement enhanced MCP server with multi-transport support ([915b17d](https://github.com/pksorensen/pks-cli/commit/915b17d18201396edf982e3ff8d8c154bf8d84ed)), closes [#2](https://github.com/pksorensen/pks-cli/issues/2)
* implement first-time warning system for AI-generated code ([dde7f02](https://github.com/pksorensen/pks-cli/commit/dde7f02eb8dbdefd6d319eb9f651d8e39c2f21c2)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4)
* Implement first-time warning system for AI-generated code ([16f9f92](https://github.com/pksorensen/pks-cli/commit/16f9f9283f0645938f0c7d877cd540adbd907594)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4)
* implement GitHub integration and project identity system ([b76d49a](https://github.com/pksorensen/pks-cli/commit/b76d49a461a5282f156f9e1e12a36fdd0444dafc)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* implement modular CLAUDE.md system and comprehensive documentation ([7e84a50](https://github.com/pksorensen/pks-cli/commit/7e84a505c6b76c97130b4a40087a7af6e9af2bf6)), closes [#5](https://github.com/pksorensen/pks-cli/issues/5)
* implement modular PRD subcommands ([65c06e3](https://github.com/pksorensen/pks-cli/commit/65c06e370b418311d524658c0d8a53041671bf09))
* implement report command with GitHub App authentication ([4c67cd3](https://github.com/pksorensen/pks-cli/commit/4c67cd36126f33464754eb2b381d52eb84a86cb4)), closes [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5)
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* Change npm install to use --no-save for semantic-release dependencies ([1f585a8](https://github.com/pksorensen/pks-cli/commit/1f585a865a4a9877cb9974f76aad9e4a38de8d3b))
* clean up bloated CHANGELOG entries ([43f2854](https://github.com/pksorensen/pks-cli/commit/43f285405b9641082eb2ee33855648b5801bf664))
* CLI workflow should only use CLI tags (v*), not template tags ([cc8d9c6](https://github.com/pksorensen/pks-cli/commit/cc8d9c6b195c4e3a928b5ea86dc8765e35e672d8))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* convert semver to numeric format for AssemblyVersion ([8e23f1b](https://github.com/pksorensen/pks-cli/commit/8e23f1b4a50e46d7b7d09939ca64e36d490dcfec))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* embed version in assemblies during pack by rebuilding with version properties ([ae25c7a](https://github.com/pksorensen/pks-cli/commit/ae25c7a04d1baba64eeb36681225d5232c1b19f1))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* fetch tags in checkout to allow semantic-release to find previous releases ([c3f15e7](https://github.com/pksorensen/pks-cli/commit/c3f15e7a1a9228f7f042665497f4fa1eaf9009d5))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** grant id-token permission to npm release workflow ([8f41a80](https://github.com/pksorensen/pks-cli/commit/8f41a8054745dc31c571a7c18a2c92c32615bf83))
* hide chore commits from release notes to prevent bloated messages ([d0be154](https://github.com/pksorensen/pks-cli/commit/d0be154ccdc25b201f30fc9c72cd5e694bac5d11))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove old monolithic semantic-release config to avoid confusion ([5d203ce](https://github.com/pksorensen/pks-cli/commit/5d203cee1b19608581aca5cc7976d118824a33cf))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
* remove successCmd that was aborting pre-release creation ([5b800a2](https://github.com/pksorensen/pks-cli/commit/5b800a2b8cdca32c9bf6ca574136c078e09a4a58))
* Remove unnecessary GITHUB_TOKEN requirement from release workflow ([3a7fd55](https://github.com/pksorensen/pks-cli/commit/3a7fd55113b6bce62e0e3e53fac1defcba1e63d6))
* replace bash-specific syntax with POSIX-compliant shell syntax in semantic-release configs ([ba1d1d9](https://github.com/pksorensen/pks-cli/commit/ba1d1d99b70fdf50dcae8737d0c53e420a241dc9))
* resolve build errors in 1.0.0 implementation ([f84b263](https://github.com/pksorensen/pks-cli/commit/f84b2639d460ed28fe749c0d1c60af4133cf17e0))
* resolve build/test configuration mismatch in PR workflow ([e0d291b](https://github.com/pksorensen/pks-cli/commit/e0d291b4c70c268af358fa9ae489c9ddce23c5fb))
* resolve code formatting issues in test files ([0019dfa](https://github.com/pksorensen/pks-cli/commit/0019dfa68ba23eec3a31298535161b8fa6b83287))
* resolve code formatting issues in test files ([f5b01cf](https://github.com/pksorensen/pks-cli/commit/f5b01cfbfbe1c3dc1a5411d7701568a5bf0be2ba))
* resolve compilation errors in hook command implementations ([3df1201](https://github.com/pksorensen/pks-cli/commit/3df120188dfc93fc4066f4814fbeb5cee48b46f5))
* resolve compilation errors in test files ([cf243ae](https://github.com/pksorensen/pks-cli/commit/cf243aebe369cc369dd9acbb05a45b3184e8ac2a))
* resolve container compatibility issues in InitCommand and test infrastructure ([df7d5fe](https://github.com/pksorensen/pks-cli/commit/df7d5fe2200941bc4badcd9d14e3d6a30785f2c6))
* resolve PRD integration test console output issues ([757e6bc](https://github.com/pksorensen/pks-cli/commit/757e6bcc52f359cb3608af5a8e5ba9aef89f4b79))
* resolve remaining compilation errors in test files ([7e305fd](https://github.com/pksorensen/pks-cli/commit/7e305fd1d80ac529ccad9c0102f859ba49ef2038))
* resolve test compilation errors for clean solution build ([69405fe](https://github.com/pksorensen/pks-cli/commit/69405fed19701a4f7696df8037c982887a4449f6))
* resolve xUnit analyzer errors in test files ([3e839e7](https://github.com/pksorensen/pks-cli/commit/3e839e7a6d4e6b147dc193cacb3f072f36728e2d))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))
* update Dockerfile and devcontainer.json to use latest base image tag ([64bdd86](https://github.com/pksorensen/pks-cli/commit/64bdd8612709f5d76f96b6b504a323b83adfbe2f))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))
* update template matrix output to use compact format ([61e9b32](https://github.com/pksorensen/pks-cli/commit/61e9b32848b96f88ad4dadea71c8dd2a78294432))


### Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### Code Refactoring

* consolidate semantic-release configurations for templates ([846487f](https://github.com/pksorensen/pks-cli/commit/846487ff992565a8d8c417ba13fcd8635c017f93))
* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))

## [1.0.0-rc.2](https://github.com/pksorensen/pks-cli/compare/npm-v1.0.0-rc.1...npm-v1.0.0-rc.2) (2026-01-07)


### Features

* Add cleanup step for failed releases in npm workflow ([063abfc](https://github.com/pksorensen/pks-cli/commit/063abfc31457bb9d616992f2cd28f7d45f17a7c0))

## 1.0.0-rc.1 (2026-01-07)


### ⚠ BREAKING CHANGES

* Legacy MCP command interface removed in favor of SDK-based hosting

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Devcontainer templates now distributed as NuGet packages

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* The old hooks system with smart dispatcher has been removed.
Use 'pks hooks init' to configure Claude Code integration instead.

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Restructures command organization and service registration for 1.0.0 architecture

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Establishes new testing infrastructure for enterprise-grade quality assurance

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>
* Initial release of PKS CLI framework

🤖 Generated with [Claude Code](https://claude.ai/code)

Co-Authored-By: Claude <noreply@anthropic.com>

* refactor(mcp)\!: remove obsolete command and service files ([c7de171](https://github.com/pksorensen/pks-cli/commit/c7de1712c672ea829b69dc581437af1c725bdf7a))


### Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* add core devcontainer interfaces and data models ([a4ee872](https://github.com/pksorensen/pks-cli/commit/a4ee8721a6f0bda25e193ce304817c387128cee1))
* add CreatedFiles alias to InitializationResult ([37ce512](https://github.com/pksorensen/pks-cli/commit/37ce512863e7c3c4be2c3f010dbccf09c0c4c179))
* add devcontainer development environment ([6ca9d3f](https://github.com/pksorensen/pks-cli/commit/6ca9d3fd4d244ef231828081d1d9b543e4560ad4))
* add devcontainer feature system ([754c83a](https://github.com/pksorensen/pks-cli/commit/754c83ac08b1e41ca593ab36d5243a9a4f2e07fc))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* add GitHub App authorization and expert guidance agents ([0a35fed](https://github.com/pksorensen/pks-cli/commit/0a35fed104af8c79e23995d431930ecc1ac05da6))
* add hooks configuration and documentation for Claude Code ([7ec7222](https://github.com/pksorensen/pks-cli/commit/7ec72221ae2d40658c266f86bb0f54c693065a31))
* add initializeCommand to set up environment variables in devcontainer templates ([4d20fc4](https://github.com/pksorensen/pks-cli/commit/4d20fc4f35b9e4dbba3594a6ad571eb7900aa995))
* add Model Context Protocol (MCP) integration ([b7e9adf](https://github.com/pksorensen/pks-cli/commit/b7e9adf0ed23447ce9948d2c4d79ef9eb6087258))
* add NuGet-based devcontainer template system ([39d4465](https://github.com/pksorensen/pks-cli/commit/39d44654fd9f7e36771315815a8e6e1c1c41c41c))
* Add PKS Fullstack DevContainer template with comprehensive setup ([5c4bf23](https://github.com/pksorensen/pks-cli/commit/5c4bf23abd95aa83f35c797192ecc7f3dc43bb0f))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add semantic-release configurations for new templates and update paths ([6ce5889](https://github.com/pksorensen/pks-cli/commit/6ce588946eac4811858a92c81738fcb4d388536b))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* Enhance npm release workflow with optional NPM_TOKEN input and Trusted Publishers support ([bf9915c](https://github.com/pksorensen/pks-cli/commit/bf9915ce0e2552c0a0aabcb2cbc7d10951d81803))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* Enhance semantic release process with detailed release summary generation ([78103d3](https://github.com/pksorensen/pks-cli/commit/78103d3d18b0feae7ed8d521aae424fd170c0962))
* enhance test infrastructure and project organization ([9174fdf](https://github.com/pksorensen/pks-cli/commit/9174fdff86e9ca4b372a9423a2c651cd043441c2))
* implement AI-powered PRD tools with comprehensive management ([43e97b7](https://github.com/pksorensen/pks-cli/commit/43e97b7c973e5b2bf6a8bea597b542e5d93098fa)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4)
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* implement Claude Code hooks integration with smart dispatcher ([6003082](https://github.com/pksorensen/pks-cli/commit/6003082168bbffaf4ac8cded4d6db49e3a6522d8)), closes [#1](https://github.com/pksorensen/pks-cli/issues/1)
* implement comprehensive agent framework with communication system ([55cb45e](https://github.com/pksorensen/pks-cli/commit/55cb45e852f0b1179600a43b8e1f88acdc2c16b0)), closes [#3](https://github.com/pksorensen/pks-cli/issues/3)
* implement comprehensive business logic in core services ([fadd428](https://github.com/pksorensen/pks-cli/commit/fadd428331e941c720ce34014a5e88aea48b9450))
* implement comprehensive command system with Spectre.Console ([75fe8f7](https://github.com/pksorensen/pks-cli/commit/75fe8f716ad2b13346b9325307285ff998586bee))
* implement comprehensive requirements gathering workflow and status tracking ([dc8eae9](https://github.com/pksorensen/pks-cli/commit/dc8eae96cc7a1184e56e97a83a8b22541ce88451))
* implement comprehensive TDD foundation and CI/CD pipeline ([9963e6e](https://github.com/pksorensen/pks-cli/commit/9963e6ee6c3a58cd6ed705888a8f87a115632903))
* implement comprehensive template system for project initialization ([8aaf44e](https://github.com/pksorensen/pks-cli/commit/8aaf44e6c2073a15d8c349449e40ff533b79d1c0))
* implement comprehensive testing infrastructure and DevContainer ([7175073](https://github.com/pksorensen/pks-cli/commit/7175073752d483f2061b10931b80da46c97386aa))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* implement core devcontainer services ([44b52ff](https://github.com/pksorensen/pks-cli/commit/44b52ffbff44a67205a405ba3be68fdbec1f7d07))
* implement core PKS CLI infrastructure ([48c0f36](https://github.com/pksorensen/pks-cli/commit/48c0f368c8c0585527dea321e0bb1efde4810f90))
* implement core test infrastructure improvements ([16f5d06](https://github.com/pksorensen/pks-cli/commit/16f5d06c5f35be1230d3f63246d60e17a8416152))
* implement devcontainer CLI commands ([176c57d](https://github.com/pksorensen/pks-cli/commit/176c57d9f97b6c4075ebde70d73d849f03f4085f))
* implement enhanced MCP server with multi-transport support ([915b17d](https://github.com/pksorensen/pks-cli/commit/915b17d18201396edf982e3ff8d8c154bf8d84ed)), closes [#2](https://github.com/pksorensen/pks-cli/issues/2)
* implement first-time warning system for AI-generated code ([dde7f02](https://github.com/pksorensen/pks-cli/commit/dde7f02eb8dbdefd6d319eb9f651d8e39c2f21c2)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4)
* Implement first-time warning system for AI-generated code ([16f9f92](https://github.com/pksorensen/pks-cli/commit/16f9f9283f0645938f0c7d877cd540adbd907594)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4)
* implement GitHub integration and project identity system ([b76d49a](https://github.com/pksorensen/pks-cli/commit/b76d49a461a5282f156f9e1e12a36fdd0444dafc)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* implement modular CLAUDE.md system and comprehensive documentation ([7e84a50](https://github.com/pksorensen/pks-cli/commit/7e84a505c6b76c97130b4a40087a7af6e9af2bf6)), closes [#5](https://github.com/pksorensen/pks-cli/issues/5)
* implement modular PRD subcommands ([65c06e3](https://github.com/pksorensen/pks-cli/commit/65c06e370b418311d524658c0d8a53041671bf09))
* implement report command with GitHub App authentication ([4c67cd3](https://github.com/pksorensen/pks-cli/commit/4c67cd36126f33464754eb2b381d52eb84a86cb4)), closes [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5)
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* Change npm install to use --no-save for semantic-release dependencies ([1f585a8](https://github.com/pksorensen/pks-cli/commit/1f585a865a4a9877cb9974f76aad9e4a38de8d3b))
* clean up bloated CHANGELOG entries ([43f2854](https://github.com/pksorensen/pks-cli/commit/43f285405b9641082eb2ee33855648b5801bf664))
* CLI workflow should only use CLI tags (v*), not template tags ([cc8d9c6](https://github.com/pksorensen/pks-cli/commit/cc8d9c6b195c4e3a928b5ea86dc8765e35e672d8))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* convert semver to numeric format for AssemblyVersion ([8e23f1b](https://github.com/pksorensen/pks-cli/commit/8e23f1b4a50e46d7b7d09939ca64e36d490dcfec))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* embed version in assemblies during pack by rebuilding with version properties ([ae25c7a](https://github.com/pksorensen/pks-cli/commit/ae25c7a04d1baba64eeb36681225d5232c1b19f1))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* fetch tags in checkout to allow semantic-release to find previous releases ([c3f15e7](https://github.com/pksorensen/pks-cli/commit/c3f15e7a1a9228f7f042665497f4fa1eaf9009d5))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** grant id-token permission to npm release workflow ([8f41a80](https://github.com/pksorensen/pks-cli/commit/8f41a8054745dc31c571a7c18a2c92c32615bf83))
* hide chore commits from release notes to prevent bloated messages ([d0be154](https://github.com/pksorensen/pks-cli/commit/d0be154ccdc25b201f30fc9c72cd5e694bac5d11))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove old monolithic semantic-release config to avoid confusion ([5d203ce](https://github.com/pksorensen/pks-cli/commit/5d203cee1b19608581aca5cc7976d118824a33cf))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
* remove successCmd that was aborting pre-release creation ([5b800a2](https://github.com/pksorensen/pks-cli/commit/5b800a2b8cdca32c9bf6ca574136c078e09a4a58))
* Remove unnecessary GITHUB_TOKEN requirement from release workflow ([3a7fd55](https://github.com/pksorensen/pks-cli/commit/3a7fd55113b6bce62e0e3e53fac1defcba1e63d6))
* replace bash-specific syntax with POSIX-compliant shell syntax in semantic-release configs ([ba1d1d9](https://github.com/pksorensen/pks-cli/commit/ba1d1d99b70fdf50dcae8737d0c53e420a241dc9))
* resolve build errors in 1.0.0 implementation ([f84b263](https://github.com/pksorensen/pks-cli/commit/f84b2639d460ed28fe749c0d1c60af4133cf17e0))
* resolve build/test configuration mismatch in PR workflow ([e0d291b](https://github.com/pksorensen/pks-cli/commit/e0d291b4c70c268af358fa9ae489c9ddce23c5fb))
* resolve code formatting issues in test files ([0019dfa](https://github.com/pksorensen/pks-cli/commit/0019dfa68ba23eec3a31298535161b8fa6b83287))
* resolve code formatting issues in test files ([f5b01cf](https://github.com/pksorensen/pks-cli/commit/f5b01cfbfbe1c3dc1a5411d7701568a5bf0be2ba))
* resolve compilation errors in hook command implementations ([3df1201](https://github.com/pksorensen/pks-cli/commit/3df120188dfc93fc4066f4814fbeb5cee48b46f5))
* resolve compilation errors in test files ([cf243ae](https://github.com/pksorensen/pks-cli/commit/cf243aebe369cc369dd9acbb05a45b3184e8ac2a))
* resolve container compatibility issues in InitCommand and test infrastructure ([df7d5fe](https://github.com/pksorensen/pks-cli/commit/df7d5fe2200941bc4badcd9d14e3d6a30785f2c6))
* resolve PRD integration test console output issues ([757e6bc](https://github.com/pksorensen/pks-cli/commit/757e6bcc52f359cb3608af5a8e5ba9aef89f4b79))
* resolve remaining compilation errors in test files ([7e305fd](https://github.com/pksorensen/pks-cli/commit/7e305fd1d80ac529ccad9c0102f859ba49ef2038))
* resolve test compilation errors for clean solution build ([69405fe](https://github.com/pksorensen/pks-cli/commit/69405fed19701a4f7696df8037c982887a4449f6))
* resolve xUnit analyzer errors in test files ([3e839e7](https://github.com/pksorensen/pks-cli/commit/3e839e7a6d4e6b147dc193cacb3f072f36728e2d))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))
* update Dockerfile and devcontainer.json to use latest base image tag ([64bdd86](https://github.com/pksorensen/pks-cli/commit/64bdd8612709f5d76f96b6b504a323b83adfbe2f))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))
* update template matrix output to use compact format ([61e9b32](https://github.com/pksorensen/pks-cli/commit/61e9b32848b96f88ad4dadea71c8dd2a78294432))


### Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### Code Refactoring

* consolidate semantic-release configurations for templates ([846487f](https://github.com/pksorensen/pks-cli/commit/846487ff992565a8d8c417ba13fcd8635c017f93))
* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))

## [1.0.0] - TBD

### Added

- Initial npm distribution with self-contained binaries
- Support for 6 platforms: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64, win-arm64
- Platform detection wrapper for automatic binary selection
- optionalDependencies pattern following esbuild
- Dual distribution alongside existing NuGet packages
