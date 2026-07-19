---
title: "pks model"
description: "Downloads, installs, and manages the local on-device AI models that pks voice and pks transcribe use for offline speech-to-text work."
tags: [reference, cli, model, offline]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks model list | pks model <name> {init|status|update|remove}"
examples:
  - command: "pks model list"
    description: "List every known model and its install status"
  - command: "pks model parakeet-v3 init"
    description: "Download and install the Parakeet speech-to-text model"
  - command: "pks model parakeet-v3 status"
    description: "Show install path, version, and size"
  - command: "pks model parakeet-v3 update"
    description: "Re-install if a newer catalog version exists"
  - command: "pks model parakeet-v3 remove"
    description: "Delete the installed model and free disk space"
---

`pks model` manages the local, on-device AI models that other `pks` commands can use for offline work — currently the Parakeet TDT speech-to-text model consumed by `pks voice` and the local-engine option of `pks transcribe`. Models are downloaded from GitHub Releases as archives, extracted, and tracked in a JSON manifest at `~/.pks-cli/models.json`, with files installed under `~/.pks-cli/models/<name>/`.

The command tree is generated at startup from a hardcoded catalog (`ModelCatalog.Known`) — each catalog entry gets its own `pks model <name> {init,status,update,remove}` branch with no additional code required. As of this writing the catalog has exactly one entry, `parakeet-v3` (Parakeet TDT 0.6B v3, multilingual, 27 languages including `da-DK` and `en-US`, catalog version `2025-09-01`).

## When to use it

Run `pks model list` before `pks voice start` or `pks transcribe --engines cloud,parakeet-v3` to check whether the local model those commands need is installed. Use the per-model verbs to manage its lifecycle on disk afterward. You don't need this group for the default cloud-only transcription path (Azure AI Foundry Speech), which requires no local model.

## Prerequisites

- **Network access to `github.com`** — `init` and `update` download the release archive over HTTPS from a public GitHub Releases URL.
- **Free disk space under `~/.pks-cli/models/`** — the `parakeet-v3` entry alone is about 640 MiB.
- **No login required** — the `model` group itself needs no Keycloak, Azure, or API-key auth. Downstream consumers such as `pks voice` separately need their own cloud credentials for the cloud engine.

## Synopsis

```text
pks model list | pks model <name> {init|status|update|remove}
```

```text
list           List every catalog model and its local install status
<name> init    Download and install a catalog model
<name> status  Show version, install path, and size of a model
<name> update  Re-install a model if a newer catalog version exists
<name> remove  Delete an installed model
```

`<name>` is not a typed CLI argument — it is a literal subcommand path segment, one per catalog entry. The only valid value today is `parakeet-v3`; the `model` branch itself and every leaf command take no flags.

## list

Prints a table (Name, Status, Version, Size, Capabilities, Languages) of every model in the catalog, cross-referenced against the local install registry. Status is one of `not installed` (dim), `installed` (green, versions match), or `update available` (yellow, installed version differs from the catalog version). This is the discovery entry point — run it before anything else in the group.

`list` takes no flags or arguments.

```bash
pks model list
```

You should see one row per catalog model with its current status.

> **Note.** The Size column shows the actual installed size for installed models but only the catalog's estimated size (for example "~640 MiB") for models not yet installed. The Languages column truncates to the first four entries plus a "+N more" suffix when a model supports more than four languages — `parakeet-v3` supports 27.

## <name> init

Downloads and installs a specific catalog model. It resolves `<name>` against the catalog, prompts for confirmation showing the download size and capabilities, downloads the archive to a staging directory, extracts it, moves the expected files into `~/.pks-cli/models/<name>/`, writes a `manifest.json` there, and registers the install in `~/.pks-cli/models.json`. Staging happens in an adjacent `<installDir>.staging` folder so a failed download or extract never leaves a half-installed model in the real install directory; the staging folder is always cleaned up.

`<name> init` takes no flags.

```bash
pks model parakeet-v3 init
```

Downloads and installs the Parakeet TDT 0.6B v3 multilingual speech-to-text model (about 640 MiB).

