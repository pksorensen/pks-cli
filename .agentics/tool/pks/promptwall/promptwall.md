---
title: "pks promptwall"
description: "Turn a prompt from a local Claude Code session transcript into a branded 1200x1200 social-media image via the Google AI Gemini image model."
tags: [reference, cli, image-generation, transcripts]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks promptwall [options]"
examples:
  - command: "pks promptwall"
    description: "Interactive flow scoped to the current project's transcripts"
  - command: "pks promptwall --all-projects"
    description: "Pick from prompts across every Claude project"
  - command: "pks promptwall --include-reply --output ./out"
    description: "Always include the reply, write images to ./out"
---

`pks promptwall` turns a prompt from a local Claude Code session transcript into a branded 1200x1200 social-media image — a shareable "today I asked Claude to…" card for X and LinkedIn. It scans your local transcripts, walks you through picking a prompt, and sends a fixed visual template to a Google AI image model.

## Overview

`pks promptwall` reads Claude Code's own session history from `~/.claude/projects/<encoded-project-path>/*.jsonl`, the same transcript files Claude Code itself writes during a session. There's nothing to configure: point it at a project (or all of them), pick a prompt from an interactive list, choose whether to include Claude's reply, confirm, and it generates the image.

- **Transcript-driven, not freeform.** Unlike `pks image`, which generates from any text prompt you supply, `promptwall` only ever renders text pulled from an actual past Claude Code exchange.
- **One fixed visual template.** A soft diagonal gradient card (cyan→violet for the prompt side, violet→cyan for the reply side), centered white monospace text with a drop shadow, an uppercase label, and a "pks promptwall" wordmark bottom-right. There is no template selection.
- **Text is rendered verbatim.** Whatever you pick (or edit) is sent to the image model unparaphrased — see [Troubleshooting](#troubleshooting) before including anything sensitive.

## When to use it

- **After a Claude Code session**, when a specific prompt or exchange is worth turning into a shareable image without a manual screenshot.
- **Not for arbitrary image generation** — use [`pks image`](/tools/pks/image) when the source text isn't a real transcript exchange, or when you need a different visual template.

## Prerequisites

- **Local Claude Code session transcripts** under `~/.claude/projects/<encoded-cwd>/*.jsonl` for the target scope — you must have already run Claude Code in that project.
- **A registered Google AI key.** Run [`pks google init`](/tools/pks/google) first. `promptwall` only checks for this after the entire interactive picker flow, so a missing key surfaces late, not up front.

## Synopsis

```text
pks promptwall [options]
```

`pks promptwall` is a single leaf command — it has no subcommands.

### Environment variables

`pks promptwall` reads no environment variables of its own; it delegates authentication to [`pks google`](/tools/pks/google).

## promptwall [options]

Discovers session transcripts for the requested scope (current project by default, or every project with `--all-projects`, or an explicit `--project` path), then shows an interactive project picker when more than one project has sessions (or always with `--pick-project`). It filters out anything that isn't a genuine user-typed prompt — sidechain and `isMeta` lines, injected task-notification prompts (`origin.kind`-tagged), `/compact` continuation summaries, slash-command echoes, and tool-result-only messages — before showing an interactive picker over the most recent `--count` prompts, newest first.

For the picked prompt you then choose **Prompt only**, **Prompt + reply**, **Edit prompt text…**, or **Cancel**. A confirmation panel shows the final text and image count before anything is generated. Only after that confirmation does it check for a registered Google AI key and call the image model — once per generated card. A short prompt-only or short combined prompt+reply produces 1 card; either side being long produces 2 cards; a side that exceeds roughly 800 characters is paginated into up to `--max-pages` cards, breaking preferentially at paragraph, sentence, then word boundaries, with the final page truncated with an ellipsis if content still remains. Every card is capped at 1200 characters and rendered verbatim into the fixed gradient template described in [Overview](#overview).

Reply extraction walks forward from the picked prompt's transcript entry and stops at the next real user prompt or an assistant message with `stop_reason=end_turn`. If extraction fails for any reason it's swallowed silently, and the "Prompt + Claude's reply" option does not appear in the picker for that prompt.

| Flag | Default | Description |
|---|---|---|
| `-p, --project <path>` | current directory | Path to the project directory to analyze; bypasses the interactive project picker entirely. |
| `--all-projects` | — | Pick from prompts across every Claude project instead of just the current one. |
| `--pick-project` | — | Always show the project picker, even when only one project has sessions. Ignored if `--project` or `--all-projects` is set. |
| `--count <n>` | `10` | How many recent prompts to show in the picker. |
| `--include-reply` | — | Skip the "what should be on the image" picker step and always include Claude's reply when one can be extracted. |
| `-o, --output <dir>` | current directory | Output directory for the generated image(s); created if it doesn't exist. |
| `-m, --model <name>` | `gemini-3.1-flash-image-preview` | Image model passed through to the Google AI image API. |
| `--aspect-ratio <ratio>` | `1:1` | Aspect ratio: `1:1`, `3:4`, `4:3`, `9:16`, or `16:9`. Currently a dead parameter — it is dropped before the Gemini API call and never affects the request; the image prompt template always requests a 1200×1200 1:1 square, so output is always square regardless of this flag. |
| `--max-pages <n>` | `4` | Maximum cards per side when paginating a long prompt or reply. |

```bash
pks promptwall
```

Scopes discovery to the current directory's Claude project and walks through the full picker flow.

```bash
pks promptwall --all-projects
```

Spans every Claude project with session history in the project picker.

```bash
pks promptwall --include-reply --output ./out
```

Skips the prompt-only/with-reply choice — always includes the reply when extractable — and writes images to `./out` instead of the current directory.

## Output files

Each generated card is written as `promptwall-<timestamp>[-suffix].jpg` in the output directory, alongside two sibling text files written next to it:

- **`.prompt`** — the exact image-generation instruction sent to the model.
- **`.source.txt`** — the cleaned prompt/reply text actually rendered onto the card.

Each saved file's full path is also echoed to raw stdout, in addition to the console message, so a script or agent can capture it. When cleaning up or `.gitignore`-ing an output directory, account for all three files per image.

## Troubleshooting

> **Note.** The Google AI auth check happens at the very end of the flow, after the full interactive picker and the confirmation prompt. You can walk through project selection, prompt selection, and confirmation, and only then see "No Google AI API key registered. Run pks google init first."

- **"No Claude session files found for: …"** — no transcripts exist for the requested scope. `promptwall` exits `1` immediately with no fallback; run Claude Code in that project first, or check the `--project` path.
- **"No user prompts found in this scope."** — the session consists only of filtered-out lines (sidechain, `isMeta`, injected task notifications, `/compact` summaries, slash-command echoes, tool-result-only messages). No plain-string user prompts were found to pick from.
- **"Prompt + Claude's reply" is missing from the picker.** Reply extraction failed for that prompt and the failure was swallowed silently — pick "Prompt only" or "Edit prompt text…" instead.
- **Sensitive text ends up in the image.** Text is rendered verbatim and unparaphrased with no redaction step. Choose "Edit prompt text…" before generating if the prompt or reply contains anything you don't want on the card.
- **A multi-card run stops partway through.** `HttpRequestException` and `InvalidOperationException` from the image call are caught and reported per-image, but abort the rest of the run — a failure partway through a multi-page run leaves only the earlier cards on disk.
- **Nothing is written even though the flow completed.** Declining the "Generate image?" confirmation (or choosing Cancel earlier) exits cleanly with no output files.

## See also

- [pks](/tools/pks) — the CLI's landing page, with the full command family overview
- [pks google](/tools/pks/google) — register the Google AI key `promptwall` requires
- [pks image](/tools/pks/image) — generate an image from an arbitrary text prompt instead of a transcript
- [pks claude](/tools/pks/claude) — inspect and analyze the same Claude Code session history `promptwall` reads
</content>
