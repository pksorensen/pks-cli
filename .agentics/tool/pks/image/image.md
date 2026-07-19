---
title: "pks image"
description: "Generate or edit an image from a text prompt via Google AI Studio (Gemini/Imagen) or Azure AI Foundry (gpt-image/dall-e), auto-resolved from the model name."
tags: [reference, cli, media]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks image [prompt] [options]"
examples:
  - command: "pks image --list-models"
    description: "See which models each authenticated provider can serve"
  - command: "pks image \"a dark editorial photograph of a match burning\""
    description: "Plain from-scratch generation with the default model"
  - command: "pks image --prompt-file prompt.txt --output cover.jpg"
    description: "Read the prompt from a file, write to a named path"
  - command: "pks image --input bg.jpg \"Add title 'My Book' in white serif\" --output cover-final.jpg"
    description: "Edit an existing image with an instruction prompt"
  - command: "pks image --model gpt-image-2 \"a red fox in autumn forest\""
    description: "Route to the foundry provider via the model prefix"
---

`pks image` generates or edits an image from a text prompt, dispatching to whichever provider — Google AI Studio or Azure AI Foundry — serves the requested model. It is a single leaf command, registered as `pks image` with no subcommands.

## Overview

`pks image` takes a prompt (positional argument or `--prompt-file`), resolves which provider serves the requested `--model`, calls that provider's generation API, and writes the resulting bytes to `--output`. Passing `--input` switches it from generation to editing: the prompt becomes an instruction applied to an existing image.

- **From-scratch generation.** Give a text prompt and get a new image written to disk.
- **Editing.** Add `--input` and the same prompt argument becomes an editing instruction against that image.
- **Model discovery.** `--list-models` skips generation entirely and prints every model each authenticated provider can currently serve.
- **Provider auto-resolution.** The `--model` name decides the provider unless `--provider` forces one.

## What you get

