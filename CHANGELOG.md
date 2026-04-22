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

## [6.8.0](https://github.com/pksorensen/pks-cli/compare/v6.7.1...v6.8.0) (2026-04-22)


### Features

* add pks foundry proxy and pks tts commands ([bebdb84](https://github.com/pksorensen/pks-cli/commit/bebdb847b43820cf3537145fbf55bb83f38f3149))
* added confluence commands ([3dae75f](https://github.com/pksorensen/pks-cli/commit/3dae75f779d0a25c8a525452deb2d909de55e820))
* added fileshare, storage, firecracher, otel support ([50e2321](https://github.com/pksorensen/pks-cli/commit/50e23216d7a77fc4feb095f9186a0688ea447521))
* **agentics:** add support for broadcastId and sessionId in job processing ([ab0a579](https://github.com/pksorensen/pks-cli/commit/ab0a579a42d420c02a9d405666fef151810577d9))
* **appinsights:** redesign init to use Azure subscription discovery via PKCE auth ([bdcdd5c](https://github.com/pksorensen/pks-cli/commit/bdcdd5c232950afba8b129be869351e89d69f0a7))
* Enhance GitHub integration with authentication and task management ([ced345c](https://github.com/pksorensen/pks-cli/commit/ced345ccf8cd7b3b2e31409b04dfc9e15ca90519))
* **hooks:** add PostCompact hook for enhanced integration ([d92d35a](https://github.com/pksorensen/pks-cli/commit/d92d35a009150ebd4f8dd1de224f7e5c8bebfb5a))
* Implement Microsoft Graph Email Export Service ([ef04cd3](https://github.com/pksorensen/pks-cli/commit/ef04cd3809211136e6e8b27d912ccce05796c6e1))
* **jira:** enhance issue fetching with parallel processing and throttling ([c5a4bf7](https://github.com/pksorensen/pks-cli/commit/c5a4bf7c5d875f4650ad842ac26169d1da47f7c6))
* **jira:** enhance pagination handling for Jira Cloud integration using nextPageToken ([54a8131](https://github.com/pksorensen/pks-cli/commit/54a8131613a2394b6325a00abd175a063c162f05))
* parallel download ([144629b](https://github.com/pksorensen/pks-cli/commit/144629b2a0159ef42c6393cdbefadeb50cd4b109))
* **runner:** implement lightweight OTLP HTTP broadcast proxy for job telemetry ([d13a702](https://github.com/pksorensen/pks-cli/commit/d13a70277d99db65e9cc9e7efba4e79bb515e137))
* **runner:** update runner start command, devcontainer spawner, and project file ([04f075c](https://github.com/pksorensen/pks-cli/commit/04f075c3977bdf3123ea1459667480f4e0c96d03))
* **storage:** added filtering support ([9e59451](https://github.com/pksorensen/pks-cli/commit/9e594515c43f1a691bb3c1dbbd100f1ceb2891e3))


### Bug Fixes

* **appinsights:** init triggers PKCE auth itself instead of requiring pks foundry init ([86dfe0a](https://github.com/pksorensen/pks-cli/commit/86dfe0aa7939f2d5b2e062cf0bf5c30bebf583de))
* **appinsights:** show resource group in selection prompt and remove Status spinner ([c8ce25a](https://github.com/pksorensen/pks-cli/commit/c8ce25a5b848b5369f6080d73ad5c5ddbd0a258b))
* auth on appinsights ([144629b](https://github.com/pksorensen/pks-cli/commit/144629b2a0159ef42c6393cdbefadeb50cd4b109))
* improve Jira issue pagination handling and add related tests ([a252818](https://github.com/pksorensen/pks-cli/commit/a252818b610ff2ed6381cb347bdf12b842ad0456))
* **otel:** parse arbitrary Nh/Nd/Nm durations and add --verbose debug flag ([301ec39](https://github.com/pksorensen/pks-cli/commit/301ec39ec07a0985c804c1aa25c6a2382a9c633c))
* **proxy:** use raw header indexer for Authorization check to avoid typed accessor quirks ([2c2b1de](https://github.com/pksorensen/pks-cli/commit/2c2b1de72a3eea6c40b9ca6a7843868bb5154d69))
* **tts:** use cognitiveservices.azure.com endpoint and default to tts-hd deployment ([c4d1e6f](https://github.com/pksorensen/pks-cli/commit/c4d1e6f9e62b821f6f7252ad0ca00312fd539d80))

## [6.7.1](https://github.com/pksorensen/pks-cli/compare/v6.7.0...v6.7.1) (2026-04-04)


### Bug Fixes

* add include-paths to pks-cli configuration in release-please ([5de0584](https://github.com/pksorensen/pks-cli/commit/5de0584585ea2441c3c4ebaa63795ef284b6b4d4))
* rename package scope from @pks-cli/pks to @pks-cli/cli across the project ([fd3f902](https://github.com/pksorensen/pks-cli/commit/fd3f90251dff676d72a9657eaed7e3961223fc8b))
* update preview versioning to count commits since last release tag ([ec985f9](https://github.com/pksorensen/pks-cli/commit/ec985f95b68f3ee9e6d8ad008ce193ab4c8ba30d))

## [6.7.0](https://github.com/pksorensen/pks-cli/compare/v6.6.0...v6.7.0) (2026-04-03)


### Features

* **agentics:** add command to submit tasks to Assembly Lines from CI/CD pipelines ([94d43e8](https://github.com/pksorensen/pks-cli/commit/94d43e8c5aeb1e8a50b0f339b5142d60fdc7ab92))
* **auth:** enhance token refresh logic for GitHub authentication ([f4f22b8](https://github.com/pksorensen/pks-cli/commit/f4f22b893e145c0fcde67cf4292966ff9ac5cfbf))
* **google:** add Google AI service and commands for API key management and image generation ([b116b07](https://github.com/pksorensen/pks-cli/commit/b116b07ab07cae0f6cfe6b0939f758635d88de7a))

## [6.6.0](https://github.com/pksorensen/pks-cli/compare/v6.5.2...v6.6.0) (2026-04-02)


### Features

* Add SSH target management commands and services ([1ef4f4c](https://github.com/pksorensen/pks-cli/commit/1ef4f4c40a48e718746bbada0120dd05f7f83d11))


### Bug Fixes

* remove 'force' parameter from Coolify deployment webhook URL ([5437292](https://github.com/pksorensen/pks-cli/commit/543729253c8ef70cf04115e62a46e15c9a09c0c6))
* **runner:** mount credential socket directory correctly in devcontainers ([46028c6](https://github.com/pksorensen/pks-cli/commit/46028c614f9a6daec831331d274b63bc8161d0bf))

## [6.5.2](https://github.com/pksorensen/pks-cli/compare/v6.5.1...v6.5.2) (2026-03-29)


### Bug Fixes

* add AGENTICS_JOB_MODE environment variable to process start command ([d399e33](https://github.com/pksorensen/pks-cli/commit/d399e337af8e2b9b0fe5cdfe3205068dd4784345))

## [6.5.1](https://github.com/pksorensen/pks-cli/compare/v6.5.0...v6.5.1) (2026-03-29)


### Bug Fixes

* add missing slash in activity URL for idle detection ([4652bbd](https://github.com/pksorensen/pks-cli/commit/4652bbdab3c3b64034deda0bb6315e2dd4e54de8))
* wrap environment variable values in quotes for consistency ([dd20e53](https://github.com/pksorensen/pks-cli/commit/dd20e5355813f9073255cd9e096d2e55eb2b2099))

## [6.5.0](https://github.com/pksorensen/pks-cli/compare/v6.4.0...v6.5.0) (2026-03-29)


### Features

* add git user configuration options for devcontainer ([dc3210f](https://github.com/pksorensen/pks-cli/commit/dc3210f2ecda3838c33d9fb30d8b62238da40656))

## [6.4.0](https://github.com/pksorensen/pks-cli/compare/v6.3.0...v6.4.0) (2026-03-29)


### Features

* enhance Agentics runner commands with new environment variables for server configuration and backward compatibility ([6a09987](https://github.com/pksorensen/pks-cli/commit/6a0998706e1ecdce64491ed6af762f9781b39e21))

## [6.3.0](https://github.com/pksorensen/pks-cli/compare/v6.2.2...v6.3.0) (2026-03-28)


### Features

* add HooksMenuCommand for interactive configuration of hook behavior; implement lint command management in StopCommand ([1da11ea](https://github.com/pksorensen/pks-cli/commit/1da11ea600453aa999570903767a27e3c0c3f08e))
* embed vibecast binary and claude-plugin for local testing; add build-local.sh script ([6a62629](https://github.com/pksorensen/pks-cli/commit/6a62629d13e621e33011e3c05cfad7a659904644))
* enhance devcontainer spawning with real-time output streaming and new ExecInContainerAsync method ([740267d](https://github.com/pksorensen/pks-cli/commit/740267dbba1faac9bca6c61421cc5ba21cdf1e89))
* implement GitHub authentication flow and devcontainer spawning via Docker volumes ([4512d17](https://github.com/pksorensen/pks-cli/commit/4512d178f2e4af068e8e8017c2b278d0aee4e899))
* refactor hook command output handling; update StopCommand to read stdin for stop_hook_active; enhance HookDecision model with Reason property ([6fd4ce2](https://github.com/pksorensen/pks-cli/commit/6fd4ce2ae8901baf090fe3ab1c340aecacd8ddb5))

## [6.2.2](https://github.com/pksorensen/pks-cli/compare/v6.2.1...v6.2.2) (2026-03-27)


### Bug Fixes

* update command option format for project parameter ([2a010b5](https://github.com/pksorensen/pks-cli/commit/2a010b53afe6748e0112dc47175c24385d9079d0))

## [6.2.1](https://github.com/pksorensen/pks-cli/compare/v6.2.0...v6.2.1) (2026-03-27)


### Bug Fixes

* resolve build errors in CI pipeline ([54f5ee9](https://github.com/pksorensen/pks-cli/commit/54f5ee98902a885d50dbcf993f9de006c6d94d57))

## [6.2.0](https://github.com/pksorensen/pks-cli/compare/v6.1.0...v6.2.0) (2026-03-27)


### Features

* add project and server options for auto-registration in Agentics runner ([f6eb40d](https://github.com/pksorensen/pks-cli/commit/f6eb40dcf3e9ee6c567f714bd09f607d30c051cd))
* enhance Agentics runner commands with GitHub integration and project info fetching ([3b94299](https://github.com/pksorensen/pks-cli/commit/3b942997f81b4685bc5cbe4e27fc46a72f0874b1))
* enhance job execution by introducing initial prompt file and staging git credentials ([4276694](https://github.com/pksorensen/pks-cli/commit/4276694378ef6dbbab35062e890acebc4db48d62))
* implement registry management commands and services ([756ce50](https://github.com/pksorensen/pks-cli/commit/756ce506b856e8fc40adaadc4f7b4ae84cb3f231))


### Bug Fixes

* update RegistryRemoveCommand to use RegistrySettings for command execution ([db52fbf](https://github.com/pksorensen/pks-cli/commit/db52fbf04a964c69080dc4f7c94e33bb4ade3efc))

## [6.1.0](https://github.com/pksorensen/pks-cli/compare/v6.0.0...v6.1.0) (2026-03-17)


### Features

* Add acceptance criteria field to Jira issues and update related parsing logic ([a435450](https://github.com/pksorensen/pks-cli/commit/a43545018852a30abd4ca3bc26df9f66209324b3))
* Add Azure AI Foundry authentication and related commands ([b217fc5](https://github.com/pksorensen/pks-cli/commit/b217fc5599745a9822bd672fa5067dbcf607f216))
* Add changelog support to Jira issues with parsing and display functionality ([6970731](https://github.com/pksorensen/pks-cli/commit/69707313ec17f041b35f2f71d1845d0b0ad920af))
* Add Coolify integration for deployment management and instance registration ([d0e455f](https://github.com/pksorensen/pks-cli/commit/d0e455f05c1002a8d220210669e312e2ff72d04e))
* Add debug output option for Jira commands to log HTTP request/response details ([d0e9dfd](https://github.com/pksorensen/pks-cli/commit/d0e9dfd235ffc981705489d701e777aa13c9dd0a))
* add GIT_ASKPASS installation command and update Dockerfiles for tmux support ([be810e4](https://github.com/pksorensen/pks-cli/commit/be810e45370a73c5c199ec48bcfc5aa5595496d1))
* Add issue links support in Jira issue model and browsing command ([edb1f68](https://github.com/pksorensen/pks-cli/commit/edb1f686c08e8642350e55ef7139f4e297e4423a))
* Add support for saving and managing JQL filters in Jira commands ([a48f636](https://github.com/pksorensen/pks-cli/commit/a48f636d09381854de738e782eb39fed1856b825))
* enhance Agentics runner with idle timeout and completion signal checks ([62ccfb0](https://github.com/pksorensen/pks-cli/commit/62ccfb0c850db9a449f32f0c6edf9c19fe098d3d))
* Enhance Coolify integration with environment support and improved app lookup ([8a479f5](https://github.com/pksorensen/pks-cli/commit/8a479f5d6431c082b7a6ade7d0cf29c5b0afab87))
* Enhance Coolify integration with environment support in job processing ([4e0b125](https://github.com/pksorensen/pks-cli/commit/4e0b12580eda2981664269c3e743b28c9e73a544))
* Enhance Coolify integration with environment-aware application lookup ([bf4370c](https://github.com/pksorensen/pks-cli/commit/bf4370cccd2d3641162c607beb3df92df76bb22b))
* Enhance Coolify integration with improved environment handling and strict matching logic ([2f3ad61](https://github.com/pksorensen/pks-cli/commit/2f3ad6147e9f7cb7eaaa7c263275cad7f0f9eadc))
* Enhance Jira authentication and validation with credential normalization and troubleshooting tips for Cloud ([e094eb6](https://github.com/pksorensen/pks-cli/commit/e094eb67d261a77ed13f1c5dfade34c06ff0632e))
* Enhance Jira authentication to support deployment type selection and username/password for Server ([321a7b8](https://github.com/pksorensen/pks-cli/commit/321a7b8363e946db1c5e84297bea737e1d87c8d6))
* Enhance Jira integration by adding support for comments, worklogs, and attachments in issue model and service ([38018c3](https://github.com/pksorensen/pks-cli/commit/38018c3f8a782b478793b336f49517aaa3b9cb97))
* Enhance Jira issue model and service to support additional fields and export functionality ([091c8fd](https://github.com/pksorensen/pks-cli/commit/091c8fd87916314573c09b5f179107f1d6718739))
* Enhance JiraBrowseCommand to load child issues recursively and improve selection interface ([f09d114](https://github.com/pksorensen/pks-cli/commit/f09d114adc232961defc8f83f6a29c3e97424fed))
* Enhance JiraBrowseCommand to load child issues recursively and improve selection interface ([f82135e](https://github.com/pksorensen/pks-cli/commit/f82135e19b84b24ba7e4a355b80d6a293e47cfaa))
* Enhance JiraBrowseCommand with async selection and export functionality for child issues ([ae9f5c8](https://github.com/pksorensen/pks-cli/commit/ae9f5c86231fe40bd16eeb651622e6e675efcaf0))
* Enhance loading indicator in JiraBrowseCommand with improved text formatting ([b86a260](https://github.com/pksorensen/pks-cli/commit/b86a26021bb12231241a01b71d42d3f5a2eb6729))
* Enhance nullability checks and error handling across various services and commands ([9fa310d](https://github.com/pksorensen/pks-cli/commit/9fa310dd54df82952b2a41f42d7c2151d4b54472))
* Implement async loading with spinner for Jira issue tree navigation ([473a625](https://github.com/pksorensen/pks-cli/commit/473a625bae3b547f979e248c2c16571188dbe3d2))
* Implement caching mechanism for stale Jira issues retrieval ([1787d93](https://github.com/pksorensen/pks-cli/commit/1787d93ced9220f6cb390e10c1eb385abdd9945d))
* Implement duplicate registration pruning for GitHub runner ([ea33d60](https://github.com/pksorensen/pks-cli/commit/ea33d60d1ca99c5524ab1484c42a1b37c3df940b))
* Implement interactive tree selection for Jira issues with expand/collapse and multi-select functionality ([f4485d6](https://github.com/pksorensen/pks-cli/commit/f4485d68d9ced511be18d0fce9a40283d9cac4f2))
* Implement Jira authentication command and service ([13b0512](https://github.com/pksorensen/pks-cli/commit/13b0512a7adf913ffe4fd6031971d18df2d0bbb6))
* Implement Jira issue raw fields retrieval and configuration command ([bb52594](https://github.com/pksorensen/pks-cli/commit/bb525943735065044876ebdf0d440eb7d14512fe))
* Implement local cache for previously exported Jira issues and enhance lazy loading of child issues ([04c7282](https://github.com/pksorensen/pks-cli/commit/04c7282ca507b024f58891843334ea617478aa18))
* Implement slugification for issue directory names in JiraBrowseCommand ([b73e857](https://github.com/pksorensen/pks-cli/commit/b73e857f9df1091df129a447fc0ddf0b42cc87dc))
* Improve rendering performance in JiraBrowseCommand with optimized redraw logic ([8ad6c6e](https://github.com/pksorensen/pks-cli/commit/8ad6c6e3a120ed2798a6503f8336fc42b6aae2e9))
* Refactor JiraBrowseCommand to support lazy loading of child issues and improve interactive selection ([ee85514](https://github.com/pksorensen/pks-cli/commit/ee855141190e1cb3aeee306850168c94fdfe83ad))
* Simplify authentication check logic in JiraService by streamlining token and auth method validation ([1f6fcc6](https://github.com/pksorensen/pks-cli/commit/1f6fcc62aa4474b5a5b03c514c7185501e22ffbb))
* Simplify console output in JiraBrowseCommand by replacing markup with plain text ([e282e1a](https://github.com/pksorensen/pks-cli/commit/e282e1a7b905a2b5d8b5662bae6e16127445b4cc))
* Update GitHub runner command options to use positional arguments for repository ([e0db545](https://github.com/pksorensen/pks-cli/commit/e0db545e3b838f3c6dff619cbad97c668344e8dd))
* Update JiraService to persist credentials globally and enhance issue search functionality for Cloud ([1333286](https://github.com/pksorensen/pks-cli/commit/1333286c3c0853622f6cb5be3a6f0a19094fd01d))


### Bug Fixes

* Allow nullable environment parameter in CoolifyLookupService method ([81d16e2](https://github.com/pksorensen/pks-cli/commit/81d16e2c0fd2475e81850f1df81a83b179777b5f))

## [6.0.0](https://github.com/pksorensen/pks-cli/compare/v5.0.0...v6.0.0) (2026-03-04)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please
* Release infrastructure replaced with Release Please

### Features

* Add Agentics runner management commands and configuration services ([a4fa663](https://github.com/pksorensen/pks-cli/commit/a4fa6630dff404f690934db9f32b3ee3f527bdf9))
* Add Azure DevOps authentication commands and services ([af70cb0](https://github.com/pksorensen/pks-cli/commit/af70cb0bb50daa9b69384f6c81c1b71275f8ce2f))
* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* Add Git credential server support and proactive token refresh for GitHub authentication ([06692a2](https://github.com/pksorensen/pks-cli/commit/06692a27d1630cd52f50270e03abefc480e74e0f))
* Add NamedContainerPool for managing reusable named containers ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* Add unit tests for RunnerConfigurationService, RunnerContainerService, and RunnerDaemonService ([0782bef](https://github.com/pksorensen/pks-cli/commit/0782bef1939ac97e96cf35e86513b89176c96702))
* **cli:** show refresh token status in ado status command ([9d88021](https://github.com/pksorensen/pks-cli/commit/9d88021f8060dd57165de5f28c6ebec7835777a3))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* Enhance GitHub authentication and logging, improve devcontainer setup ([8aa606e](https://github.com/pksorensen/pks-cli/commit/8aa606e0774eb13d405745b5f44fd8084dad5ccf))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* Implement AgenticsRunnerStartCommand to start runner daemon and poll for jobs ([317e02c](https://github.com/pksorensen/pks-cli/commit/317e02ccbf25995d6c33dedc13c526ddd485fbc0))
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* Implement token refresh functionality and container discovery in Runner services ([bcc9512](https://github.com/pksorensen/pks-cli/commit/bcc95122932fecd77677d9b1d6ba134cdec42a38))
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* migrate from semantic-release to Release Please ([f364bbb](https://github.com/pksorensen/pks-cli/commit/f364bbb5b3c3dd32043c72c7f47d8d33f5306107))
* migrate from semantic-release to Release Please ([a4d5488](https://github.com/pksorensen/pks-cli/commit/a4d5488775b775948779d2738c572f1086213644))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* **cli:** add XML doc comment to AdoSettings class ([d3d514d](https://github.com/pksorensen/pks-cli/commit/d3d514d52cbc9b48ac7b64423ed805269e5b635e))
* **cli:** correct npm publish to use OIDC trusted publishing ([793d141](https://github.com/pksorensen/pks-cli/commit/793d14190974e7808be3dec61598da51bfd49322))
* **cli:** extract token status variable in AdoStatusCommand ([f38ae86](https://github.com/pksorensen/pks-cli/commit/f38ae86e2bc40979f49c9a7c1ba90fb63ad87819))
* **cli:** improve AdoInitCommand documentation ([c864194](https://github.com/pksorensen/pks-cli/commit/c8641948062808ae4a876779528e051a05cf0187))
* **cli:** improve AdoStatusCommand doc comment ([749d698](https://github.com/pksorensen/pks-cli/commit/749d6989df24bd8f3c0f518c5d6c85c9e804379c))
* **cli:** remove duplicate using directive in Program.cs ([363a41f](https://github.com/pksorensen/pks-cli/commit/363a41f3543e107316e160462cb5842b8768f000))
* **cli:** resolve formatting and platform compatibility issues ([6ec9d8e](https://github.com/pksorensen/pks-cli/commit/6ec9d8e13e9277c90614237550256ce1c3f7697d))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* reset manifest to stable version baselines for main branch ([b49145a](https://github.com/pksorensen/pks-cli/commit/b49145a5d867858bcadd7f20c656a4dd6b508799))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))
* update NuGet packages to 6.12.4 and remove unnecessary System.IO.Compression ([fcba202](https://github.com/pksorensen/pks-cli/commit/fcba202f8582f40c06f8306043f96442de388c9e))
* Update RunnerDaemonService to utilize NamedContainerPool ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))

## [5.1.3-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.1.2-rc.28...v5.1.3-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** extract token status variable in AdoStatusCommand ([f38ae86](https://github.com/pksorensen/pks-cli/commit/f38ae86e2bc40979f49c9a7c1ba90fb63ad87819))

## [5.1.2-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.1.1-rc.28...v5.1.2-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** add XML doc comment to AdoSettings class ([d3d514d](https://github.com/pksorensen/pks-cli/commit/d3d514d52cbc9b48ac7b64423ed805269e5b635e))

## [5.1.1-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.1.0-rc.28...v5.1.1-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** improve AdoInitCommand documentation ([c864194](https://github.com/pksorensen/pks-cli/commit/c8641948062808ae4a876779528e051a05cf0187))

## [5.1.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.0.3-rc.28...v5.1.0-rc.28) (2026-03-04)


### Features

* **cli:** show refresh token status in ado status command ([9d88021](https://github.com/pksorensen/pks-cli/commit/9d88021f8060dd57165de5f28c6ebec7835777a3))

## [5.0.3-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.0.2-rc.28...v5.0.3-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** improve AdoStatusCommand doc comment ([749d698](https://github.com/pksorensen/pks-cli/commit/749d6989df24bd8f3c0f518c5d6c85c9e804379c))

## [5.0.2-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.0.1-rc.28...v5.0.2-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** remove duplicate using directive in Program.cs ([363a41f](https://github.com/pksorensen/pks-cli/commit/363a41f3543e107316e160462cb5842b8768f000))

## [5.0.1-rc.28](https://github.com/pksorensen/pks-cli/compare/v5.0.0-rc.28...v5.0.1-rc.28) (2026-03-04)


### Bug Fixes

* **cli:** correct npm publish to use OIDC trusted publishing ([793d141](https://github.com/pksorensen/pks-cli/commit/793d14190974e7808be3dec61598da51bfd49322))

## [5.0.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v4.0.0-rc.28...v5.0.0-rc.28) (2026-03-04)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please
* Release infrastructure replaced with Release Please

### Features

* Add Agentics runner management commands and configuration services ([a4fa663](https://github.com/pksorensen/pks-cli/commit/a4fa6630dff404f690934db9f32b3ee3f527bdf9))
* Add Azure DevOps authentication commands and services ([af70cb0](https://github.com/pksorensen/pks-cli/commit/af70cb0bb50daa9b69384f6c81c1b71275f8ce2f))
* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* Add Git credential server support and proactive token refresh for GitHub authentication ([06692a2](https://github.com/pksorensen/pks-cli/commit/06692a27d1630cd52f50270e03abefc480e74e0f))
* Add NamedContainerPool for managing reusable named containers ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* Add unit tests for RunnerConfigurationService, RunnerContainerService, and RunnerDaemonService ([0782bef](https://github.com/pksorensen/pks-cli/commit/0782bef1939ac97e96cf35e86513b89176c96702))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* Enhance GitHub authentication and logging, improve devcontainer setup ([8aa606e](https://github.com/pksorensen/pks-cli/commit/8aa606e0774eb13d405745b5f44fd8084dad5ccf))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* Implement AgenticsRunnerStartCommand to start runner daemon and poll for jobs ([317e02c](https://github.com/pksorensen/pks-cli/commit/317e02ccbf25995d6c33dedc13c526ddd485fbc0))
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* Implement token refresh functionality and container discovery in Runner services ([bcc9512](https://github.com/pksorensen/pks-cli/commit/bcc95122932fecd77677d9b1d6ba134cdec42a38))
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* migrate from semantic-release to Release Please ([f364bbb](https://github.com/pksorensen/pks-cli/commit/f364bbb5b3c3dd32043c72c7f47d8d33f5306107))
* migrate from semantic-release to Release Please ([a4d5488](https://github.com/pksorensen/pks-cli/commit/a4d5488775b775948779d2738c572f1086213644))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* reset manifest to stable version baselines for main branch ([b49145a](https://github.com/pksorensen/pks-cli/commit/b49145a5d867858bcadd7f20c656a4dd6b508799))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))
* update NuGet packages to 6.12.4 and remove unnecessary System.IO.Compression ([fcba202](https://github.com/pksorensen/pks-cli/commit/fcba202f8582f40c06f8306043f96442de388c9e))
* Update RunnerDaemonService to utilize NamedContainerPool ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))

## [4.0.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v3.1.0-rc.28...v4.0.0-rc.28) (2026-03-03)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please
* Release infrastructure replaced with Release Please

### Features

* Add Agentics runner management commands and configuration services ([a4fa663](https://github.com/pksorensen/pks-cli/commit/a4fa6630dff404f690934db9f32b3ee3f527bdf9))
* Add Azure DevOps authentication commands and services ([af70cb0](https://github.com/pksorensen/pks-cli/commit/af70cb0bb50daa9b69384f6c81c1b71275f8ce2f))
* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* Add Git credential server support and proactive token refresh for GitHub authentication ([06692a2](https://github.com/pksorensen/pks-cli/commit/06692a27d1630cd52f50270e03abefc480e74e0f))
* Add NamedContainerPool for managing reusable named containers ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* Add unit tests for RunnerConfigurationService, RunnerContainerService, and RunnerDaemonService ([0782bef](https://github.com/pksorensen/pks-cli/commit/0782bef1939ac97e96cf35e86513b89176c96702))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* Enhance GitHub authentication and logging, improve devcontainer setup ([8aa606e](https://github.com/pksorensen/pks-cli/commit/8aa606e0774eb13d405745b5f44fd8084dad5ccf))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* Implement AgenticsRunnerStartCommand to start runner daemon and poll for jobs ([317e02c](https://github.com/pksorensen/pks-cli/commit/317e02ccbf25995d6c33dedc13c526ddd485fbc0))
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* Implement token refresh functionality and container discovery in Runner services ([bcc9512](https://github.com/pksorensen/pks-cli/commit/bcc95122932fecd77677d9b1d6ba134cdec42a38))
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* migrate from semantic-release to Release Please ([f364bbb](https://github.com/pksorensen/pks-cli/commit/f364bbb5b3c3dd32043c72c7f47d8d33f5306107))
* migrate from semantic-release to Release Please ([a4d5488](https://github.com/pksorensen/pks-cli/commit/a4d5488775b775948779d2738c572f1086213644))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* reset manifest to stable version baselines for main branch ([b49145a](https://github.com/pksorensen/pks-cli/commit/b49145a5d867858bcadd7f20c656a4dd6b508799))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))
* update NuGet packages to 6.12.4 and remove unnecessary System.IO.Compression ([fcba202](https://github.com/pksorensen/pks-cli/commit/fcba202f8582f40c06f8306043f96442de388c9e))
* Update RunnerDaemonService to utilize NamedContainerPool ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))

## [3.1.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v3.0.0-rc.28...v3.1.0-rc.28) (2026-02-26)


### Features

* Add NamedContainerPool for managing reusable named containers ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))
* Add unit tests for RunnerConfigurationService, RunnerContainerService, and RunnerDaemonService ([0782bef](https://github.com/pksorensen/pks-cli/commit/0782bef1939ac97e96cf35e86513b89176c96702))


### Bug Fixes

* update NuGet packages to 6.12.4 and remove unnecessary System.IO.Compression ([fcba202](https://github.com/pksorensen/pks-cli/commit/fcba202f8582f40c06f8306043f96442de388c9e))
* Update RunnerDaemonService to utilize NamedContainerPool ([0bd33c5](https://github.com/pksorensen/pks-cli/commit/0bd33c5e22ee756702c06580173b89bb90adae9b))

## [3.0.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v2.0.0-rc.28...v3.0.0-rc.28) (2026-02-22)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please

### Features

* migrate from semantic-release to Release Please ([f364bbb](https://github.com/pksorensen/pks-cli/commit/f364bbb5b3c3dd32043c72c7f47d8d33f5306107))

## [3.0.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v2.0.0-rc.28...v3.0.0-rc.28) (2026-02-21)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please

### Features

* migrate from semantic-release to Release Please ([f364bbb](https://github.com/pksorensen/pks-cli/commit/f364bbb5b3c3dd32043c72c7f47d8d33f5306107))

## [2.0.0-rc.28](https://github.com/pksorensen/pks-cli/compare/v1.2.0-rc.28...v2.0.0-rc.28) (2026-02-21)


### ⚠ BREAKING CHANGES

* Release infrastructure replaced with Release Please

### Features

* Add comprehensive documentation on VS Code Dev Containers enhancements vs devcontainer CLI ([7c1d65c](https://github.com/pksorensen/pks-cli/commit/7c1d65c765337dc333b29e500997c38f1033f2d9))
* Add devcontainer spawning functionality and related services ([16f9905](https://github.com/pksorensen/pks-cli/commit/16f9905846dc8b095a5e569ca2b65124de450c63))
* Add progress reporting to devcontainer spawning operations ([1137805](https://github.com/pksorensen/pks-cli/commit/11378051ca6871cb2e67a8eec9f51d78b75c363b))
* Add self-contained build and npm version synchronization scripts ([cd6202f](https://github.com/pksorensen/pks-cli/commit/cd6202fcbb6017e6cf0c2a7ce7098d94bcfbf09d))
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* Enable Docker credential forwarding by default and fix postStartCommand for directory creation ([41f0fd8](https://github.com/pksorensen/pks-cli/commit/41f0fd841be9d786a39c51bafd6eb05403da3bbe))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance CreateOverrideConfigWithJsonElementAsync to include volume name for unique config files ([c614d0a](https://github.com/pksorensen/pks-cli/commit/c614d0a12531acd08bc49379ec001deaf3f6182b))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* Enhance RunDevcontainerUpInBootstrapAsync with Docker config forwarding options ([fd4e827](https://github.com/pksorensen/pks-cli/commit/fd4e82758010770e62922cd7079528b5492eeaed))
* Implement bootstrap container strategy for cross-platform devcontainer support ([f072597](https://github.com/pksorensen/pks-cli/commit/f072597f0eeed59cd201b7803a9b401fe656ea89))
* Implement configuration hash detection and synchronization for devcontainers ([e93f0f5](https://github.com/pksorensen/pks-cli/commit/e93f0f56ca68f2a42ec23fd6d9591f9b9964ef01))
* Implement configuration hash service for devcontainer change detection and enhance rebuild options ([43afb65](https://github.com/pksorensen/pks-cli/commit/43afb65efa802467566ca96cebf738bb231371e6))
* Improve Docker socket handling in DevcontainerSpawnerService and add new Dockerfile for VS Code Dev Containers ([6a50c9c](https://github.com/pksorensen/pks-cli/commit/6a50c9cd0fc436ffa8e739c4c51a2cb6507c97be))
* migrate from semantic-release to Release Please ([a4d5488](https://github.com/pksorensen/pks-cli/commit/a4d5488775b775948779d2738c572f1086213644))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### Bug Fixes

* Add functionality to connect to existing devcontainers and start them if stopped ([8fcdb2b](https://github.com/pksorensen/pks-cli/commit/8fcdb2b32e8803f998db9af57ea94d8fc4c04212))
* add git notes to v1.2.0-rc.10 for semantic-release tracking ([0871b5d](https://github.com/pksorensen/pks-cli/commit/0871b5df26580d33d9978eb2880f59bb257bafbc))
* Correct label matching for existing containers and improve logging for project identification ([4ce218f](https://github.com/pksorensen/pks-cli/commit/4ce218f528e5a6ee700bf8dae548cba8a269e5e5))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* DevcontainerSpawnerService to improve override config handling ([3dd4578](https://github.com/pksorensen/pks-cli/commit/3dd45788c228c9056d7c431d7a7a0802e8305915))
* display .NET runtime version in welcome banner ([1a554f6](https://github.com/pksorensen/pks-cli/commit/1a554f6a969ace1b3809617f4b497b3b295b9c2b))
* Enhance Docker credential handling and fix file ownership issues in devcontainer spawning ([0b0ff68](https://github.com/pksorensen/pks-cli/commit/0b0ff68f7c5343086cd3598bb4c44a0a30eb9281))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* Improve JSON normalization by handling direct parsing and comment removal more effectively ([f938279](https://github.com/pksorensen/pks-cli/commit/f938279d2c2d60768b5cac35158a293f9dd419da))
* trigger semantic-release to create v1.2.0-rc.11 ([230763d](https://github.com/pksorensen/pks-cli/commit/230763de7ad31f15e0273ad4d94805c2e90cb136))
* Update Docker credential handling and improve workspace folder resolution in DevcontainerSpawnerService ([688f6d2](https://github.com/pksorensen/pks-cli/commit/688f6d2d1d53cb0ac0cdcf278153cdd7e6b8daaf))

## [1.2.0-rc.9] - Latest

See releases for detailed changelog.
