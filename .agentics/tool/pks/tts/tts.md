---
title: "pks tts"
description: "Generate an MP3 from text or SSML via Azure AI Foundry / Azure Speech, with an optional audio-reactive MP4 rendered through ffmpeg."
tags: [reference, cli, tts, audio, foundry]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks tts [text] [options]"
examples:
  - command: "pks tts \"Hello world\""
    description: "Synthesize a short phrase with the default voice and model"
  - command: "pks tts --text-file script.txt --voice nova --output speech.mp3"
    description: "Synthesize a longer script from a file to a chosen path"
  - command: "pks tts --ssml-file dialog-da.xml --output spike-dialog-da.mp3"
    description: "Synthesize Danish multi-voice dialog from SSML"
  - command: "pks tts \"Launch day.\" --video --video-style pulse --video-size 1080x1920"
    description: "Synthesize speech and render a vertical audio-reactive MP4"
---

`pks tts` turns text or SSML into an MP3 using Azure AI Foundry's `tts-hd` deployment or Azure Speech's neural-voice endpoint, and can optionally render an audio-reactive MP4 from the result.

## Overview

`pks tts` is a single leaf command — it has no subcommands, aliases, or hidden flags. It runs one of two independent synthesis paths and always writes the resulting audio to disk:

- **Plain-text mode** (default): text from the positional argument or `--text-file` goes to an Azure AI Foundry `tts-hd` deployment via an OpenAI-compatible endpoint. `--voice` and `--model` apply here.
- **SSML mode**: a file passed to `--ssml-file` goes to Azure Speech's neural-voice REST endpoint instead. `--voice`, `--model`, and any text input are ignored — the SSML document controls everything, including which voice speaks.

Either path can chain into an optional ffmpeg post-step (`--video`) that renders an audio-reactive MP4 alongside the MP3.

Use plain-text mode with the OpenAI-style voices (`alloy`, `echo`, `fable`, `onyx`, `nova`, `shimmer`) for generic English narration. Switch to `--ssml-file` mode for Danish narration or anything needing SSML control — neural voices such as `da-DK-ChristelNeural` or `da-DK-JeppeNeural`, multi-voice dialog, prosody, or breaks — since plain-text mode has no SSML support.

## Prerequisites

- **Azure AI Foundry authentication.** Run [`pks foundry init`](/tools/pks/foundry/init) first. `pks tts` does not authenticate on its own — it checks the stored Foundry session and a selected resource, and fails immediately if either is missing.
- **ffmpeg, for `--video` only.** Either put `ffmpeg` on `PATH` or point `PKS_FFMPEG_BIN` at the binary.

## Synopsis

```text
pks tts [text] [options]
```

```text
tts    Synthesize speech from text or SSML, optionally rendering an audio-reactive MP4
```

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `PKS_FFMPEG_BIN` | `ffmpeg` (resolved from `PATH`) | Path to the ffmpeg binary used by `--video`. Set this when ffmpeg isn't on `PATH`, especially on Windows. |

## Arguments

| Argument | Required | Description |
|---|---|---|
| `text` | no | Positional text to synthesize. Ignored when `--ssml-file` is set. If omitted, `pks tts` falls back to `--text-file`; if neither is present and `--ssml-file` is also absent, the command exits with a usage error. |

## Options

| Flag | Default | Description |
|---|---|---|
| `--text-file` | — | Path to a text file containing the input, as an alternative to the positional argument. |
| `--ssml-file` | — | Path to an SSML file. Switches synthesis to the Azure Speech neural-voice endpoint (supports `da-DK-ChristelNeural`, `da-DK-JeppeNeural`, multi-voice dialog, prosody, breaks). When set, `--voice`, `--model`, the positional text, and `--text-file` are all ignored. |
| `--format` | `audio-24khz-160kbitrate-mono-mp3` | SSML mode only: the `X-Microsoft-OutputFormat` header value sent to Azure Speech. Has no effect in plain-text mode. |
| `--video` | `false` | Also render an MP4 with an audio-reactive visualization, using ffmpeg, after synthesis succeeds. |
| `--video-size` | `1080x1080` | Video resolution as `WxH`. Use `1080x1920` for vertical formats. |
| `--video-style` | `waves` | Visualization style: `waves`, `volume`, `cqt`, `pulse`, or `scope`. `pulse` draws a centered circle with a sharp waveform behind it; `scope` is an organic moving line. |
| `--voice`, `-v` | `alloy` | Plain-text mode voice: `alloy`, `echo`, `fable`, `onyx`, `nova`, or `shimmer`. Ignored in SSML mode. |
| `--model`, `-m` | `tts-hd` | Plain-text mode TTS model name. Ignored in SSML mode. |
| `--deployment`, `-d` | `tts-hd` | Foundry deployment name for plain-text mode. This is a hardcoded fallback, not a value read from your stored Foundry credentials — see Troubleshooting. |
| `--output`, `-o` | `speech-<timestamp>.mp3` (`speech-ssml-<timestamp>.mp3` in SSML mode) | Output file path. Parent directories are created automatically if they don't exist. |

