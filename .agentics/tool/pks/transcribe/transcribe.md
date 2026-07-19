---
title: "pks transcribe"
description: "Transcribe an audio or video file to text with heypoul and Azure AI Foundry Speech, or with a local on-device model, in one non-interactive pass."
tags: [reference, cli, voice, speech]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks transcribe <file> [options]"
examples:
  - command: "pks transcribe recording.mp4"
    description: "Transcribe with the default cloud engine and da-DK"
  - command: "pks transcribe recording.mp4 --engines cloud,parakeet-v3"
    description: "Compare the cloud engine against a local model"
  - command: "pks transcribe interview.wav --language en-US --out-dir ./out"
    description: "Skip ffmpeg on a pre-extracted wav and set English"
  - command: "pks transcribe call.m4a --engine parakeet-v3 --model-dir ~/.pks-cli/models/parakeet-v3"
    description: "Transcribe locally with an explicit model directory"
---

`pks transcribe` turns a local audio or video file into a text transcript using the `heypoul` companion binary, either against Azure AI Foundry Speech in the cloud or against a local on-device model. It runs once and exits, writing its output to disk instead of staying interactive.

## Overview
`pks transcribe <file>` is a one-shot, non-interactive command: point it at an existing recording — a meeting capture, a screen recording's audio track, an `.mp4`/`.m4a`/`.wav` dump — and it produces a text transcript on disk. It is registered directly on the root CLI rather than nested under `pks voice`, even though both commands share the same Azure AI Foundry credential handoff and the same `heypoul` binary.

- **Cloud transcription:** the default `cloud` engine sends audio to Azure AI Foundry Speech using your stored Foundry credentials.
- **Local transcription:** an on-device engine such as `parakeet-v3` runs entirely offline, once its model files are installed.
- **Compare mode:** pass a comma-separated `--engines` list to run several engines against the same audio and get side-by-side transcripts.

## When to use it
Use `pks transcribe <file>` for one-shot transcription of a file that already exists on disk. For live push-to-talk dictation while typing, use [pks voice](/tools/pks/voice) instead. To browse past push-to-talk dictations, use `pks voice show`, not this command.

## Prerequisites
- **Azure AI Foundry sign-in.** Run [pks foundry init](/tools/pks/foundry) first. `pks transcribe` checks Foundry authentication and mints an access token immediately after confirming the file exists — this check runs even for local-only engines, so an unconfigured Foundry account blocks `--engine parakeet-v3` runs too.
- **`ffmpeg` on `PATH`**, unless the input file is already a `.wav`. Any other format (`.mp4`, `.m4a`, `.mp3`, …) is converted to mono 16 kHz PCM before transcription. Passing an already-`.wav` file is the deliberate escape hatch for machines without `ffmpeg`, typically Windows.
- **A resolvable `heypoul` binary.** On Windows, `pks` can self-extract an embedded `heypoul.exe` to a temp directory if it was built with `build-local.sh`. On other platforms, build `heypoul` from `projects/heypoul/build.sh` and put it on `PATH`, or in `./projects/heypoul/` or `../projects/heypoul/` relative to your working directory. Use `--heypoul` to point at an explicit binary instead.
- **Installed model files** for any non-cloud engine. `pks transcribe` does not download models — it only wires `HEYPOUL_MODEL_DIR_<engine>` to `~/.pks-cli/models/<engine>` when that directory already exists. Install models first with [pks model](/tools/pks/model).

## Synopsis
```text
pks transcribe <file> [options]
```

```text
transcribe    Transcribe a local audio or video file with heypoul and Azure AI Foundry Speech
```

## transcribe

Transcribes the given file and exits. In order, the command: verifies the file exists; checks Foundry authentication and mints an access token; resolves the `heypoul` binary; computes an output directory; extracts mono 16 kHz PCM audio with `ffmpeg` unless the input is already a `.wav`; invokes `heypoul transcribe` as a child process with the resolved options and Foundry credentials passed through environment variables; streams `heypoul`'s output live to the console while also writing it to `transcribe.log`; and, on success, lists the transcript files it produced.

### Arguments

| Argument | Required | Description |
|---|---|---|
| `file` | yes | Path to the audio or video file to transcribe. The command exits with an error if the path does not exist. |

### Options

