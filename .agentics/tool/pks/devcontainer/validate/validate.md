---
title: "pks devcontainer validate"
description: "Lint an existing devcontainer.json — configuration structure, features, VS Code extensions, forwarded ports, base image, and referenced file paths."
tags: [reference, devcontainer, validation, ci]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer validate [CONFIG-PATH] [options]"
examples:
  - command: "pks devcontainer validate"
    description: "Validate the configuration found in this directory"
  - command: "pks devcontainer validate .devcontainer/devcontainer.json --strict"
    description: "Fail the run on warnings as well as errors"
  - command: "pks devcontainer validate --check-features --check-extensions"
    description: "Run feature and extension checks explicitly"
---

`pks devcontainer validate` parses a `devcontainer.json` and runs several independent checks over it, then prints a per-category summary and a totals panel. It touches no Docker daemon and starts no container, so it is safe to run in CI on every commit.

The exit code is 1 when there are errors, and also when `--strict` is set and there are warnings but no errors. Otherwise it is 0.

## Synopsis

```text
pks devcontainer validate [CONFIG-PATH] [options]
```

`CONFIG-PATH` is optional. When omitted, the command probes three locations under the output path, in order: `.devcontainer/devcontainer.json`, `.devcontainer.json`, `devcontainer.json`. If none exist, it reports against the first.

## What is checked

The validation passes run concurrently and report into named categories.

- **Configuration structure.** Name, image versus build, and the Docker Compose service definition.
- **Features.** Validity, deprecation, conflicts, and missing dependencies, resolved through the feature registry. Controlled by `--check-features`.
- **Extensions.** Existence, compatibility, and dependencies of each VS Code extension. Controlled by `--check-extensions`.
- **Ports.** Range, duplicates, and use of reserved ranges.
- **Base image.** Image name shape, use of the `:latest` tag, and an Ubuntu base suggestion.
- **File paths.** Whether `workspaceFolder` is absolute, and whether a referenced Dockerfile or Compose file exists relative to the configuration.

## Run it

```bash
pks devcontainer validate
```

The command prints one row per category with its error and warning counts, then a totals panel. Categories with no issues are hidden unless `-v` is set.

In CI, treat warnings as failures:

```bash
pks devcontainer validate .devcontainer/devcontainer.json --strict
```

## Verify

```bash
pks devcontainer validate --strict
```

A configuration that is ready to build exits 0 and reports zero errors and zero warnings. Fix errors before running [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — a valid file is a prerequisite, though not a guarantee that the build succeeds.

## Options

| Flag | Default | Description |
|---|---|---|
| `--strict` | `false` | Exit 1 when there are warnings, even with zero errors. |
| `--check-features` | `true` | Validate features: deprecation, conflicts, dependency resolution. |
| `--check-extensions` | `true` | Validate VS Code extensions through the extension service. |
| `-o\|--output-path <PATH>` | current directory | Base directory used to locate the configuration when `CONFIG-PATH` is omitted. |
| `-v\|--verbose` | `false` | Show every category, including clean ones, plus informational messages. |
| `-f\|--force` | `false` | Inherited. Not read by this command. |
| `--dry-run` | `false` | Inherited. Not read by this command. |

## Troubleshooting

**The run fails immediately with a parse error.** Malformed JSON stops validation outright with exit code 1. There is no partial validation of a file that does not parse.

**An extension reports a warning rather than an error.** A failure to validate an individual extension is recorded as a warning, not a hard error, so an unreachable extension service degrades the report instead of failing it.

**`-f` and `--dry-run` appear to do nothing.** Both are inherited from the shared settings base and are not read by this command.

**The wrong file was validated.** Pass `CONFIG-PATH` explicitly, or set `-o` to the directory that contains `.devcontainer/`.

## See also

- [pks devcontainer init](/tools/pks/devcontainer/init) — generate the configuration this command checks
- [pks devcontainer wizard](/tools/pks/devcontainer/wizard) — build a configuration through prompts
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — run the validated configuration as a container
- [pks devcontainer](/tools/pks/devcontainer) — the command group and its mental model
