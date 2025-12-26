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
* CLI workflow should only use CLI tags (v*), not template tags ([7984271](https://github.com/pksorensen/pks-cli/commit/7984271909b40b400b5b651b43e1aa47cfb631e8))
* convert semver to numeric format for AssemblyVersion ([1d0034d](https://github.com/pksorensen/pks-cli/commit/1d0034d495159f9b22df45d83e77801a36560fe2))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* embed version in assemblies during pack by rebuilding with version properties ([cddc2f6](https://github.com/pksorensen/pks-cli/commit/cddc2f6ef0cc0e1974e53c4087315758f91a1d23))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove successCmd that was aborting pre-release creation ([7023f0b](https://github.com/pksorensen/pks-cli/commit/7023f0b4571b3436e754ab22b29a0c255d8d4c2c))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))


### 🔧 Chores

* **release:** 1.0.0-rc.1 [skip ci] ([fc5bf06](https://github.com/pksorensen/pks-cli/commit/fc5bf0660fd622527041ecdb8bcc89645edc4d18)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([24152f1](https://github.com/pksorensen/pks-cli/commit/24152f1405c60889d4375a30ab45ac6f9fc36e0b)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([bc72168](https://github.com/pksorensen/pks-cli/commit/bc72168839fd0a61ae0bb0517340adc2b5738461)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([1761d05](https://github.com/pksorensen/pks-cli/commit/1761d05aee44e5af74bce55dd40568f1551aa70c)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.1.0-rc.5 [skip ci] ([973edee](https://github.com/pksorensen/pks-cli/commit/973edee27799ab55004230e6aa788153043721d9))
* **release:** 1.1.0-rc.6 [skip ci] ([014c2dc](https://github.com/pksorensen/pks-cli/commit/014c2dc0a50c1c597b4ff2b9d876f57b2e48eba3))
* **release:** 1.1.0-rc.7 [skip ci] ([54387b6](https://github.com/pksorensen/pks-cli/commit/54387b665d15c90013604cd7ee066d0945cd55e4))
* **release:** 1.2.0-rc.1 [skip ci] ([a61d044](https://github.com/pksorensen/pks-cli/commit/a61d044631666a8489e2007cb03ab9a0e100308e))
* **release:** 1.2.0-rc.10 [skip ci] ([62fb730](https://github.com/pksorensen/pks-cli/commit/62fb73005cece0176c8a0e3bb1030d3f5c0617de))
* **release:** 1.2.0-rc.2 [skip ci] ([f4fdb6b](https://github.com/pksorensen/pks-cli/commit/f4fdb6baa1625192ae38649d93207cf39c62a4e3))
* **release:** 1.2.0-rc.3 [skip ci] ([b166766](https://github.com/pksorensen/pks-cli/commit/b166766212972368032b964435300fb788614289))
* **release:** 1.2.0-rc.4 [skip ci] ([d19af0d](https://github.com/pksorensen/pks-cli/commit/d19af0de1c37a5fd0c8538dee749cc383fc0371a))
* **release:** 1.2.0-rc.5 [skip ci] ([ff3e7dc](https://github.com/pksorensen/pks-cli/commit/ff3e7dcb52b1cd50cfea635066acf0ef7a5838f9))
* **release:** 1.2.0-rc.6 [skip ci] ([5bd2e8c](https://github.com/pksorensen/pks-cli/commit/5bd2e8c794c3b1a71ecf62162905a3ab1244d01f))
* **release:** 1.2.0-rc.7 [skip ci] ([977c8e8](https://github.com/pksorensen/pks-cli/commit/977c8e8ed46aec00527e5934a2cdd0767b53dc4a))
* **release:** 1.2.0-rc.8 [skip ci] ([5a44053](https://github.com/pksorensen/pks-cli/commit/5a440530269ec198347a4fcf579deb5d6cf1f071))
* **release:** 1.2.0-rc.9 [skip ci] ([5acad92](https://github.com/pksorensen/pks-cli/commit/5acad920fb40ff27ccfb7f223952ab3e8d324433))

## 1.0.0-rc.1 (2025-12-26)


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


### 🚀 Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
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
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
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
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### 🐛 Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* CLI workflow should only use CLI tags (v*), not template tags ([7984271](https://github.com/pksorensen/pks-cli/commit/7984271909b40b400b5b651b43e1aa47cfb631e8))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* convert semver to numeric format for AssemblyVersion ([1d0034d](https://github.com/pksorensen/pks-cli/commit/1d0034d495159f9b22df45d83e77801a36560fe2))
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* embed version in assemblies during pack by rebuilding with version properties ([cddc2f6](https://github.com/pksorensen/pks-cli/commit/cddc2f6ef0cc0e1974e53c4087315758f91a1d23))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
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
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))


### ⚡ Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### 📚 Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### 🔧 Chores

* add comprehensive .NET gitignore patterns ([83146cf](https://github.com/pksorensen/pks-cli/commit/83146cf9271732292a9a73f962f1356a44832c2d))
* clean up obsolete files ([e777c49](https://github.com/pksorensen/pks-cli/commit/e777c49981e7068e49692c39393e60a639b91c9d))
* **release:** 1.0.0 [skip ci] ([97906a2](https://github.com/pksorensen/pks-cli/commit/97906a2a29f0b2f337f73f687042c58373384f75)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([24152f1](https://github.com/pksorensen/pks-cli/commit/24152f1405c60889d4375a30ab45ac6f9fc36e0b)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([bc72168](https://github.com/pksorensen/pks-cli/commit/bc72168839fd0a61ae0bb0517340adc2b5738461)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([1761d05](https://github.com/pksorensen/pks-cli/commit/1761d05aee44e5af74bce55dd40568f1551aa70c)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([d518dc8](https://github.com/pksorensen/pks-cli/commit/d518dc8ec840b3745ed7b96975b0563a927b6b97)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.2 [skip ci] ([d56c908](https://github.com/pksorensen/pks-cli/commit/d56c908c5e3838e9819968f8a2538db2d2be2fdd))
* **release:** 1.0.1 [skip ci] ([18b246f](https://github.com/pksorensen/pks-cli/commit/18b246f5e5b3da6b7194847d014ae2aefedea3c9))
* **release:** 1.0.1-rc.1 [skip ci] ([baaeb81](https://github.com/pksorensen/pks-cli/commit/baaeb81dd6751704339144d17379e4880ccd16fb))
* **release:** 1.1.0 [skip ci] ([4174048](https://github.com/pksorensen/pks-cli/commit/417404843ac5446b9e9fdbb4c6bbf32720d5adcb))
* **release:** 1.1.0-rc.1 [skip ci] ([79c44e2](https://github.com/pksorensen/pks-cli/commit/79c44e206a01d9487085f4ce30ac696684c1aff5))
* **release:** 1.1.0-rc.2 [skip ci] ([906c3f6](https://github.com/pksorensen/pks-cli/commit/906c3f6bb1c9daf820aeee665a0c9c724f48b788))
* **release:** 1.1.0-rc.3 [skip ci] ([5580a6a](https://github.com/pksorensen/pks-cli/commit/5580a6aee6db5d1260280f396fcd658e9e1c1be5))
* **release:** 1.1.0-rc.4 [skip ci] ([80af657](https://github.com/pksorensen/pks-cli/commit/80af6573473fa13700eeadf9ef1938d02a214e61))
* **release:** 1.1.0-rc.5 [skip ci] ([973edee](https://github.com/pksorensen/pks-cli/commit/973edee27799ab55004230e6aa788153043721d9))
* **release:** 1.1.0-rc.6 [skip ci] ([014c2dc](https://github.com/pksorensen/pks-cli/commit/014c2dc0a50c1c597b4ff2b9d876f57b2e48eba3))
* **release:** 1.1.0-rc.7 [skip ci] ([54387b6](https://github.com/pksorensen/pks-cli/commit/54387b665d15c90013604cd7ee066d0945cd55e4))
* **release:** 1.2.0-rc.1 [skip ci] ([a61d044](https://github.com/pksorensen/pks-cli/commit/a61d044631666a8489e2007cb03ab9a0e100308e))
* **release:** 1.2.0-rc.10 [skip ci] ([62fb730](https://github.com/pksorensen/pks-cli/commit/62fb73005cece0176c8a0e3bb1030d3f5c0617de))
* **release:** 1.2.0-rc.2 [skip ci] ([f4fdb6b](https://github.com/pksorensen/pks-cli/commit/f4fdb6baa1625192ae38649d93207cf39c62a4e3))
* **release:** 1.2.0-rc.3 [skip ci] ([b166766](https://github.com/pksorensen/pks-cli/commit/b166766212972368032b964435300fb788614289))
* **release:** 1.2.0-rc.4 [skip ci] ([d19af0d](https://github.com/pksorensen/pks-cli/commit/d19af0de1c37a5fd0c8538dee749cc383fc0371a))
* **release:** 1.2.0-rc.5 [skip ci] ([ff3e7dc](https://github.com/pksorensen/pks-cli/commit/ff3e7dcb52b1cd50cfea635066acf0ef7a5838f9))
* **release:** 1.2.0-rc.6 [skip ci] ([5bd2e8c](https://github.com/pksorensen/pks-cli/commit/5bd2e8c794c3b1a71ecf62162905a3ab1244d01f))
* **release:** 1.2.0-rc.7 [skip ci] ([977c8e8](https://github.com/pksorensen/pks-cli/commit/977c8e8ed46aec00527e5934a2cdd0767b53dc4a))
* **release:** 1.2.0-rc.8 [skip ci] ([5a44053](https://github.com/pksorensen/pks-cli/commit/5a440530269ec198347a4fcf579deb5d6cf1f071))
* **release:** 1.2.0-rc.9 [skip ci] ([5acad92](https://github.com/pksorensen/pks-cli/commit/5acad920fb40ff27ccfb7f223952ab3e8d324433))
* update commands and services for compatibility ([c1a419a](https://github.com/pksorensen/pks-cli/commit/c1a419acae91be4c8ef555fd08a3d5241fd35d9c))
* update gitignore for Claude Code configurations ([2fb3ed5](https://github.com/pksorensen/pks-cli/commit/2fb3ed5fe5401066dab9420135fb028b59d315f1))


### ♻️ Code Refactoring

* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))


### ✅ Tests

* add comprehensive devcontainer test suite ([ebc3f1f](https://github.com/pksorensen/pks-cli/commit/ebc3f1fda554d0dd9647997b751aedcd1b4b116e))
* add comprehensive PRD command test suite ([e792ab2](https://github.com/pksorensen/pks-cli/commit/e792ab22174ef5b4a95c13750c66e441e7f97f9c))
* add comprehensive test coverage for new features ([9ced585](https://github.com/pksorensen/pks-cli/commit/9ced5854876ad69196be2f055dcde7e9e0d6496e))
* add comprehensive test suite and documentation for report command ([bc23307](https://github.com/pksorensen/pks-cli/commit/bc233076afcdff6f39d841ffc3fb0922e4f5f929))
* add integration tests for devcontainer templates ([75d7f32](https://github.com/pksorensen/pks-cli/commit/75d7f32ead0db90d2a944cdca3e81e71262543c7))
* enhance service mocking and test reliability ([541aa5c](https://github.com/pksorensen/pks-cli/commit/541aa5cea8895eb0ebc887aa6ad23b0aa268d8da))
* fix GitHub Copilot review comments ([c0bf743](https://github.com/pksorensen/pks-cli/commit/c0bf7433cefa6e9b6cbe3003caa28ca8a3cbd05e)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* fix hooks service test issues ([d9ade66](https://github.com/pksorensen/pks-cli/commit/d9ade6696904b3c1827149d5ba951ba87151c62b))
* update test suite for new architecture ([c4b0510](https://github.com/pksorensen/pks-cli/commit/c4b05107bea24f2249a99b74f7a8eefe84825dba))


### 📦 Build System

* reorganize solution structure ([58bda0d](https://github.com/pksorensen/pks-cli/commit/58bda0d964e87b838eeeb1ccce9d08e1e2dca589))


### 👷 CI/CD

* add GitHub Actions workflow for automated testing ([274bd80](https://github.com/pksorensen/pks-cli/commit/274bd80862a9dd88160e6a99a680c714a92a0c33))
* fix CI build by excluding nullable warnings from warnings-as-errors ([a16cae9](https://github.com/pksorensen/pks-cli/commit/a16cae927acea4e7d2744a5a2fc456f429a087b5))
* fix GitHub Actions workflows for PR validation ([2d5b24c](https://github.com/pksorensen/pks-cli/commit/2d5b24cfd908741cc12e9c98e7e5f3f603f88b00)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* streamline GitHub workflows for PR success ([a923ed2](https://github.com/pksorensen/pks-cli/commit/a923ed20e01a7cd2732724c79ad726a1dd7fb194))
* update test workflow to use core stable tests only ([1797fc5](https://github.com/pksorensen/pks-cli/commit/1797fc5aad53aaf17865806dfbcf1b85d115fb23))

## 1.0.0-rc.1 (2025-12-26)


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


### 🚀 Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
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
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
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
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### 🐛 Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* CLI workflow should only use CLI tags (v*), not template tags ([7984271](https://github.com/pksorensen/pks-cli/commit/7984271909b40b400b5b651b43e1aa47cfb631e8))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* embed version in assemblies during pack by rebuilding with version properties ([cddc2f6](https://github.com/pksorensen/pks-cli/commit/cddc2f6ef0cc0e1974e53c4087315758f91a1d23))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
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
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))


### ⚡ Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### 📚 Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### 🔧 Chores

* add comprehensive .NET gitignore patterns ([83146cf](https://github.com/pksorensen/pks-cli/commit/83146cf9271732292a9a73f962f1356a44832c2d))
* clean up obsolete files ([e777c49](https://github.com/pksorensen/pks-cli/commit/e777c49981e7068e49692c39393e60a639b91c9d))
* **release:** 1.0.0 [skip ci] ([97906a2](https://github.com/pksorensen/pks-cli/commit/97906a2a29f0b2f337f73f687042c58373384f75)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([bc72168](https://github.com/pksorensen/pks-cli/commit/bc72168839fd0a61ae0bb0517340adc2b5738461)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([1761d05](https://github.com/pksorensen/pks-cli/commit/1761d05aee44e5af74bce55dd40568f1551aa70c)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([d518dc8](https://github.com/pksorensen/pks-cli/commit/d518dc8ec840b3745ed7b96975b0563a927b6b97)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.2 [skip ci] ([d56c908](https://github.com/pksorensen/pks-cli/commit/d56c908c5e3838e9819968f8a2538db2d2be2fdd))
* **release:** 1.0.1 [skip ci] ([18b246f](https://github.com/pksorensen/pks-cli/commit/18b246f5e5b3da6b7194847d014ae2aefedea3c9))
* **release:** 1.0.1-rc.1 [skip ci] ([baaeb81](https://github.com/pksorensen/pks-cli/commit/baaeb81dd6751704339144d17379e4880ccd16fb))
* **release:** 1.1.0 [skip ci] ([4174048](https://github.com/pksorensen/pks-cli/commit/417404843ac5446b9e9fdbb4c6bbf32720d5adcb))
* **release:** 1.1.0-rc.1 [skip ci] ([79c44e2](https://github.com/pksorensen/pks-cli/commit/79c44e206a01d9487085f4ce30ac696684c1aff5))
* **release:** 1.1.0-rc.2 [skip ci] ([906c3f6](https://github.com/pksorensen/pks-cli/commit/906c3f6bb1c9daf820aeee665a0c9c724f48b788))
* **release:** 1.1.0-rc.3 [skip ci] ([5580a6a](https://github.com/pksorensen/pks-cli/commit/5580a6aee6db5d1260280f396fcd658e9e1c1be5))
* **release:** 1.1.0-rc.4 [skip ci] ([80af657](https://github.com/pksorensen/pks-cli/commit/80af6573473fa13700eeadf9ef1938d02a214e61))
* **release:** 1.1.0-rc.5 [skip ci] ([973edee](https://github.com/pksorensen/pks-cli/commit/973edee27799ab55004230e6aa788153043721d9))
* **release:** 1.1.0-rc.6 [skip ci] ([014c2dc](https://github.com/pksorensen/pks-cli/commit/014c2dc0a50c1c597b4ff2b9d876f57b2e48eba3))
* **release:** 1.1.0-rc.7 [skip ci] ([54387b6](https://github.com/pksorensen/pks-cli/commit/54387b665d15c90013604cd7ee066d0945cd55e4))
* **release:** 1.2.0-rc.1 [skip ci] ([a61d044](https://github.com/pksorensen/pks-cli/commit/a61d044631666a8489e2007cb03ab9a0e100308e))
* **release:** 1.2.0-rc.10 [skip ci] ([62fb730](https://github.com/pksorensen/pks-cli/commit/62fb73005cece0176c8a0e3bb1030d3f5c0617de))
* **release:** 1.2.0-rc.2 [skip ci] ([f4fdb6b](https://github.com/pksorensen/pks-cli/commit/f4fdb6baa1625192ae38649d93207cf39c62a4e3))
* **release:** 1.2.0-rc.3 [skip ci] ([b166766](https://github.com/pksorensen/pks-cli/commit/b166766212972368032b964435300fb788614289))
* **release:** 1.2.0-rc.4 [skip ci] ([d19af0d](https://github.com/pksorensen/pks-cli/commit/d19af0de1c37a5fd0c8538dee749cc383fc0371a))
* **release:** 1.2.0-rc.5 [skip ci] ([ff3e7dc](https://github.com/pksorensen/pks-cli/commit/ff3e7dcb52b1cd50cfea635066acf0ef7a5838f9))
* **release:** 1.2.0-rc.6 [skip ci] ([5bd2e8c](https://github.com/pksorensen/pks-cli/commit/5bd2e8c794c3b1a71ecf62162905a3ab1244d01f))
* **release:** 1.2.0-rc.7 [skip ci] ([977c8e8](https://github.com/pksorensen/pks-cli/commit/977c8e8ed46aec00527e5934a2cdd0767b53dc4a))
* **release:** 1.2.0-rc.8 [skip ci] ([5a44053](https://github.com/pksorensen/pks-cli/commit/5a440530269ec198347a4fcf579deb5d6cf1f071))
* **release:** 1.2.0-rc.9 [skip ci] ([5acad92](https://github.com/pksorensen/pks-cli/commit/5acad920fb40ff27ccfb7f223952ab3e8d324433))
* update commands and services for compatibility ([c1a419a](https://github.com/pksorensen/pks-cli/commit/c1a419acae91be4c8ef555fd08a3d5241fd35d9c))
* update gitignore for Claude Code configurations ([2fb3ed5](https://github.com/pksorensen/pks-cli/commit/2fb3ed5fe5401066dab9420135fb028b59d315f1))


### ♻️ Code Refactoring

* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))


### ✅ Tests

* add comprehensive devcontainer test suite ([ebc3f1f](https://github.com/pksorensen/pks-cli/commit/ebc3f1fda554d0dd9647997b751aedcd1b4b116e))
* add comprehensive PRD command test suite ([e792ab2](https://github.com/pksorensen/pks-cli/commit/e792ab22174ef5b4a95c13750c66e441e7f97f9c))
* add comprehensive test coverage for new features ([9ced585](https://github.com/pksorensen/pks-cli/commit/9ced5854876ad69196be2f055dcde7e9e0d6496e))
* add comprehensive test suite and documentation for report command ([bc23307](https://github.com/pksorensen/pks-cli/commit/bc233076afcdff6f39d841ffc3fb0922e4f5f929))
* add integration tests for devcontainer templates ([75d7f32](https://github.com/pksorensen/pks-cli/commit/75d7f32ead0db90d2a944cdca3e81e71262543c7))
* enhance service mocking and test reliability ([541aa5c](https://github.com/pksorensen/pks-cli/commit/541aa5cea8895eb0ebc887aa6ad23b0aa268d8da))
* fix GitHub Copilot review comments ([c0bf743](https://github.com/pksorensen/pks-cli/commit/c0bf7433cefa6e9b6cbe3003caa28ca8a3cbd05e)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* fix hooks service test issues ([d9ade66](https://github.com/pksorensen/pks-cli/commit/d9ade6696904b3c1827149d5ba951ba87151c62b))
* update test suite for new architecture ([c4b0510](https://github.com/pksorensen/pks-cli/commit/c4b05107bea24f2249a99b74f7a8eefe84825dba))


### 📦 Build System

* reorganize solution structure ([58bda0d](https://github.com/pksorensen/pks-cli/commit/58bda0d964e87b838eeeb1ccce9d08e1e2dca589))


### 👷 CI/CD

* add GitHub Actions workflow for automated testing ([274bd80](https://github.com/pksorensen/pks-cli/commit/274bd80862a9dd88160e6a99a680c714a92a0c33))
* fix CI build by excluding nullable warnings from warnings-as-errors ([a16cae9](https://github.com/pksorensen/pks-cli/commit/a16cae927acea4e7d2744a5a2fc456f429a087b5))
* fix GitHub Actions workflows for PR validation ([2d5b24c](https://github.com/pksorensen/pks-cli/commit/2d5b24cfd908741cc12e9c98e7e5f3f603f88b00)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* streamline GitHub workflows for PR success ([a923ed2](https://github.com/pksorensen/pks-cli/commit/a923ed20e01a7cd2732724c79ad726a1dd7fb194))
* update test workflow to use core stable tests only ([1797fc5](https://github.com/pksorensen/pks-cli/commit/1797fc5aad53aaf17865806dfbcf1b85d115fb23))

## 1.0.0-rc.1 (2025-12-26)


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


### 🚀 Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
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
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
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
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### 🐛 Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* embed version in assemblies during pack by rebuilding with version properties ([cddc2f6](https://github.com/pksorensen/pks-cli/commit/cddc2f6ef0cc0e1974e53c4087315758f91a1d23))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
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
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))


### ⚡ Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### 📚 Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### 🔧 Chores

* add comprehensive .NET gitignore patterns ([83146cf](https://github.com/pksorensen/pks-cli/commit/83146cf9271732292a9a73f962f1356a44832c2d))
* clean up obsolete files ([e777c49](https://github.com/pksorensen/pks-cli/commit/e777c49981e7068e49692c39393e60a639b91c9d))
* **release:** 1.0.0 [skip ci] ([97906a2](https://github.com/pksorensen/pks-cli/commit/97906a2a29f0b2f337f73f687042c58373384f75)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([1761d05](https://github.com/pksorensen/pks-cli/commit/1761d05aee44e5af74bce55dd40568f1551aa70c)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([d518dc8](https://github.com/pksorensen/pks-cli/commit/d518dc8ec840b3745ed7b96975b0563a927b6b97)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.2 [skip ci] ([d56c908](https://github.com/pksorensen/pks-cli/commit/d56c908c5e3838e9819968f8a2538db2d2be2fdd))
* **release:** 1.0.1 [skip ci] ([18b246f](https://github.com/pksorensen/pks-cli/commit/18b246f5e5b3da6b7194847d014ae2aefedea3c9))
* **release:** 1.0.1-rc.1 [skip ci] ([baaeb81](https://github.com/pksorensen/pks-cli/commit/baaeb81dd6751704339144d17379e4880ccd16fb))
* **release:** 1.1.0 [skip ci] ([4174048](https://github.com/pksorensen/pks-cli/commit/417404843ac5446b9e9fdbb4c6bbf32720d5adcb))
* **release:** 1.1.0-rc.1 [skip ci] ([79c44e2](https://github.com/pksorensen/pks-cli/commit/79c44e206a01d9487085f4ce30ac696684c1aff5))
* **release:** 1.1.0-rc.2 [skip ci] ([906c3f6](https://github.com/pksorensen/pks-cli/commit/906c3f6bb1c9daf820aeee665a0c9c724f48b788))
* **release:** 1.1.0-rc.3 [skip ci] ([5580a6a](https://github.com/pksorensen/pks-cli/commit/5580a6aee6db5d1260280f396fcd658e9e1c1be5))
* **release:** 1.1.0-rc.4 [skip ci] ([80af657](https://github.com/pksorensen/pks-cli/commit/80af6573473fa13700eeadf9ef1938d02a214e61))
* **release:** 1.1.0-rc.5 [skip ci] ([973edee](https://github.com/pksorensen/pks-cli/commit/973edee27799ab55004230e6aa788153043721d9))
* **release:** 1.1.0-rc.6 [skip ci] ([014c2dc](https://github.com/pksorensen/pks-cli/commit/014c2dc0a50c1c597b4ff2b9d876f57b2e48eba3))
* **release:** 1.1.0-rc.7 [skip ci] ([54387b6](https://github.com/pksorensen/pks-cli/commit/54387b665d15c90013604cd7ee066d0945cd55e4))
* **release:** 1.2.0-rc.1 [skip ci] ([a61d044](https://github.com/pksorensen/pks-cli/commit/a61d044631666a8489e2007cb03ab9a0e100308e))
* **release:** 1.2.0-rc.10 [skip ci] ([62fb730](https://github.com/pksorensen/pks-cli/commit/62fb73005cece0176c8a0e3bb1030d3f5c0617de))
* **release:** 1.2.0-rc.2 [skip ci] ([f4fdb6b](https://github.com/pksorensen/pks-cli/commit/f4fdb6baa1625192ae38649d93207cf39c62a4e3))
* **release:** 1.2.0-rc.3 [skip ci] ([b166766](https://github.com/pksorensen/pks-cli/commit/b166766212972368032b964435300fb788614289))
* **release:** 1.2.0-rc.4 [skip ci] ([d19af0d](https://github.com/pksorensen/pks-cli/commit/d19af0de1c37a5fd0c8538dee749cc383fc0371a))
* **release:** 1.2.0-rc.5 [skip ci] ([ff3e7dc](https://github.com/pksorensen/pks-cli/commit/ff3e7dcb52b1cd50cfea635066acf0ef7a5838f9))
* **release:** 1.2.0-rc.6 [skip ci] ([5bd2e8c](https://github.com/pksorensen/pks-cli/commit/5bd2e8c794c3b1a71ecf62162905a3ab1244d01f))
* **release:** 1.2.0-rc.7 [skip ci] ([977c8e8](https://github.com/pksorensen/pks-cli/commit/977c8e8ed46aec00527e5934a2cdd0767b53dc4a))
* **release:** 1.2.0-rc.8 [skip ci] ([5a44053](https://github.com/pksorensen/pks-cli/commit/5a440530269ec198347a4fcf579deb5d6cf1f071))
* **release:** 1.2.0-rc.9 [skip ci] ([5acad92](https://github.com/pksorensen/pks-cli/commit/5acad920fb40ff27ccfb7f223952ab3e8d324433))
* update commands and services for compatibility ([c1a419a](https://github.com/pksorensen/pks-cli/commit/c1a419acae91be4c8ef555fd08a3d5241fd35d9c))
* update gitignore for Claude Code configurations ([2fb3ed5](https://github.com/pksorensen/pks-cli/commit/2fb3ed5fe5401066dab9420135fb028b59d315f1))


### ♻️ Code Refactoring

* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))


### ✅ Tests

* add comprehensive devcontainer test suite ([ebc3f1f](https://github.com/pksorensen/pks-cli/commit/ebc3f1fda554d0dd9647997b751aedcd1b4b116e))
* add comprehensive PRD command test suite ([e792ab2](https://github.com/pksorensen/pks-cli/commit/e792ab22174ef5b4a95c13750c66e441e7f97f9c))
* add comprehensive test coverage for new features ([9ced585](https://github.com/pksorensen/pks-cli/commit/9ced5854876ad69196be2f055dcde7e9e0d6496e))
* add comprehensive test suite and documentation for report command ([bc23307](https://github.com/pksorensen/pks-cli/commit/bc233076afcdff6f39d841ffc3fb0922e4f5f929))
* add integration tests for devcontainer templates ([75d7f32](https://github.com/pksorensen/pks-cli/commit/75d7f32ead0db90d2a944cdca3e81e71262543c7))
* enhance service mocking and test reliability ([541aa5c](https://github.com/pksorensen/pks-cli/commit/541aa5cea8895eb0ebc887aa6ad23b0aa268d8da))
* fix GitHub Copilot review comments ([c0bf743](https://github.com/pksorensen/pks-cli/commit/c0bf7433cefa6e9b6cbe3003caa28ca8a3cbd05e)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* fix hooks service test issues ([d9ade66](https://github.com/pksorensen/pks-cli/commit/d9ade6696904b3c1827149d5ba951ba87151c62b))
* update test suite for new architecture ([c4b0510](https://github.com/pksorensen/pks-cli/commit/c4b05107bea24f2249a99b74f7a8eefe84825dba))


### 📦 Build System

* reorganize solution structure ([58bda0d](https://github.com/pksorensen/pks-cli/commit/58bda0d964e87b838eeeb1ccce9d08e1e2dca589))


### 👷 CI/CD

* add GitHub Actions workflow for automated testing ([274bd80](https://github.com/pksorensen/pks-cli/commit/274bd80862a9dd88160e6a99a680c714a92a0c33))
* fix CI build by excluding nullable warnings from warnings-as-errors ([a16cae9](https://github.com/pksorensen/pks-cli/commit/a16cae927acea4e7d2744a5a2fc456f429a087b5))
* fix GitHub Actions workflows for PR validation ([2d5b24c](https://github.com/pksorensen/pks-cli/commit/2d5b24cfd908741cc12e9c98e7e5f3f603f88b00)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* streamline GitHub workflows for PR success ([a923ed2](https://github.com/pksorensen/pks-cli/commit/a923ed20e01a7cd2732724c79ad726a1dd7fb194))
* update test workflow to use core stable tests only ([1797fc5](https://github.com/pksorensen/pks-cli/commit/1797fc5aad53aaf17865806dfbcf1b85d115fb23))

## 1.0.0-rc.1 (2025-12-26)


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


### 🚀 Features

* add --prerelease flag to init command for preview template packages ([44dbcca](https://github.com/pksorensen/pks-cli/commit/44dbcca15133d25f4a8b356b66aa1ae31e637209))
* add .NET 10 preview support to devcontainer ([3477b12](https://github.com/pksorensen/pks-cli/commit/3477b125fbdac1015ecaf6d6afa905e1c24758e9))
* add commit-analyzer and release-notes-generator to semantic-release installation ([32a591d](https://github.com/pksorensen/pks-cli/commit/32a591d9b8b4d6a080670865e18f3a47f84ed8db))
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
* add semantic release configuration for multiple templates ([2389ff8](https://github.com/pksorensen/pks-cli/commit/2389ff8d2cf19b4740f8366f407b38fab240ec2e))
* add slash command generation and swarm orchestration documentation ([a6401d6](https://github.com/pksorensen/pks-cli/commit/a6401d6c9966b1d53dd244d147afc05883b8afea))
* enable vnext branch for prerelease versioning ([b0ea3ee](https://github.com/pksorensen/pks-cli/commit/b0ea3ee4d983191ea6d103b22fe8dbe765408583))
* Enhance command execution with working directory support and Windows compatibility ([1634ea8](https://github.com/pksorensen/pks-cli/commit/1634ea896c5b185cba9fa3bf69428e295c3b09d0))
* Enhance devcontainer CLI detection with multiple approaches ([ae80a9d](https://github.com/pksorensen/pks-cli/commit/ae80a9d11f5f37db7bd10d6abc9a83a248104d42))
* enhance devcontainer commands with improved configuration display ([b34e08d](https://github.com/pksorensen/pks-cli/commit/b34e08d1b30859647109ead0a9d4d4b29e38e3dd))
* enhance HooksCommand and HooksService to support settings scope for Claude Code hooks ([a6c9906](https://github.com/pksorensen/pks-cli/commit/a6c9906f9f53a97d0c8268a1489629a05de3ada3))
* enhance NuGet template discovery with short name extraction and update related tests ([eb37b3b](https://github.com/pksorensen/pks-cli/commit/eb37b3b2ecc3e65a2ffc57c3420a922f22b856c5))
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
* integrate all systems with enhanced initializers and service registration ([3537e38](https://github.com/pksorensen/pks-cli/commit/3537e38c2903bd596bc29998888f7c5f69ebb997))
* integrate devcontainer with init command ([f9a0a06](https://github.com/pksorensen/pks-cli/commit/f9a0a061213f02e871d4d3ea79b80ca2a4a4e855))
* **mcp:** add comprehensive MCP tool service suite ([b97a98d](https://github.com/pksorensen/pks-cli/commit/b97a98dbcc561f4eb85367f661bd90adf1bf3d0a))
* **mcp:** enhance SDK-based service architecture ([68f6c78](https://github.com/pksorensen/pks-cli/commit/68f6c788d50b622294e30be984df01e1d829fba0))
* **mcp:** migrate to SDK-based hosting service with logging fixes ([dab3947](https://github.com/pksorensen/pks-cli/commit/dab39475bcf790140c85c960d1dd611c5f2c12bf))
* modernize infrastructure services and configuration ([7d86052](https://github.com/pksorensen/pks-cli/commit/7d86052e2b4f60f710fb4e926d941b7fe93f6a56))
* **prd:** modernize command implementations and service layer ([715e6f9](https://github.com/pksorensen/pks-cli/commit/715e6f95e3ce5179102735c299635b0aa830975c))
* refactored things ([a068996](https://github.com/pksorensen/pks-cli/commit/a068996ee53e95eaad61576ecf0d97dc1cd9d050))
* register devcontainer services and commands ([dfe0710](https://github.com/pksorensen/pks-cli/commit/dfe0710c924ed3cb7120e77aad645b31c71f1f06))
* **release:** implement semantic release with pre-release strategy ([#7](https://github.com/pksorensen/pks-cli/issues/7)) ([479d530](https://github.com/pksorensen/pks-cli/commit/479d53090dd081103f486272370284152255885f)), closes [#6](https://github.com/pksorensen/pks-cli/issues/6)
* trigger semantic-release on vnext branch ([d715849](https://github.com/pksorensen/pks-cli/commit/d715849979fbaf0885ac76aafa9641665d29471f))
* update devcontainer configuration and add .env to .gitignore ([a3d71ed](https://github.com/pksorensen/pks-cli/commit/a3d71ed6c1e37a2830a43cd4d250569e604d1d00))
* upgrade to .NET 10 and update related configurations ([179435b](https://github.com/pksorensen/pks-cli/commit/179435b888d38cc9aac7a53011503280e8faacf1))


### 🐛 Bug Fixes

* add .env template files to devcontainer templates and update .gitignore ([1e8b5f1](https://github.com/pksorensen/pks-cli/commit/1e8b5f132eaa33a6441fbb5e5576312088275fa8))
* add package.json to semantic-release workflow path triggers ([b003baa](https://github.com/pksorensen/pks-cli/commit/b003baae5a7ac41b700bb71e9ff5732bc9fd61d8))
* add working-directory to CI workflow steps ([e965c12](https://github.com/pksorensen/pks-cli/commit/e965c12ccfe0497fddbc6042b81b295f6b04147a))
* added updated devcontainer ([ca13552](https://github.com/pksorensen/pks-cli/commit/ca135528d7f7f8af28f5d3284f9710a0b1e119c4))
* apply code formatting to resolve CI/CD issues ([46cb76f](https://github.com/pksorensen/pks-cli/commit/46cb76fdd0185085431b1d94f5f4e04037807d10))
* apply code formatting to resolve CI/CD issues ([9e29103](https://github.com/pksorensen/pks-cli/commit/9e29103315a30a5c95bc69af2086f83e4d0df090))
* apply dotnet format to resolve code formatting validation ([11bc1d1](https://github.com/pksorensen/pks-cli/commit/11bc1d1afa7ade1212189e068a908fd08d43dd67))
* **ci:** create packages with correct pre-release versions ([90af0c4](https://github.com/pksorensen/pks-cli/commit/90af0c4efd25a6993b2f27b1476c39521a9c5fb7))
* **ci:** fix semantic release workflow to build and publish .NET packages ([7df183b](https://github.com/pksorensen/pks-cli/commit/7df183b1d691990d8321a3f1881224488de11bbb))
* **ci:** move semantic release files to repository root ([0f9b7b7](https://github.com/pksorensen/pks-cli/commit/0f9b7b77060b18a60ab40868290a840a534df01e))
* **ci:** skip tests and build only CLI and template packages ([d7a60f4](https://github.com/pksorensen/pks-cli/commit/d7a60f4d7323b9f8e2dfa9163558556a74d2bb37))
* comprehensive Claude Code hooks integration ([4066220](https://github.com/pksorensen/pks-cli/commit/40662202e574c8383d16942c1a4505289dbf02a2)), closes [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16)
* Correct resource path for embedded Dockerfile in DevcontainerSpawnerService ([69fac98](https://github.com/pksorensen/pks-cli/commit/69fac987c9eb782c89225fad6a5d97a3fe276ba2))
* Correct version parsing in template discovery to use NuGetVersion ([6ce4e17](https://github.com/pksorensen/pks-cli/commit/6ce4e17848abf0cff71fa03443f8eefc5e689d7b))
* enhance command error handling and initializer stability ([ce8e8d0](https://github.com/pksorensen/pks-cli/commit/ce8e8d0ca0de504cf77dc7dfe7f4622b524781df))
* ensure correct path for semantic-release config file in release workflows ([3ccec4a](https://github.com/pksorensen/pks-cli/commit/3ccec4a5d04c52232bd36acbf5664fdf613848e6))
* Escape markup in error and warning messages for better display ([9e75df1](https://github.com/pksorensen/pks-cli/commit/9e75df1871d2f6427ae45a724f1a9b985e468844))
* explicitly include .env and .gitignore files in template packages ([2f265ef](https://github.com/pksorensen/pks-cli/commit/2f265ef5f7d33eac161d4d698a5f90651774836d))
* Include .env template files in all template packages ([d793177](https://github.com/pksorensen/pks-cli/commit/d793177003cee03ad4d79dd38e9c134069647b21))
* include .releaserc.json in semantic-release workflow paths ([213de15](https://github.com/pksorensen/pks-cli/commit/213de15333cb63a7f944872343b06a550e02a35d))
* install semantic-release plugins locally instead of globally ([d7160f1](https://github.com/pksorensen/pks-cli/commit/d7160f1b58a21ddf7fded21052c90f376748d2ba))
* move GitHub workflows to repository root and update working directories ([d58194b](https://github.com/pksorensen/pks-cli/commit/d58194b21ee2e084ce3fbe9fda5bf612533029bb))
* normalize line endings for cross-platform compatibility ([77bcec3](https://github.com/pksorensen/pks-cli/commit/77bcec3a9e2f7aea20d8e1ae0e6144dd5dda9d9b))
* refine template change detection logic in semantic release workflow ([33b0c81](https://github.com/pksorensen/pks-cli/commit/33b0c810583d00151e3cb78328abc30632d8e876))
* remove duplicate semantic-release branch configuration from package.json ([de18705](https://github.com/pksorensen/pks-cli/commit/de18705ffe967b8cadecda91c27f4b03fa024885))
* remove path filters from semantic-release workflow to trigger on all commits ([55d4031](https://github.com/pksorensen/pks-cli/commit/55d40310d6aa19b7783c2a2e1b7c6858e833a527))
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
* stabilize integration test infrastructure ([39c2838](https://github.com/pksorensen/pks-cli/commit/39c2838727c9c1f36eb9075431e1bda5f2ddaa98))
* streamline .NET workflow by removing unnecessary working-directory specifications ([ce1194e](https://github.com/pksorensen/pks-cli/commit/ce1194e21c8fc81376d24dd0af2ffd3479277be8))
* update InitCommand tests to match new NuGet template discovery implementation ([05a412e](https://github.com/pksorensen/pks-cli/commit/05a412e65e83c05b7b6c60e359585016b56e7a42))
* update prerelease setting for release branches to true ([74f0588](https://github.com/pksorensen/pks-cli/commit/74f0588d5708eb2d364104d8bad7f8d625ad9167))
* update semantic-release configuration to include vnext and develop branches ([3c8325a](https://github.com/pksorensen/pks-cli/commit/3c8325a4a315f8ef988b0c3edc884a4942d9ba8e))
* update semantic-release configuration to remove deprecated .releaserc.json file and adjust paths ([985b05d](https://github.com/pksorensen/pks-cli/commit/985b05dea4e1586d57cc1c7e1b22771ba5a0f41e))


### ⚡ Performance Improvements

* optimize test execution configuration for CI/CD ([0b5fd31](https://github.com/pksorensen/pks-cli/commit/0b5fd31901ae37357ecf127258bb7b6f348d55f6))


### 📚 Documentation

* add comprehensive project documentation and roadmap ([8ccabc1](https://github.com/pksorensen/pks-cli/commit/8ccabc1aec4c66a21a2873f50ff793285e002a30))
* update changelog with bug fixes from agent swarm ([6641743](https://github.com/pksorensen/pks-cli/commit/6641743a300972f025b6a296195149bd326659fe))
* update documentation for new template system ([f12c6ff](https://github.com/pksorensen/pks-cli/commit/f12c6ff030923775f1d185ca6766c399bc55b083))
* update project configuration and documentation ([4624b35](https://github.com/pksorensen/pks-cli/commit/4624b35c0b9ab701258dcd2a8b91b00778222ab5))
* update project objectives and branding considerations ([3f3c0d7](https://github.com/pksorensen/pks-cli/commit/3f3c0d7897ddb320577faf003789f80de718ccd6))
* update README and add comprehensive project documentation ([1ca9148](https://github.com/pksorensen/pks-cli/commit/1ca9148660c7d7be0ee6f856724a9613f57f30fb))
* update README with test suite improvements and CI/CD status ([eb7d9f9](https://github.com/pksorensen/pks-cli/commit/eb7d9f916fdeb32d85c006e6dd86dbc0728fa41a))


### 🔧 Chores

* add comprehensive .NET gitignore patterns ([83146cf](https://github.com/pksorensen/pks-cli/commit/83146cf9271732292a9a73f962f1356a44832c2d))
* clean up obsolete files ([e777c49](https://github.com/pksorensen/pks-cli/commit/e777c49981e7068e49692c39393e60a639b91c9d))
* **release:** 1.0.0 [skip ci] ([97906a2](https://github.com/pksorensen/pks-cli/commit/97906a2a29f0b2f337f73f687042c58373384f75)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.1 [skip ci] ([d518dc8](https://github.com/pksorensen/pks-cli/commit/d518dc8ec840b3745ed7b96975b0563a927b6b97)), closes [#4](https://github.com/pksorensen/pks-cli/issues/4) [#1](https://github.com/pksorensen/pks-cli/issues/1) [#3](https://github.com/pksorensen/pks-cli/issues/3) [#2](https://github.com/pksorensen/pks-cli/issues/2) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#4](https://github.com/pksorensen/pks-cli/issues/4) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#5](https://github.com/pksorensen/pks-cli/issues/5) [#6](https://github.com/pksorensen/pks-cli/issues/6) [#15](https://github.com/pksorensen/pks-cli/issues/15) [#14](https://github.com/pksorensen/pks-cli/issues/14) [#11](https://github.com/pksorensen/pks-cli/issues/11) [#12](https://github.com/pksorensen/pks-cli/issues/12) [#13](https://github.com/pksorensen/pks-cli/issues/13) [#16](https://github.com/pksorensen/pks-cli/issues/16) [#18](https://github.com/pksorensen/pks-cli/issues/18) [#18](https://github.com/pksorensen/pks-cli/issues/18)
* **release:** 1.0.0-rc.2 [skip ci] ([d56c908](https://github.com/pksorensen/pks-cli/commit/d56c908c5e3838e9819968f8a2538db2d2be2fdd))
* **release:** 1.0.1 [skip ci] ([18b246f](https://github.com/pksorensen/pks-cli/commit/18b246f5e5b3da6b7194847d014ae2aefedea3c9))
* **release:** 1.0.1-rc.1 [skip ci] ([baaeb81](https://github.com/pksorensen/pks-cli/commit/baaeb81dd6751704339144d17379e4880ccd16fb))
* **release:** 1.1.0 [skip ci] ([4174048](https://github.com/pksorensen/pks-cli/commit/417404843ac5446b9e9fdbb4c6bbf32720d5adcb))
* **release:** 1.1.0-rc.1 [skip ci] ([79c44e2](https://github.com/pksorensen/pks-cli/commit/79c44e206a01d9487085f4ce30ac696684c1aff5))
* **release:** 1.1.0-rc.2 [skip ci] ([906c3f6](https://github.com/pksorensen/pks-cli/commit/906c3f6bb1c9daf820aeee665a0c9c724f48b788))
* **release:** 1.1.0-rc.3 [skip ci] ([5580a6a](https://github.com/pksorensen/pks-cli/commit/5580a6aee6db5d1260280f396fcd658e9e1c1be5))
* **release:** 1.1.0-rc.4 [skip ci] ([80af657](https://github.com/pksorensen/pks-cli/commit/80af6573473fa13700eeadf9ef1938d02a214e61))
* **release:** 1.1.0-rc.5 [skip ci] ([973edee](https://github.com/pksorensen/pks-cli/commit/973edee27799ab55004230e6aa788153043721d9))
* **release:** 1.1.0-rc.6 [skip ci] ([014c2dc](https://github.com/pksorensen/pks-cli/commit/014c2dc0a50c1c597b4ff2b9d876f57b2e48eba3))
* **release:** 1.1.0-rc.7 [skip ci] ([54387b6](https://github.com/pksorensen/pks-cli/commit/54387b665d15c90013604cd7ee066d0945cd55e4))
* **release:** 1.2.0-rc.1 [skip ci] ([a61d044](https://github.com/pksorensen/pks-cli/commit/a61d044631666a8489e2007cb03ab9a0e100308e))
* **release:** 1.2.0-rc.10 [skip ci] ([62fb730](https://github.com/pksorensen/pks-cli/commit/62fb73005cece0176c8a0e3bb1030d3f5c0617de))
* **release:** 1.2.0-rc.2 [skip ci] ([f4fdb6b](https://github.com/pksorensen/pks-cli/commit/f4fdb6baa1625192ae38649d93207cf39c62a4e3))
* **release:** 1.2.0-rc.3 [skip ci] ([b166766](https://github.com/pksorensen/pks-cli/commit/b166766212972368032b964435300fb788614289))
* **release:** 1.2.0-rc.4 [skip ci] ([d19af0d](https://github.com/pksorensen/pks-cli/commit/d19af0de1c37a5fd0c8538dee749cc383fc0371a))
* **release:** 1.2.0-rc.5 [skip ci] ([ff3e7dc](https://github.com/pksorensen/pks-cli/commit/ff3e7dcb52b1cd50cfea635066acf0ef7a5838f9))
* **release:** 1.2.0-rc.6 [skip ci] ([5bd2e8c](https://github.com/pksorensen/pks-cli/commit/5bd2e8c794c3b1a71ecf62162905a3ab1244d01f))
* **release:** 1.2.0-rc.7 [skip ci] ([977c8e8](https://github.com/pksorensen/pks-cli/commit/977c8e8ed46aec00527e5934a2cdd0767b53dc4a))
* **release:** 1.2.0-rc.8 [skip ci] ([5a44053](https://github.com/pksorensen/pks-cli/commit/5a440530269ec198347a4fcf579deb5d6cf1f071))
* **release:** 1.2.0-rc.9 [skip ci] ([5acad92](https://github.com/pksorensen/pks-cli/commit/5acad920fb40ff27ccfb7f223952ab3e8d324433))
* update commands and services for compatibility ([c1a419a](https://github.com/pksorensen/pks-cli/commit/c1a419acae91be4c8ef555fd08a3d5241fd35d9c))
* update gitignore for Claude Code configurations ([2fb3ed5](https://github.com/pksorensen/pks-cli/commit/2fb3ed5fe5401066dab9420135fb028b59d315f1))


### ♻️ Code Refactoring

* reorganize Claude agents and commands into structured directories ([7214b18](https://github.com/pksorensen/pks-cli/commit/7214b18b2067c99080497dad6db0f205d85c3098))
* reorganize templates to dedicated root directory ([347b9b3](https://github.com/pksorensen/pks-cli/commit/347b9b33fbe43e969727eabb6f33c6fae6d30fe2))
* restructure PRD command architecture ([a6ddc48](https://github.com/pksorensen/pks-cli/commit/a6ddc48f1f476d153660c1204f93505c23c458a9))
* simplify Claude Code hooks integration architecture ([5117de5](https://github.com/pksorensen/pks-cli/commit/5117de5e386ab778f9ed411f25dd9150b3d36268))
* update initializers for new template system ([d2a4330](https://github.com/pksorensen/pks-cli/commit/d2a4330a771d795be6481de858f7b72ea049bd58))


### ✅ Tests

* add comprehensive devcontainer test suite ([ebc3f1f](https://github.com/pksorensen/pks-cli/commit/ebc3f1fda554d0dd9647997b751aedcd1b4b116e))
* add comprehensive PRD command test suite ([e792ab2](https://github.com/pksorensen/pks-cli/commit/e792ab22174ef5b4a95c13750c66e441e7f97f9c))
* add comprehensive test coverage for new features ([9ced585](https://github.com/pksorensen/pks-cli/commit/9ced5854876ad69196be2f055dcde7e9e0d6496e))
* add comprehensive test suite and documentation for report command ([bc23307](https://github.com/pksorensen/pks-cli/commit/bc233076afcdff6f39d841ffc3fb0922e4f5f929))
* add integration tests for devcontainer templates ([75d7f32](https://github.com/pksorensen/pks-cli/commit/75d7f32ead0db90d2a944cdca3e81e71262543c7))
* enhance service mocking and test reliability ([541aa5c](https://github.com/pksorensen/pks-cli/commit/541aa5cea8895eb0ebc887aa6ad23b0aa268d8da))
* fix GitHub Copilot review comments ([c0bf743](https://github.com/pksorensen/pks-cli/commit/c0bf7433cefa6e9b6cbe3003caa28ca8a3cbd05e)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* fix hooks service test issues ([d9ade66](https://github.com/pksorensen/pks-cli/commit/d9ade6696904b3c1827149d5ba951ba87151c62b))
* update test suite for new architecture ([c4b0510](https://github.com/pksorensen/pks-cli/commit/c4b05107bea24f2249a99b74f7a8eefe84825dba))


### 📦 Build System

* reorganize solution structure ([58bda0d](https://github.com/pksorensen/pks-cli/commit/58bda0d964e87b838eeeb1ccce9d08e1e2dca589))


### 👷 CI/CD

* add GitHub Actions workflow for automated testing ([274bd80](https://github.com/pksorensen/pks-cli/commit/274bd80862a9dd88160e6a99a680c714a92a0c33))
* fix CI build by excluding nullable warnings from warnings-as-errors ([a16cae9](https://github.com/pksorensen/pks-cli/commit/a16cae927acea4e7d2744a5a2fc456f429a087b5))
* fix GitHub Actions workflows for PR validation ([2d5b24c](https://github.com/pksorensen/pks-cli/commit/2d5b24cfd908741cc12e9c98e7e5f3f603f88b00)), closes [#18](https://github.com/pksorensen/pks-cli/issues/18)
* streamline GitHub workflows for PR success ([a923ed2](https://github.com/pksorensen/pks-cli/commit/a923ed20e01a7cd2732724c79ad726a1dd7fb194))
* update test workflow to use core stable tests only ([1797fc5](https://github.com/pksorensen/pks-cli/commit/1797fc5aad53aaf17865806dfbcf1b85d115fb23))

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