| Flag | Default | Description |
|---|---|---|
| `--out-dir <dir>` / `-o <dir>` | `<file-dir>/transcripts/<file-stem>` | Output directory for transcripts and intermediate files. |
| `--engine <id>` | `cloud` | Single engine to use: `cloud` (Azure AI Foundry Speech) or `parakeet-v3` (local on-device model). Ignored when `--engines` is also given. |
| `--engines <csv>` | — | Comma-separated engine ids for compare mode, for example `cloud,parakeet-v3`. Overrides `--engine`. |
| `--model-dir <dir>` | (none — omitted from `heypoul`'s argv unless set) | Model directory for non-cloud engines. Only forwarded to `heypoul` when set explicitly; leaving it unset does not itself default anything (see the `HEYPOUL_MODEL_DIR_<engine>` environment variable below for the actual `~/.pks-cli/models/<engine>` fallback). |
| `--language <tag>` / `-l <tag>` | `da-DK` | BCP-47 language tag passed to `heypoul`. |
| `--chunk-seconds <n>` | `60` | Target chunk length, in seconds, for splitting the audio during transcription. |
| `--heypoul <path>` | (none, auto-detect) | Explicit path to the `heypoul` binary, bypassing `PATH` search, relative-path search, and embedded-resource extraction. |
| `--keep-wav <bool>` | `true` | Whether to keep the extracted intermediate `audio.wav` after a successful run. Transcript chunk files reference it, so the default is to keep it. |

### Environment variables

`pks transcribe` sets these on the `heypoul` child process; they are not read from your shell.

| Variable | Purpose |
|---|---|
| `HEYPOUL_ENDPOINT` | The Azure Foundry Speech REST endpoint, `https://{SelectedResourceName}.cognitiveservices.azure.com`, derived from your stored Foundry resource. |
| `HEYPOUL_TOKEN` | The Azure access token freshly minted for this run. |
| `HEYPOUL_API_KEY` | Set only when the stored Foundry credentials include an API key. |
| `HEYPOUL_LANGUAGE` | The resolved `--language` value. |
| `HEYPOUL_MODEL_DIR_<engine>` | Per-engine model directory for each non-cloud engine in `--engines`, defaulted to `~/.pks-cli/models/<engine>` when that directory exists and the variable is not already set. |

### Examples

```bash
pks transcribe recording.mp4
```

Transcribes with the default cloud engine, `da-DK`, and 60-second chunks; output lands under `recording-dir/transcripts/recording/`.

```bash
pks transcribe recording.mp4 --engines cloud,parakeet-v3
```

Runs both the Azure cloud engine and the local `parakeet-v3` model against the same audio for side-by-side transcripts.

```bash
pks transcribe interview.wav --language en-US --out-dir ./out
```

Skips `ffmpeg` because the input is already a `.wav`, forces English, and writes to a custom output directory.

```bash
pks transcribe call.m4a --engine parakeet-v3 --model-dir ~/.pks-cli/models/parakeet-v3
```

Transcribes locally only, with an explicit model directory.

## Troubleshooting

> **Note.** `transcribe.log` in the output directory is the authoritative place to look when terminal output seems truncated or a run fails. On Windows the embedded `heypoul.exe` is built without a console window, so its output only reaches you through this log and the console tee.

- **"Not authenticated with Azure AI Foundry" or "No Foundry resource configured."** Run `pks foundry init`. This check runs unconditionally, even for `--engine parakeet-v3`.
- **Access-token acquisition fails after the initial auth check passes.** Re-authenticate with `pks foundry init --force`.
- **`ffmpeg` is missing or fails on a non-`.wav` input.** Install `ffmpeg`, or pre-extract a `.wav` yourself and pass that instead — the command exits with `ffmpeg`'s own exit code.
- **`heypoul` binary not found.** Build it from `projects/heypoul/build.sh` and put it on `PATH` or in `./projects/heypoul/` / `../projects/heypoul/`, or pass `--heypoul <path>` explicitly. On Windows, a `pks` build made with `build-local.sh` can self-extract it instead.
- **A local engine run fails inside `heypoul`.** `pks transcribe` does not install model files; confirm the model is present with [pks model](/tools/pks/model) before retrying.
- **Re-running against the same file and output directory.** The directory is reused, not cleaned: `audio.wav` and `transcribe.log` are overwritten, but prior `transcript-*` files are not deleted first.

## See also

- [pks voice](/tools/pks/voice) — live push-to-talk dictation, a different `heypoul` mode, and where to browse past dictations.
- [pks foundry](/tools/pks/foundry) — the Azure AI Foundry sign-in this command requires before it can mint a token.
- [pks model](/tools/pks/model) — download and manage the local on-device models used by non-cloud engines.
