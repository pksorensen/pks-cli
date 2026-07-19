---
title: "pks tools reference"
description: "Reference for pks tools publish, which reflects over ToolRegistryExport-tagged pks commands and regenerates their agentics.dk/tools Markdown docs."
tags: [reference, cli, documentation]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks tools publish"
examples:
  - command: "pks tools publish"
    description: "Regenerate tools-registry Markdown for tagged commands"
---

`pks tools publish` is the one command under the `pks tools` branch, and it generates the Markdown documentation that publishes individual pks commands to agentics.dk/tools. It reflects over the currently running pks-cli assembly, finds every command class carrying a `ToolRegistryExport` attribute, and writes one Markdown file per tagged command.

The command lives at `src/Commands/Tools/ToolsPublishCommand.cs`. The `tools` branch itself is a bare grouping with no options of its own, and `publish` takes no arguments or options. Running it writes local files only — it does not commit or deploy anything.

## Synopsis

```text
pks tools <command> [options]
```

```text
publish    Generate and write tools-registry Markdown for ToolRegistryExport-tagged commands
```

## publish

Running `pks tools publish` walks the assembly for command classes decorated with `[ToolRegistryExport(slug, ...)]`, and for each one writes `{registryRoot}/{slug-path}/{leaf}.md`: YAML frontmatter (`title`, `description`, `tags`, `category`, `platform`, `icon`, `status`, `type`, `author`, `component`, `usage`, `examples`) followed by a Markdown body — an H1 title, description prose, a Usage code block, and an Examples code block.

Before writing, it locates the target `tools-registry/` directory by walking up from the current working directory, checking up to 8 parent levels. If no such directory is found, nothing is written to disk — every generated document is printed to stdout instead. When files are written, the command prints a summary table (Slug / File / Status) and a reminder that the new files still need to be committed and the site deployed to actually go live.

It takes no arguments or options.

```bash
pks tools publish
```

Run this from inside (or below) a directory tree that contains `tools-registry/`. You should see a summary table listing each generated slug, its file path, and a written/printed status, followed by the commit-and-deploy reminder.

### Frontmatter it generates

Fields the command writes into each generated file's YAML block:

| Field | Emitted value |
|---|---|
| `title` | From the `ToolRegistryExport` attribute |
| `description` | From the `ToolRegistryExport` attribute |
| `tags` | From the `ToolRegistryExport` attribute |
| `category` | Always `ai-tools` |
| `platform` | Always `[linux, macos]` |
| `icon` | From the `ToolRegistryExport` attribute |
| `status` | From the `ToolRegistryExport` attribute |
| `type` | Always `cli` |
| `author` | Always `Poul Kjeldager` |
| `component` | Always `pks` |
| `usage` | From the `ToolRegistryExport` attribute |
| `examples` | From the `ToolRegistryExport` attribute |

These generated values are independent of this reference page's own frontmatter conventions — `pks tools publish` writes the same fixed `category`/`platform`/`type`/`author`/`component` values for every command it exports, regardless of house style elsewhere on the site.

### Currently tagged commands

Only command classes carrying `[ToolRegistryExport]` are picked up. As of this writing that is three commands, all under `pks claude`: `pks claude backup`, `pks claude limits`, and `pks claude stats`. `pks tools publish` cannot add or infer tagging — a command must first get the attribute added to its class before a `publish` run will generate a page for it.

## Troubleshooting

> **Note.** The write is an unconditional overwrite — `publish` always rewrites `{leaf}.md`, with no diff and no confirmation. A manual edit made directly to a previously generated registry file is silently discarded on the next run.

- **Nothing appears on disk after running the command.** The `tools-registry/` search only looks up to 8 parent directories from the current working directory. Run the command from inside the repository tree that actually contains `tools-registry/`, or move up manually and re-run. Stdout-only output looks identical to a successful run at a glance — check for the printed summary table's file paths, not just its exit code.
- **The command exits 0 with a yellow warning and writes nothing.** This happens when zero command classes carry `[ToolRegistryExport]` in the assembly that ran — for example a stripped-down build. Confirm the attribute is present on the command class you expect to see published.
- **A generated page never appears on agentics.dk/tools.** `publish` only writes local Markdown files. The generated files still need to be committed and the site deployed before the change is live.

## See also

- [pks](/tools/pks) — the root command reference for the full pks CLI
- [pks claude](/tools/pks/claude) — the command family whose `backup`, `limits`, and `stats` subcommands are currently tagged for export
