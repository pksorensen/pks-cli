---
title: "Install pks"
description: "Install the pks CLI as a .NET global tool or an npm-wrapped self-contained binary on Linux, macOS, or Windows, then verify, pin, update, and remove it."
tags: [how-to, install, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "dotnet tool install -g pks-cli"
---

Get `pks` on your machine in a few minutes: pick an install channel, run one command, and confirm the binary reports a version. The same commands work on Linux, macOS, and Windows — the only difference between the two published channels is what they need already installed.

`pks` is a .NET 10 application published to nuget.org as the `pks-cli` package and to npm as `@pks-cli/cli`. For the command surface itself, see the [pks overview](/tools/pks).

## 1. Prerequisites

Pick one row. You do not need both.

- **.NET 10 SDK** — required for the `dotnet tool` channel. `pks` targets `net10.0`, so an older SDK cannot run it.
- **Node.js 18 or newer** — required for the npm channel. That channel ships a self-contained binary, so no .NET install is needed.

Supported platform packages on the npm channel are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, and `win-arm64`. Other platform and architecture combinations are not published.

## 2. Install pks

### Option A — .NET global tool (recommended)

```bash
dotnet tool install -g pks-cli
```

```powershell
dotnet tool install -g pks-cli
```

The tool is packed with `PackAsTool`, so the installed command name is `pks`, not `pks-cli`. The .NET SDK places the shim in `~/.dotnet/tools` on Linux and macOS, and in `%USERPROFILE%\.dotnet\tools` on Windows.

### Option B — npm wrapper

```bash
npm install -g @pks-cli/cli
```

```powershell
npm install -g @pks-cli/cli
```

The `@pks-cli/cli` package contains only a Node launcher. The actual binary arrives through an optional dependency chosen for your platform — `@pks-cli/cli-linux-x64`, `@pks-cli/cli-osx-arm64`, `@pks-cli/cli-win-x64`, and so on. A postinstall check prints `PKS CLI installed successfully` when the right platform package landed.

### Option C — build from source

```bash
git clone https://github.com/pksorensen/pks-cli.git
cd pks-cli
dotnet build pks-cli.sln
```

Run the result without installing it:

```bash
cd src
dotnet run -- status
```

A source build reports its install method as unknown, so self-update is unavailable — rebuild to pick up changes.

To produce self-contained single-file binaries for every supported runtime identifier, use the publish script. It writes one directory per platform:

```bash
./scripts/publish-self-contained.sh 6.20.1 ./npm-dist
```

> **Note.** `build-local.sh` is a different script. It cross-compiles the embedded Go companions (`vibecast`, `heypoul`) before publishing a Windows binary, so it additionally needs Go 1.24 or newer on `PATH`.

## 3. Verify the install

```bash
pks --version
```

The version string is read from the assembly's informational version and printed on its own line — for a current stable install, `6.20.1`.

Confirm the shim resolves to the channel you expect:

```bash
command -v pks
```

```powershell
Get-Command pks
```

A `dotnet tool` install resolves under `.dotnet/tools`; an npm install resolves under your global npm prefix and points at a launcher inside `node_modules/@pks-cli/`.

## 4. Pin or check a version

Both channels accept an explicit version, which is the supported way to hold a machine on a known build.

```bash
dotnet tool install -g pks-cli --version 6.20.1
```

```bash
npm install -g @pks-cli/cli@6.20.1
```

To read what is installed rather than what is published:

```bash
dotnet tool list -g
```

```bash
npm list -g @pks-cli/cli
```

Prereleases are published to nuget.org from `main` alongside stable releases. Install one by asking for it explicitly:

```bash
dotnet tool install -g pks-cli --prerelease
```

## 5. Update

`pks update` compares the running binary against the latest `pks-cli` version on nuget.org, shows a current-to-latest panel, and asks for confirmation.

```bash
pks update
```

What happens next depends on how `pks` was installed. The command detects that from the running executable's path:

| Install method | Behavior of `pks update` |
| --- | --- |
| .NET global tool | Runs `dotnet tool update -g pks-cli` for you. |
| npm | Prints the command to run: `npm install -g @pks-cli/cli@latest`. |
| Devcontainer-baked binary | Prints the host-side swap script, because the container user cannot write `/usr/local/bin`. |
| Standalone binary | Prints the target version and expects you to replace the file. |
| Source checkout | Reports that the build came from source and stops. |

On first run the command asks which channel to follow — `stable` (released versions) or `daily` (latest prerelease from `main`). The choice persists at the config key `cli.update.channel`, and the daily channel adds `--prerelease` to the underlying `dotnet tool update`.

Replacing the binary is gated by the `pks.update` second factor, since a swapped binary could otherwise disable the guard. The gate is skipped when no factor is enrolled — see [pks authenticator](/tools/pks/authenticator).

Updating without the command works too:

```bash
dotnet tool update -g pks-cli
```

```bash
npm install -g @pks-cli/cli@latest
```

## 6. Uninstall

```bash
dotnet tool uninstall -g pks-cli
```

```bash
npm uninstall -g @pks-cli/cli
```

Uninstalling removes the executable only. Configuration and credentials stay in `~/.pks-cli` on Linux and macOS, and in `%USERPROFILE%\.pks-cli` on Windows. Delete that directory to remove stored tokens, registered SSH targets, certificates, and cached state as well.

## 7. Troubleshoot a failed install

**`pks: command not found` after a `dotnet tool` install.** The SDK's tools directory is not on `PATH`. Add it and reopen the shell:

```bash
export PATH="$HOME/.dotnet/tools:$PATH"
```

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
```

Make the change permanent in your shell profile on Linux and macOS, or through the Windows environment-variable settings.

**The install fails with a framework or target error.** The package targets `net10.0`. Run `dotnet --list-sdks` and confirm a 10.x SDK is present. If only older SDKs are installed, either install .NET 10 or switch to the npm channel, which needs no .NET at all.

**`PKS CLI binary not found for <platform>-<arch>`.** The platform-specific optional dependency did not install — commonly because optional dependencies were skipped, or a proxy blocked the download. Install it directly, or force a reinstall:

```bash
npm install @pks-cli/cli-linux-x64
```

```bash
npm install -g @pks-cli/cli --force
```

**`Unsupported platform`.** The launcher only maps the six published platform packages listed in step 1. No binary exists for anything else; build from source instead.

**`pks` runs but resolves to the wrong copy.** Both channels install a command named `pks`. Having the .NET tool and the npm wrapper on the same machine means `PATH` order decides which one runs, and `pks update` then reports the install method of whichever won. Remove the channel you do not want.

**Nothing prints, or the output is decorated when you expected clean text.** Pass `--no-logo` to suppress the ASCII banner, and `--debug` to set `PKS_DEBUG=1` and get verbose output for the run.

## Next steps

- [pks](/tools/pks) — the full command surface and what each family does
- [pks update](/tools/pks/update) — the update command's channels, flags, and per-install-method behavior
- [pks init](/tools/pks/init) — scaffold a project and create the `.pks` folder
- [pks status](/tools/pks/status) — confirm the CLI sees your project and environment
- [pks authenticator](/tools/pks/authenticator) — enroll the second factor that gates sensitive actions
