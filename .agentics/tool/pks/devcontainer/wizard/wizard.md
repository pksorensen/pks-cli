---
title: "pks devcontainer wizard"
description: "Build a devcontainer configuration through guided prompts: template, features, required environment variables, VS Code extensions, and advanced settings."
tags: [how-to, devcontainer, scaffolding]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks devcontainer wizard [options]"
examples:
  - command: "pks devcontainer wizard"
    description: "Run the guided configuration builder"
  - command: "pks devcontainer wizard --quick-setup"
    description: "Minimal prompts with popular features only"
  - command: "pks devcontainer wizard --expert-mode"
    description: "Add ports, environment, and post-create prompts"
  - command: "pks devcontainer wizard --from-templates"
    description: "Also discover templates from NuGet packages"
---

`pks devcontainer wizard` builds a `.devcontainer/devcontainer.json` through prompts instead of flags. It covers everything [pks devcontainer init](/tools/pks/devcontainer/init) does, and adds template discovery from NuGet, prompting for a template's required environment variables, and extension recommendations derived from the features you pick.

The wizard needs an interactive terminal. It uses selection prompts and key input throughout, so it is not scriptable — use `init` in CI.

## Synopsis

```text
pks devcontainer wizard [options]
```

## Prerequisites

- **An interactive terminal.** Selection prompts, multi-selects, and masked input are used at every step.
- **A writable output directory.** The wizard confirms before overwriting an existing configuration, unless `-f` is set.
- **Network access to a NuGet source**, only when `--from-templates` is used.

## Walk through the wizard

Run it from the repository you want to configure:

```bash
pks devcontainer wizard
```

The wizard proceeds through six steps.

1. **Basic settings.** Name the configuration, then pick a template or supply a custom base image. In `--expert-mode` you are also asked whether to use Docker Compose.
2. **Template selection.** Built-in templates are listed. With `--from-templates`, NuGet-discovered templates are added to the list.
3. **Features.** A multi-select over the feature registry. When a template was chosen, the list is scoped to that template's optional features. Categories are browsable.
4. **Environment variables.** Shown only when the selected template declares required environment variables. Values whose name contains `token`, `secret`, `key`, or `password` are read masked, and the wizard offers to reuse a matching variable already exported in your shell.
5. **Extensions.** Recommended VS Code extensions derived from the features you selected, plus manual entry of your own.
6. **Advanced settings.** Only in `--expert-mode`: forwarded ports, environment variables, and a post-create command.

A review step summarizes the configuration and asks for confirmation, then the files are generated the same way `init` generates them.

## Verify

```bash
pks devcontainer validate --strict
```

The configuration the wizard wrote should report zero errors and zero warnings.

## Options

| Flag | Default | Description |
|---|---|---|
| `--skip-templates` | `false` | Skip the template selection step. |
| `--skip-features` | `false` | Skip the feature selection step. |
| `--skip-extensions` | `false` | Skip the extension selection step. |
| `--expert-mode` | `false` | Add the Docker Compose prompt and the advanced settings step. |
| `--quick-setup` | `false` | Minimal prompts. Skips the template-or-image choice, shows at most ten features tagged popular or essential, and skips custom extensions. |
| `--from-templates` | `false` | Also discover templates from NuGet packages. |
| `--sources <SOURCES>` | `https://api.nuget.org/v3/index.json` | Comma-separated NuGet sources used for template discovery. |
| `--add-sources <SOURCES>` | — | Comma-separated NuGet sources appended to the sources above. |
| `--debug` | `false` | Print discovery diagnostics: source list, per-tag search results, exception detail. |
| `-o\|--output-path <PATH>` | current directory | Output directory for the generated files. |
| `-v\|--verbose` | `false` | Show extra tables for templates, features, extensions, and environment variables. |
| `-f\|--force` | `false` | Skip the overwrite confirmation. |
| `--dry-run` | `false` | Run the full wizard, then list the files instead of writing them. |

## Troubleshooting

**No NuGet templates appear with `--from-templates`.** Discovery searches the `pks-devcontainers` tag first, then falls back to the `pks-cli` tag filtered by devcontainer-related keywords in tags, title, and description. On any exception it warns and continues with built-in templates only. Run with `--debug` to see which sources were queried and what each tag returned.

**Step 4 never appears.** Environment-variable prompting only runs when the selected template declares required variables. Most built-in templates declare none.

**The wizard hangs or errors in CI.** It requires a TTY. Use [pks devcontainer init](/tools/pks/devcontainer/init) for non-interactive generation.

**A prompt echoes asterisks.** Variable names containing `token`, `secret`, `key`, or `password` are matched case-insensitively and read as masked input.

## See also

- [pks devcontainer init](/tools/pks/devcontainer/init) — the flag-driven, scriptable equivalent
- [pks devcontainer validate](/tools/pks/devcontainer/validate) — lint the configuration the wizard produced
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — run the configuration in a Docker volume
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