- **A written image file.** Default path `./image-<yyyyMMdd-HHmmss>.jpg`, or the path given with `--output`. Parent directories are created if missing.
- **A companion `.prompt` file.** Every successful run also writes `<output-basename>.prompt` next to the image, recording the exact prompt used — this happens unconditionally, not just on request.
- **Two providers behind one flag.** `--model` names starting with `gemini`/`imagen` resolve to the `google` provider; names starting with `gpt-image`/`dall-e` (or listed in a Foundry deployment's `EnabledModels`) resolve to `foundry`.
- **A scriptable stdout contract.** The final absolute output path is printed as the command's last stdout line specifically so a script or agent can capture it, separate from the human-readable confirmation line.

## How it fits together

`pks image` does not authenticate anything itself — it delegates to whichever provider owns the requested model, and that provider's credentials must already exist. For `google`-resolved models it checks a Google AI Studio API key stored by [`pks google init`](/tools/pks/google). For `foundry`-resolved models it checks Azure AI Foundry credentials stored by [`pks foundry init`](/tools/pks/foundry) (an `ApiKey` or OAuth `RefreshToken`, plus a selected resource endpoint). If the resolved provider isn't authenticated, the command fails immediately with a red error naming the exact setup command to run; it does not fall back to a different provider unless another authenticated provider also claims the same model name.

Once a provider is resolved, `pks image` calls that provider's `GenerateAsync` (or, when `--input` is set, its edit path) and writes the returned bytes to `--output`.

- **Generation path:** prompt (or `--prompt-file`) → provider resolved from `--model` → provider's `images/generations`-style call → bytes written to `--output` + sibling `.prompt` file.
- **Edit path:** `--input` + prompt-as-instruction → same provider resolution → provider's `images/edits`-style call (foundry uses a distinct, newer API version for edits than for generations) → bytes written to `--output` + sibling `.prompt` file.

## Prerequisites

- **A stored credential for at least one provider.** For `gemini`/`imagen` models, run [`pks google init`](/tools/pks/google) once to store a Google AI Studio API key. For `gpt-image-*`/`dall-e-*` models, run [`pks foundry init`](/tools/pks/foundry) once to store Azure AI Foundry credentials.
- **An actual image-model deployment on the Foundry side.** `pks foundry init` succeeding proves auth works, not that an image model (for example `gpt-image-2`) is deployed on the selected resource. Confirm with `--list-models`.
- **Run `--list-models` first** if you are unsure which models are currently usable — it queries every registered provider, skips unauthenticated ones, and prints what remains.

## Synopsis

```text
pks image [prompt] [options]
```

## image

Generates an image from `prompt` (or `--prompt-file`), or edits an existing image when `--input` is set — the prompt then becomes an editing instruction. With `--list-models`, the command instead prints a discovery table and exits without generating anything.

| Argument | Required | Description |
|---|---|---|
| `prompt` | no | The image generation prompt (positional). Optional because `--prompt-file` or `--list-models` can substitute; if none of the three resolve to text, the command prints usage examples and exits 1. |

| Flag | Default | Description |
|---|---|---|
| `--prompt-file <path>` | `(none)` | Path to a text file containing the prompt, used when the positional `prompt` is empty. Exits 1 with a red error if the path doesn't exist. |
| `-m`, `--model <name>` | `gemini-3.1-flash-image-preview` | Model to use. Names starting with `gemini`/`imagen` resolve to the `google` provider; names starting with `gpt-image`/`dall-e` (or in the Foundry deployment's `EnabledModels`) resolve to `foundry`. |
| `-o`, `--output <path>` | `./image-<yyyyMMdd-HHmmss>.jpg` | Output file path. A sibling `.prompt` file (same basename, `.prompt` extension) is always written alongside it. |
| `--aspect-ratio <ratio>` | `auto` | One of `1:1`, `3:4`, `4:3`, `9:16`, `16:9`, `auto`. On `foundry`, mapped to a concrete pixel size: `16:9`/`4:3` → `1792x1024`, `9:16`/`3:4` → `1024x1792`; anything else falls through to `--resolution` or a `1024x1024` default. |
| `--resolution <size>` | model/provider decides | Output resolution, e.g. `512`, `1024`, `2048`, `4096`. On `foundry`, used only as an `NxN` fallback for aspect ratios outside the `16:9`/`9:16`/`4:3`/`3:4` presets. |
| `-i`, `--input <path>` | `(none)` | Input image (jpg/png) to edit or augment. When set, `prompt` is treated as an editing instruction. Exits 1 if the path doesn't exist. On `foundry` this routes to the `images/edits` API instead of `images/generations`. |
| `-l`, `--list-models` | `false` | List available image models across every registered provider and exit, skipping prompt/provider resolution entirely. Prints each unauthenticated provider's setup hint if none are authenticated, and exits 1 in that case. |
| `--provider <name>` | auto-resolved from `--model` | Force a specific provider (`google` or `foundry`) instead of auto-resolving. Exits 1 with that provider's setup hint if it isn't authenticated. Does **not** bypass the model-name check — forcing `google` with a foundry-shaped model string still sends that string to Google's API and will likely fail server-side. |

```bash
pks image --list-models
```

Prints a Provider / Model / Display Name / Description table for every authenticated provider.

```bash
pks image "a dark editorial photograph of a match burning"
```

Generates with the default model (`gemini-3.1-flash-image-preview`, via `google`) and writes `./image-<timestamp>.jpg`.

```bash
pks image --prompt-file prompt.txt --output cover.jpg
```

Reads the prompt from `prompt.txt` and writes the result to `cover.jpg` (plus `cover.prompt`).

```bash
pks image --input bg.jpg "Add title 'My Book' in white serif at the top" --output cover-final.jpg
```

Edits `bg.jpg` per the instruction and writes `cover-final.jpg`.

```bash
pks image --model gpt-image-2 "a red fox in autumn forest"
```

Routes to the `foundry` provider via the `gpt-image-` prefix; requires [`pks foundry init`](/tools/pks/foundry) to have been run with an image model deployed.

## Troubleshooting

**`pks google init` named in the error.** The resolved model needs the `google` provider and no Google AI Studio API key is stored. Run [`pks google init`](/tools/pks/google).

**`pks foundry init` named in the error.** The resolved model needs the `foundry` provider and no usable Azure AI Foundry credentials are stored. Run [`pks foundry init`](/tools/pks/foundry), then confirm an image model (for example `gpt-image-2`) is actually deployed on the selected resource — successful Foundry auth does not guarantee a deployment exists.

**`--list-models` prints only setup hints, no models.** No provider is authenticated at all. Follow the printed hint for `google` or `foundry`, then re-run.

**A model name fails to auto-resolve.** Provider resolution for `google` is a prefix check (`gemini`/`imagen`); an unusual model name may not match even if it would technically work. Force the provider explicitly with `--provider`.

**`[red]API error:[/]` with an HTTP-shaped message.** The provider's HTTP call failed (`HttpRequestException`). Exit code is 1; there is no distinct exit code beyond that.

**`[red]Error:[/]` with a logical message.** A provider-side invariant failed — missing credentials, an empty response, or a bad endpoint (`InvalidOperationException`). Exit code is 1.

**Edits behave differently from generations against the same Foundry deployment.** `--input` routes Foundry through a different, newer API version (`2025-04-01-preview` for edits vs. `2024-02-01` for generations) — this is provider-internal but explains behavior gaps between the two modes.

**`--resolution` seems to be ignored on Foundry.** Foundry only supports `1024x1024`, `1792x1024`, and `1024x1792`. `--resolution` is used only as an `NxN` fallback for aspect ratios that aren't `16:9`/`9:16`/`4:3`/`3:4` — it does not produce arbitrary sizes.

**Forcing `--provider` doesn't fix a mismatched model.** `--provider` selects the provider but does not validate the model string against it — a foundry-shaped model name sent to `google` (or vice versa) will still be attempted and will likely fail server-side rather than being caught up front.

> **Note.** Every successful run writes a second file, `<output-basename>.prompt`, next to the image — do not be surprised by the extra file when scripting around `--output` paths.

## See also

- [pks google](/tools/pks/google) — stores the Google AI Studio API key that authenticates `gemini`/`imagen` models
- [pks foundry](/tools/pks/foundry) — stores the Azure AI Foundry credentials that authenticate `gpt-image-*`/`dall-e-*` models
- [pks](/tools/pks) — the full command surface and where `image` fits among the content and media commands