## How synthesis and output work

The two modes hit genuinely different Azure endpoints. Plain-text mode POSTs a JSON body (`model`/`input`/`voice`) to the deployment's `/openai/deployments/{deployment}/audio/speech` route. SSML mode POSTs the raw SSML document as `application/ssml+xml` to `/tts/cognitiveservices/v1`, with `--format` sent as the `X-Microsoft-OutputFormat` header instead of anything in the body — this is why `--format` only applies to SSML mode. Both endpoints live under the `cognitiveservices.azure.com` resource family, not the newer `services.ai.azure.com` family used by some other pks Foundry integrations; if you're debugging a permissions or endpoint mismatch against your Foundry resource, check that family first.

`pks tts` resolves the output path to an absolute path and prints it as the first line of stdout via a plain write, the same convention `pks image` uses. It then writes a human-readable success line (file size, voice, and timing) to that same stdout stream right after it — nothing is redirected to stderr. A script that wants just the path should take the first line of stdout, not capture the whole stream; the progress spinner is the only output that stays off stdout.

When `--video` is set, ffmpeg runs as a post-step after synthesis succeeds, reading the generated audio and writing an MP4 next to it using the chosen `--video-size` and `--video-style`.

## Examples

```bash
pks tts "Hello world"
```

Writes `speech-<timestamp>.mp3` in the current directory using the default voice (`alloy`) and model (`tts-hd`).

```bash
pks tts --text-file script.txt --voice nova --output speech.mp3
```

Synthesizes a longer script from a file with the `nova` voice, writing to `speech.mp3`.

```bash
pks tts --ssml-file dialog-da.xml --output spike-dialog-da.mp3
```

Synthesizes Danish, multi-voice dialog from an SSML file via the Azure Speech neural endpoint. `--voice` and `--model` would be ignored here even if supplied.

```bash
pks tts "Launch day." --video --video-style pulse --video-size 1080x1920
```

Synthesizes the line and renders a vertical, pulse-style audio-reactive MP4 alongside the MP3 — ready for a LinkedIn Story post.

## Troubleshooting

> **Note.** `--deployment`/`-d` is documented as defaulting to the value from your stored Foundry credentials, but the command always falls back to the literal `tts-hd` when the flag is omitted — it does not read a deployment name from `pks foundry init`'s stored resource selection. Pass `--deployment` explicitly if your Foundry deployment isn't named `tts-hd`.

| Symptom | Cause | Fix |
|---|---|---|
| `Not authenticated with Azure AI Foundry. Run pks foundry init first.` | No stored Foundry session. | Run [`pks foundry init`](/tools/pks/foundry/init). |
| `No Foundry resource configured. Run pks foundry init to select a resource.` | Authenticated, but no Foundry resource was ever selected. | Run `pks foundry init` again to pick a resource. |
| `Failed to acquire Azure access token. Try pks foundry init --force.` | The stored refresh token is expired or invalid. On both plain-text and SSML paths. | Run `pks foundry init --force`. |
| A usage-example error with exit code 1 and no synthesis attempted. | No text was supplied via any of the positional argument, `--text-file`, or `--ssml-file`. | Supply exactly one of the three. |
| Azure's raw validation error text appended after an HTTP status in SSML mode. | Malformed SSML XML or an unsupported voice name in the SSML document. | Read the appended error body — it names the exact validation failure — and fix the SSML file. |
| `Could not launch ffmpeg (...)` | `--video` was set but ffmpeg isn't on `PATH` and `PKS_FFMPEG_BIN` isn't set. | Install ffmpeg or set `PKS_FFMPEG_BIN` to its full path. |
| `Invalid --video-size: ...` | `--video-size` didn't parse as `WxH` integers — caught by `pks tts` itself before ffmpeg is launched, not an ffmpeg error. | Pass a value like `1080x1080` or `1080x1920`. |
| ffmpeg exits non-zero for another reason. | ffmpeg itself failed (for example, a missing codec). | The command surfaces ffmpeg's exit code and full stderr — read it for the actual ffmpeg diagnostic. |
| `Text file not found: <path>` | `--text-file` points at a path that doesn't exist. | Correct the `--text-file` path. |
| `SSML file not found: <path>` | `--ssml-file` points at a path that doesn't exist. | Correct the `--ssml-file` path. |

## See also

- [pks foundry init](/tools/pks/foundry/init) — authenticate and select the Foundry resource `pks tts` requires
- [pks foundry](/tools/pks/foundry) — the Azure AI Foundry auth and model area `pks tts` depends on
- [pks](/tools/pks) — command families overview, including where `tts` sits among content and media commands
