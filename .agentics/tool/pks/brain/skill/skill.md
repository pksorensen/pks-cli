---
title: "pks brain skill"
description: "List, install, and inspect the editable prompts behind the brain's AI phases, so extract, synth, wiki, and adr produce the shape of output you want."
tags: [how-to, brain, skills, prompts]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks brain skill <list|init|show> [options]"
examples:
  - command: "pks brain skill list"
    description: "Show all five skills and where each resolves from"
  - command: "pks brain skill show brain-extract"
    description: "Print the prompt text extract would use right now"
  - command: "pks brain skill init brain-extract"
    description: "Copy the default out to a personal editable file"
  - command: "pks brain skill init brain-extract --agents"
    description: "Install into the repo so Claude and Codex share it"
---

Each AI phase of the brain is driven by a prompt file — a skill. There are five: `brain-extract`, `brain-synth-cluster`, `brain-synth-habits`, `brain-wiki-page`, and `brain-adr`. Every one ships as an embedded default, and every one can be replaced by a copy you edit.

If the extracts are not capturing what matters to you, or the ADRs are the wrong shape, this is the surface to change. Nothing else in the group needs to be touched.

## 1. Prerequisites

- **Nothing for `list` and `show`.** Both read local files only.
- **A git repository** for `init --agents`, which writes into the repository's `.agents/skills/`. Without one it falls back to writing under the working directory.

## 2. See what is in play

```bash
pks brain skill list
```

All five skills print with, for each, whether it resolves from the embedded default or a customized override, a short content hash, and which command uses it.

The status is a resolution check rather than a content diff — it tells you that some path in the lookup hierarchy won, not what changed inside it.

## 3. Read the exact prompt in use

```bash
pks brain skill show brain-extract
```

The resolved body prints to stdout with a header comment naming the source path (or the embedded default) and the content hash. It walks the same hierarchy the AI phases walk, so this is exactly the text that would be sent.

Output is raw markdown rather than console markup, so it pipes and redirects cleanly:

```bash
pks brain skill show brain-adr > ./adr-prompt.md
```

## 4. Install an editable copy

```bash
pks brain skill init brain-extract
```

The embedded default is copied to `~/.claude/skills/brain-extract/SKILL.md` — personal, applying across all your projects. Edit that file and the next `pks brain extract` picks it up automatically.

To share the prompt with a repository and with Codex as well as Claude Code:

```bash
pks brain skill init brain-extract --agents
```

That writes to the repository's `.agents/skills/brain-extract/SKILL.md`. The repository root is found by walking up from the working directory looking for a `.git` entry; if none is found, the file is written under the working directory instead.

`--target` picks an arbitrary destination directory, and is mutually exclusive with `--agents`. An existing file is never overwritten unless `--force` is passed.

## 5. Re-run the affected phase

Changing a prompt does not retroactively change existing output. After editing:

```bash
pks brain extract --force
```

`pks brain status` flags extracts whose stored skill hash differs from the currently-resolved skill, which is how you find what is stale.

## 6. Verify

```bash
pks brain skill list
```

The skill you installed now reports as customized rather than embedded, and its hash matches what `pks brain skill show` prints.

## Options

### skill list

Takes no options.

### skill init

| Flag | Default | Description |
|---|---|---|
| `--target <dir>` | `~/.claude/skills/<name>/` | Destination directory for the copied skill. |
| `--agents` | `false` | Install into the current repository's `.agents/skills/<name>/`, shared between Claude Code and Codex. |
| `--force` | `false` | Overwrite an existing file instead of refusing. |

The positional argument `NAME` is required and must be one of the five known skills.

### skill show

Takes no options. The positional argument `NAME` is required.

## Troubleshooting

**Exit code 1 on `init` or `show`.** The skill name is not one of the five. Run `pks brain skill list` for the exact spellings.

**Exit code 1 when both `--target` and `--agents` are passed.** They are mutually exclusive. Pick one.

**`init` refuses to write.** A `SKILL.md` already exists at the destination. Pass `--force` to replace it.

**Edits had no effect on output.** Existing extracts are not regenerated automatically. Run the affected phase with `--force`.

**`--agents` wrote outside the repository.** No `.git` entry was found walking up from the working directory, so the write fell back to the working directory. Run from inside the repository.

## See also

- [pks brain extract](/tools/pks/brain/extract) — the phase driven by `brain-extract`
- [pks brain synth](/tools/pks/brain/synth) — driven by `brain-synth-cluster` and `brain-synth-habits`
- [pks brain adr](/tools/pks/brain/adr) — driven by `brain-adr`
- [pks brain status](/tools/pks/brain/status) — finds extracts made with a stale prompt version
- [pks brain CLI reference](/tools/pks/brain/reference) — every command and flag in the group
