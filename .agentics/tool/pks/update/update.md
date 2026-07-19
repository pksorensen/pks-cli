---
title: "pks update"
description: "Check nuget.org for a newer pks-cli release, show a current-to-latest diff, and apply it using whichever mechanism matches how this pks binary was installed."
tags: [reference, cli, update]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks update [--self]"
examples:
  - command: "pks update"
    description: "Check for a newer release and apply it if one exists"
  - command: "pks update --self"
    description: "Same as above; --self mirrors aspire's calling convention"
---

`pks update` is a single leaf command registered directly under the root `pks` command — it has no subcommands. It picks an update channel, checks nuget.org for the latest `pks-cli` package version, shows a current-to-latest diff, asks for confirmation, then applies the update through whichever mechanism matches how this `pks` binary was installed.

## Overview

`pks update` compares the running binary's version against the latest version of the `pks-cli` package on nuget.org and, if a newer one exists, updates in place. What "updates in place" means depends entirely on the install method:

- **Automatic** for a `dotnet tool` install — it shells out to `dotnet tool update -g pks-cli` for you.
- **Guidance only** for every other install method (npm, a devcontainer-baked binary, a standalone binary, or a source checkout) — the command prints the exact command or script to run and stops there.

## When to use it

Run `pks update` periodically to pick up new `pks-cli` releases, or when you're told a newer version is available. Before relying on it, check `pks actions` to see whether the `pks.update` action is set to require a two-factor code, and use `pks authenticator status` (or `pks authenticator init`) to check or enroll the TOTP factor that can gate it.

## Prerequisites

- **Network access to nuget.org** — the version check calls out to the NuGet feed with no offline fallback.
- **A readable `AssemblyInformationalVersion`** on the running binary — this is baked in at build time and is not something you configure.
- Nothing else. `pks update` needs no credentials of its own.

## Synopsis

```text
pks update [options]
```

### Options

| Flag | Type | Default | Description |
|---|---|---|---|
| `--self` | `bool` | `false` | Accepted by the parser but not branched on anywhere in the command body — `pks update` always performs a self-update whether or not `--self` is passed. It exists to mirror `aspire update --self`'s calling convention. |

## Behavior