> **Note.** The confirmation prompt (`Proceed with download?`, default yes) has no `--yes` or `--force` flag to skip it, so this command can hang when run non-interactively unless input is piped.

If the model is already installed at the current catalog version, this is a no-op that prints the install path and suggests `update` or `remove` — it will not reinstall. The downloaded archive must contain at least one top-level directory — extraction aborts with exit code 1 only if there are zero. If the archive happens to contain more than one, that is not validated: the first one the filesystem returns is used silently, with no check that it is the only one. Whichever directory is used, every file the catalog entry expects (encoder, decoder, joiner, and tokens for `parakeet-v3`) must be present under it, or the install aborts with exit code 1. Extraction of the `parakeet-v3` archive alone can take 30–60 seconds on a typical laptop CPU.

## <name> status

Shows detailed install info for one model: name, display name, version, install path, install timestamp, size, capabilities, and languages, read from the local registry. It also flags "Update available: X → Y" when the catalog has a newer version than what's installed.

`<name> status` takes no flags.

```bash
pks model parakeet-v3 status
```

If `<name>` matches neither the catalog nor an existing install record, it prints "Unknown model" and exits 1. If `<name>` is a valid catalog entry but nothing is installed yet, it prints a hint to run `pks model <name> init` and exits 0 — this is not treated as an error.

## <name> update

Re-installs a model only if the catalog version differs from the installed version. On confirmation, it first deletes the existing install directory and unregisters it from `models.json`, then re-runs the same download, extract, and register sequence as `init`.

`<name> update` takes no flags.

```bash
pks model parakeet-v3 update
```

> **Note.** The existing install directory is deleted before the new download starts, not after a successful one. If the download or extraction then fails, the model ends up fully uninstalled with no rollback to the previous version.

It no-ops with a green "already up to date" message if the installed version matches the catalog — running `update` does not force a re-download the way removing and re-initializing would. It also no-ops with a yellow "is not installed" message (exit 0) if the model was never installed; it does not install it, so run `init` first. The confirmation prompt behaves the same way `init`'s does (default yes, same non-interactive-terminal caveat), though the wording differs — `init` asks "Proceed with download?" while `update` asks "Proceed with update?".

## <name> remove

Uninstalls a model: deletes its install directory (`~/.pks-cli/models/<name>/`) recursively and removes its entry from `models.json`. It shows the disk space that will be freed before asking for confirmation.

`<name> remove` takes no flags.

```bash
pks model parakeet-v3 remove
```

> **Note.** Deletion is irreversible — the install directory is recursively removed with no backup. Unlike `init` and `update`, this confirmation prompt defaults to **no**, so pressing Enter without typing `y` cancels the removal.

If nothing is installed under `<name>`, it prints a dim "is not installed" message and exits 0 without error — it's safe to call speculatively.

## Troubleshooting

- **Download hangs or fails.** `init` and `update` need HTTPS access to `github.com` to fetch the release archive; check outbound network access first.
- **Command hangs in a script or CI job.** `init`, `update`, and `remove` all gate on an interactive confirmation prompt with no `--yes`/`--force` flag. Pipe input or run them interactively.
- **Install aborts with a `FileNotFoundException` or `InvalidOperationException`.** The downloaded archive contained zero top-level directories, or the extracted directory (the first one found, if there was more than one) is missing one of the exact files the catalog entry expects. This points at an upstream release-format change rather than local misconfiguration.
- **No checksum verification.** The catalog's `Sha256` field is currently unset for `parakeet-v3`, so a downloaded archive isn't verified against a known hash. Treat the download as coming from a trusted source (a public GitHub Release) rather than a cryptographically checked one.
- **`pks voice` or `pks transcribe` behavior after `remove`.** How those commands react when their configured local engine is missing was not traced in the current source. If a session depends on the on-device engine, reinstall with `init` before running it.

## See also

- [pks](/tools/pks) — the CLI's full command surface, installation, and global options.
- [sherpa-onnx releases](https://github.com/k2-fsa/sherpa-onnx/releases) — the upstream GitHub Releases page the `parakeet-v3` archive is downloaded from.
