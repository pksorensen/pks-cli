---
title: "pks voice"
description: "Push-to-talk voice dictation for pks: hold a key to speak, release to inject the transcript, powered by the heypoul binary and Azure AI Foundry Speech."
tags: [reference, cli, voice, speech]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks voice <command> [options]"
examples:
  - command: "pks voice start"
    description: "Start the daemon with saved or default settings"
  - command: "pks voice start --key 100 --language da-DK"
    description: "Start with an explicit push-to-talk key and language"
  - command: "pks voice off"
    description: "Stop the running daemon"
  - command: "pks voice show -n 50"
    description: "Browse the last 50 dictations"
  - command: "pks voice settings"
    description: "Open the native settings window (Windows only)"
---

`pks voice` is the push-to-talk dictation branch of the CLI: hold a key, speak, release the key, and the transcript is injected into whatever window has focus. pks itself only handles Azure AI Foundry authentication, engine and microphone selection, and config persistence — the actual recording, transcription, and text injection is done by a companion Go binary called `heypoul` that pks execs as either a background daemon or a blocking foreground process.

## Overview

`heypoul` reads its own config from `~/.config/heypoul/config.json` on Linux or `%APPDATA%\heypoul\config.json` on Windows. `pks voice start` writes to that config and then execs `heypoul`, passing the resolved auth token, subscription key, language, key code, injection mode, engine, and model directory as `HEYPOUL_*` environment variables — it does not talk to Azure Speech directly itself.

- **Dictate anywhere:** `pks voice start` runs the daemon; hold the configured key in any focused window to record.
- **Recover a past dictation:** `pks voice show` re-copies or re-prints something you said without recording it again.
- **Stop a stuck daemon:** `pks voice off` kills it by PID file.
- **Reconfigure without restarting:** `pks voice settings` opens heypoul's own settings window (Windows only).

A closely related command, `pks transcribe <file>`, is **not** nested under `voice` — it is a separate top-level command registered directly in `Program.cs`, implemented in the same source folder, and sharing the identical Foundry-auth and `heypoul`-resolution logic as `voice start`. Use it for one-shot transcription of an existing recording instead of live dictation.

## Prerequisites

- **Azure AI Foundry sign-in.** `voice start` requires a stored, authenticated Foundry credential with a selected resource — run `pks foundry init` first. It also requires the stored resource's region (`SelectedResourceLocation`); if that's missing, re-run `pks foundry select` to refresh it.
- **The `heypoul` binary.** On Linux and macOS it must be built from source and be on `PATH` or in one of the command's relative fallback paths. On Windows it's normally embedded in the pks executable and self-extracts to `%TEMP%\heypoul.exe` on first run — the extraction always overwrites the existing file to avoid stale-binary mismatches.
- **A non-cloud engine, if you want one.** Selecting a locally installed speech engine (for example `parakeet-v3`) that hasn't been installed via `pks model <name> init` fails hard with a pointer to that command.
- **Root history file, for `show`.** `~/.heypoul_history.jsonl` is written by `heypoul` itself on every transcription — it doesn't exist until you've dictated at least once.

## Commands

`start` · `off` · `show` · `settings` — none of the four have aliases. Full detail below.

## start

Starts the `heypoul` push-to-talk daemon. It authenticates to Azure AI Foundry, resolves the transcription engine (cloud Azure Speech, or a locally installed model via `pks model`), picks a microphone, optionally builds a voice-phrase-to-shell-command map, then execs `heypoul` — as a background process by default, or as a blocking foreground process with a live waveform in `--inline` mode. Hold the configured key to record; release it to transcribe and inject the text (or, in `command` injection mode, press Enter after injection).

| Flag | Default | Description |
|---|---|---|
| `--key <code>` / `-k <code>` | `100` (Linux) / `165` (Windows) | Key code for push-to-talk. Linux: `/dev/input` code (run `sudo find-key`). Windows: VK hex, e.g. `0xA5` for right-alt. |
| `--language <tag>` / `-l <tag>` | `da-DK` | Speech recognition language, falling back to the saved `heypoul` config, then `da-DK`. |
| `--inject <mode>` | `text` | Injection mode: `text` or `command` (`command` presses Enter after injection). |
| `--heypoul <path>` | auto-detect | Path to the `heypoul` binary. |
| `--inline` | `false` | Block the terminal and show an inline waveform display (debug mode). |

```bash
pks voice start
```

```bash
pks voice start --key 100 --language da-DK
```

Device, engine, key, and language choices are cached in `heypoul`'s own `config.json` and reused silently on the next run — changing microphone or engine again requires `pks voice settings`, or editing that file directly, not just re-running `start`.

> **Note.** `--inline` with multiple STT engines selected runs them in parallel for side-by-side comparison and disables text injection entirely — it's a debug mode, not a dictation mode.

Daemon mode prints the log, PID, and state file paths in the OS temp directory but doesn't tail them — watch live transcripts with `tail -f` (or `Get-Content -Wait` on Windows) yourself. The interactive voice-phrase-to-shell-command prompt runs on every `start` unless declined.

## off

Stops the running `heypoul` daemon by reading its PID from `%TEMP%/heypoul.pid` and killing the process, then removes the PID file. Takes no flags.