1. **Load or choose a channel.** Reads the persisted preference at config key `cli.update.channel`. If none is set, prompts interactively for `stable` (released nuget.org versions) or `daily` (the latest `X.Y.Z-preview.N` published on every push to main) and persists the choice.
2. **Query nuget.org.** Fetches the latest `pks-cli` version for the chosen channel (`includePrerelease` is only set for `daily`). A feed failure prints a red error and exits `1` — there is no cached or offline fallback.
3. **Compare versions.** Compares the fetched version against the running `AssemblyInformationalVersion` (build metadata after `+` stripped) using NuGet semver comparison. If the candidate is not strictly newer, prints "Already up to date" and exits `0`.
4. **Show the diff and confirm.** Renders a cyan-bordered panel with Current → Latest and the active channel, then asks `Update to {latest}?` (default: yes). Answering no exits `0` with nothing changed.
5. **Gate the write.** The actual binary replacement is gated by the `pks.update` two-factor action (see [Auth model](#auth-model) below). A denial prints a red message and exits `1`.
6. **Detect install method and dispatch.** Uses `Environment.ProcessPath` to classify how this `pks` was installed, then runs the matching update path — see [Install-method dispatch](#install-method-dispatch).

Answering no to the confirm prompt, a feed failure, and an already-up-to-date result all return before the two-factor gate is even reached — the gate only fires when a newer version genuinely exists and you've confirmed applying it.

## Install-method dispatch

Detection (`IInstallMethodDetector.Detect()`) is purely path-based against `Environment.ProcessPath`, checked in this order:

| Detected path pattern | Install method | What `pks update` then does |
|---|---|---|
| Under `/.dotnet/tools/` or `/.store/pks-cli/` | `DotnetTool` | Runs `dotnet tool update -g pks-cli` (adds `--prerelease` on the `daily` channel) via `Process.Start`, streams the exit code, and prints success or failure. This is the only path that actually performs the update for you. |
| Under `/node_modules/@pks-cli/` | `Npm` | Prints guidance to run `npm install -g @pks-cli/cli@latest` yourself. Does not run it. |
| Under `/usr/local/`, `/usr/bin/`, or `/opt/`, **not** writable by the current process owner | `Baked` | Prints a yellow panel telling the operator to run `./scripts/host/pks-devswap.sh release <container>` or `./scripts/host/pks-devswap.sh workspace <container>` from the Docker host, then returns `0`. Nothing is updated by the command itself. |
| Under `/usr/local/`, `/usr/bin/`, or `/opt/`, writable by the current process owner; or any other writable path | `StandaloneBinary` | Prints guidance to download the new version and replace the binary yourself. Does not download or replace anything. |
| Process filename is literally `dotnet` (i.e. invoked via `dotnet run` / `dotnet pks.dll`), or an unrecognized layout | `Unknown` | Prints "This pks was run from source... rebuild from the repo to pick up changes." |

Because detection is filesystem-path-based, running `pks` from an unusual location can misclassify the install method and print the wrong guidance panel.

## Auth model

`pks update` reads nuget.org, which needs no credentials. The sensitive step — actually replacing the binary — is gated by pks-cli's own local two-factor action guard, under action id `pks.update` ("Update pks" / "Replace or self-update the pks binary", category "Control plane", default-required in the catalog).

That gate is opt-in and silent until you turn it on:

- **No TOTP authenticator enrolled:** `RequireAsync` auto-satisfies and the update proceeds ungated — this is trust-on-first-use, so existing users see no behavior change.
- **An authenticator is enrolled and `pks.update` is set to require it:** `pks update` renders an approval panel and requires a TOTP code before the binary write proceeds.

`pks authenticator init` is deliberately kept off any agent- or sudo-automatable path, so an autonomous agent cannot both enable and then satisfy this gate.

## Examples

```bash
pks update
```

Checks the current channel, shows the diff panel if a newer version exists, and asks to confirm before applying.

```bash
pks update --self
```

Identical in practice to `pks update` — `--self` does not change behavior, but this is the invocation shown in the CLI's own registered example.

## Troubleshooting

### It hung with no visible progress

The `dotnet tool update -g pks-cli` step runs synchronously and blocks on `proc.WaitForExit()` with no timeout. If `dotnet` itself stalls — commonly a NuGet network stall — `pks update` hangs too, and the spinner is only shown during the earlier version-check step, not during this one. Wait it out or interrupt and retry once connectivity is confirmed.

### "Already up to date" when I know a new version shipped

Two independent causes produce this message:

- You're on a channel that doesn't include the version you're expecting — `stable` never sees `daily` preview builds. Check `cli.update.channel`.
- The fetched candidate version string failed to parse as a `NuGetVersion`. A parse failure is treated as "not newer," silently — there is no separate error for it.

### It updated the channel choice but not the binary

Only `InstallMethod.DotnetTool` performs a real automatic update. Every other install method — `Npm`, `Baked`, `StandaloneBinary`, `Unknown` — only prints instructions and leaves the running binary untouched. Re-read the panel it printed; it names the exact command or script to run next.

### I'm in a devcontainer and it told me to run a script on the host

This is expected for `InstallMethod.Baked`. A `pks` baked into a devcontainer image runs as a non-root `pks` user that cannot write `/usr/local/bin/pks`, so the update has to happen from outside the container. Run `./scripts/host/pks-devswap.sh release <container>` (or `workspace <container>`) from the Docker host, not from inside the container.

### I never see a two-factor prompt

No TOTP authenticator is enrolled, or `pks.update` isn't marked required in your action policy. Run `pks authenticator init` to enroll one, then check its requirement with `pks actions`.

## See also

- [pks](/tools/pks) — command families and the full command surface.
- [pks authenticator](/tools/pks/authenticator) — enroll or check the TOTP factor that can gate `pks update`.
- [pks actions](/tools/pks/actions) — choose whether `pks.update` requires a code.
