---
title: "pks brain conversation"
description: "Export a single Claude session as readable markdown — human prompts and assistant replies, with tool traffic collapsed into references instead of inlined."
tags: [how-to, brain, export, claude]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain conversation <session> [options]"
examples:
  - command: "pks brain conversation 40dafe36-24bd-4169-aacb-3955d6a442a2"
    description: "Export a session by ID to the project brain"
  - command: "pks brain conversation ./session.jsonl --output ./conversation.md"
    description: "Export a raw JSONL file to a chosen path"
  - command: "pks brain conversation ./session.jsonl --include-intermediate"
    description: "Keep the assistant's narration between tool calls"
---

Conversation turns one raw session transcript into something a person can read: the human prompts and the assistant's text replies, with tool traffic collapsed into references to the raw source rather than inlined verbatim. It is deterministic and costs nothing — the alternative to paying for an AI summary when you want to reread what actually happened.

## 1. Prerequisites

- **A session ID or a path** to a raw session JSONL file.
- **A git repository**, if you want the default output location. Outside one, the file lands next to the working directory instead.

## 2. Export by session ID

```bash
pks brain conversation 40dafe36-24bd-4169-aacb-3955d6a442a2
```

The argument is resolved two ways. If it names an existing file, that file is used directly. Otherwise it is treated as a session ID and matched by filename against every discovered session.

The default output path is `./.pks/brain/conversations/<session-id>.md`. Outside a git repository the fallback is `<session-id>.conversation.md` next to the working directory.

## 3. Export a file directly

```bash
pks brain conversation ./session.jsonl --output ./conversation.md
```

Passing a path avoids ID resolution entirely, which is also the fix for an ambiguous ID.

## 4. Control what is kept

```bash
pks brain conversation ./session.jsonl --include-intermediate
pks brain conversation ./session.jsonl --max-message-chars 4000
```

By default only final, end-of-turn assistant replies are kept. `--include-intermediate` also keeps the progress narration the assistant writes between tool calls — more faithful, considerably longer.

`--max-message-chars` caps how much of each visible text block is written inline; the remainder is referenced rather than reproduced. It defaults to 12000 and must be positive, or the command exits with code 1.

## 5. Verify

```bash
cat ./.pks/brain/conversations/40dafe36-24bd-4169-aacb-3955d6a442a2.md
```

You should see alternating human and assistant sections, with tool calls appearing as references to the raw source.

## Options

| Flag | Default | Description |
|---|---|---|
| `-o, --output <path>` | `./.pks/brain/conversations/<session-id>.md` | Output markdown path. |
| `--max-message-chars <n>` | `12000` | Maximum characters kept inline per visible text block. Must be positive. |
| `--include-intermediate` | `false` | Keep assistant progress narration between tool calls. |

The positional argument `<session>` is required and accepts a session ID or a path to a raw session JSONL file.

## Troubleshooting

**Several matches were printed and nothing was written.** The same session ID was found in more than one JSONL file. Re-run with the full path to the file you want.

**The file landed somewhere unexpected.** Outside a git repository the default path falls back to `<session-id>.conversation.md` in the current directory. Pass `--output` to be explicit.

**Exit code 1 immediately.** `--max-message-chars` was zero or negative.

## See also

- [pks brain search](/tools/pks/brain/search) — locate the session before exporting it
- [pks brain scan filepath](/tools/pks/brain/scan) — find sessions by the files they touched
- [pks brain extract](/tools/pks/brain/extract) — the AI summary alternative to a full transcript
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