```bash
pks voice off
```

If the PID file is missing, the command reports "not running" and exits `0` — it does not scan for a `heypoul` process by name. If the PID file points to a process that no longer exists, it silently cleans up the stale file instead of erroring. It only tracks a daemon started via non-inline `start`; a blocking `--inline` process has no PID file and is stopped with Ctrl-C or by closing its terminal instead.

## show

Browses recent dictation history from `~/.heypoul_history.jsonl` and lets you pick an entry to re-inject: the chosen text is copied to the OS clipboard (best-effort, via `Set-Clipboard`/`pbcopy`/`xclip`/`xsel`) and printed to stdout for scripting or piping.

| Flag | Default | Description |
|---|---|---|
| `--count <n>` / `-n <n>` | `30` | Number of recent dictations to show. |

```bash
pks voice show
```

```bash
pks voice show -n 50
```

If `~/.heypoul_history.jsonl` doesn't exist yet, the command tells you to run `pks voice start` first — there's nothing to browse until at least one dictation has happened. Malformed JSONL lines are skipped silently, not reported as errors. Clipboard write is fully best-effort: on Linux without `xclip` or `xsel` installed, it silently falls back to printing only, with no error. The selection list marks command-phrases with a lightning icon and plain dictations with a speech-bubble icon; re-injecting prefers the entry's injected text and falls back to the raw transcript.

## settings

Launches `heypoul`'s own native settings window (microphone, language, push-to-talk key) as a fire-and-forget detached process, so you can reconfigure without hand-editing `config.json` or restarting the daemon. Takes no flags.

```bash
pks voice settings
```

> **Note.** The settings GUI is Windows-only. On Linux and macOS the command prints the `config.json` path and tells you to edit it manually — it exits `0`, not as an error, and does nothing else.

It works even before `pks voice start` has ever run, independently resolving or extracting the embedded `heypoul.exe` on Windows. It does not wait for the GUI to close or confirm that a save happened; it launches the process and returns immediately.

## How pks talks to heypoul

`pks voice start` never talks to Azure Speech directly. It resolves auth and settings, then hands everything to the `heypoul` child process as environment variables:

| Variable | Default | Purpose |
|---|---|---|
| `HEYPOUL_ENDPOINT` | (set by pks) | Azure Speech REST endpoint, built from the selected Foundry resource's region. |
| `HEYPOUL_TOKEN` | (set by pks) | Azure AD bearer token for the Foundry Cognitive Services scope. |
| `HEYPOUL_API_KEY` | (set by pks when available) | Resource subscription key, preferred by the Speech REST API over the bearer token. |
| `HEYPOUL_LANGUAGE` | (set by pks) | Resolved language tag: `--language` flag, then saved config, then `da-DK`. |
| `HEYPOUL_KEY` | (set by pks) | Resolved push-to-talk key code. |
| `HEYPOUL_INJECT` | (set by pks) | `text` or `command` injection mode. |
| `HEYPOUL_ENGINE` / `HEYPOUL_ENGINES` | (set by pks) | Single engine id, or a comma-separated list for `--inline` multi-engine comparison. |
| `HEYPOUL_MODEL_DIR` / `HEYPOUL_MODEL_DIR_<engine>` | (set by pks) | Install path of a non-cloud STT model, per engine id. |
| `HEYPOUL_COMMANDS` | (set by pks) | JSON map of voice phrase to shell command. |
| `HEYPOUL_CLASSIFIER` | (set by pks, when configured via `pks foundry select`) | Voice classifier model, when one is stored in the Foundry credential. |
| `HEYPOUL_DEVICE_NAME` | (set by pks, when a microphone was chosen or saved) | Named microphone; unset means `heypoul` uses the system default. |

These are outputs pks writes for `heypoul` to read — there's nothing to set in your own shell.

## Troubleshooting

- **"Not authenticated" or "no resource selected" on `start`.** Run `pks foundry init`. If the error is specifically about region, run `pks foundry select` to refresh the stored `SelectedResourceLocation`.
- **`heypoul` not found on Linux or macOS.** Build it from source and put it on `PATH`, or pass `--heypoul <path>` explicitly.
- **Non-cloud engine fails to start.** Install the model first with `pks model <name> init`.
- **Local STT engines silently fall back to cloud-only on Windows.** A missing sherpa-onnx/onnxruntime DLL embed is not surfaced as an error — check that the DLLs were extracted alongside `heypoul.exe`.
- **`pks voice off` says "not running" but a process is clearly stuck.** The command only reads the PID file; it does not scan by process name. Kill the process manually and remove `%TEMP%/heypoul.pid`.
- **`pks voice show` finds nothing.** Dictate at least once with `pks voice start` first — the history file doesn't exist until then.
- **Clipboard copy silently does nothing on Linux.** Install `xclip` or `xsel`; the command prints the text either way.
- **`pks voice settings` does nothing visible.** On Linux and macOS it's a no-op stub by design — it prints the `config.json` path instead of opening a window.

## See also

- [pks foundry](/tools/pks/foundry) — sign in and select the Azure AI Foundry resource that `voice start` and `transcribe` both authenticate against
