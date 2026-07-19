---
title: "pks devcontainer init"
description: "Generate a .devcontainer/devcontainer.json non-interactively from flags — template, features, extensions, ports, and forwarded environment variables."
tags: [reference, devcontainer, scaffolding]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks devcontainer init [NAME] [options]"
examples:
  - command: "pks devcontainer init MyProject"
    description: "Generate a configuration named MyProject"
  - command: "pks devcontainer init MyProject --features dotnet,docker-in-docker"
    description: "Add two devcontainer features"
  - command: "pks devcontainer init MyProject --template dotnet-web --force"
    description: "Use a template and overwrite an existing file"
  - command: "pks devcontainer init MyProject --dry-run"
    description: "Preview the configuration without writing files"
---

`pks devcontainer init` writes a new `.devcontainer/devcontainer.json` (plus a README, and a `docker-compose.yml` when asked) into an output directory. It is file generation only — no Docker daemon, no container, no VS Code.

Use it when you know what you want and can express it in flags, including from a script or a CI job. For a prompt-driven path, use [pks devcontainer wizard](/tools/pks/devcontainer/wizard).

## Synopsis

```text
pks devcontainer init [NAME] [options]
```

`NAME` is required outside interactive mode. Omitting it always aborts the command with `Project name is required`, even though the underlying options model has an unreachable fallback to the output directory's folder name.

## What it does

0. Before validating anything else, the command checks whether `.devcontainer/devcontainer.json` already exists in the output directory. If it does and `--force` was not passed, the command exits immediately with `Devcontainer already exists` — before the template, feature, port, or env-var checks below ever run.

Then it resolves and checks the remaining inputs:

1. The requested template is looked up in the template service. An unknown template is an error.
2. Each requested feature is looked up in the feature registry, using the raw value you passed. An unknown feature is an error — this includes an `id@version` value, since the version suffix is *not* stripped before this lookup and fails unless a feature happens to be registered under that exact compound id.
3. Feature dependencies and conflicts are resolved. Missing dependency features are added automatically and a warning is printed.
4. Ports are parsed; a non-numeric entry produces a warning and is skipped.
5. Environment variables are parsed as `KEY=VALUE`; an entry without `=` produces a warning and is skipped.

Only after all of the above succeeds is the configuration written.

## Generate a configuration

```bash
pks devcontainer init MyProject --template dotnet-web --features docker-in-docker --ports 5000,5001
```

The command writes `.devcontainer/devcontainer.json` and a README into the output directory, and reports each generated file.

Preview first when you are unsure about a template or feature set:

```bash
pks devcontainer init MyProject --template dotnet-web --dry-run
```

`--dry-run` prints a configuration preview panel and the list of files that would be generated, and writes nothing.

## Verify

Lint the result before you try to build it:

```bash
pks devcontainer validate --strict
```

A clean run reports zero errors and zero warnings. Anything else is described in [pks devcontainer validate](/tools/pks/devcontainer/validate).

## Options

| Flag | Default | Description |
|---|---|---|
| `-t\|--template <TEMPLATE>` | — | Template to initialize from. Must resolve in the template service. |
| `--image <IMAGE>` | — | Base container image to use. |
| `--features <FEATURES>` | — | Comma-separated feature IDs. Must match a registered feature `id` exactly — an `id@version` value fails lookup (`Feature '<id>@<version>' not found`) unless that exact compound string is itself a registered id. |
| `--extensions <EXTENSIONS>` | — | Comma-separated VS Code extension IDs. |
| `--ports <PORTS>` | — | Comma-separated ports to forward. |
| `--post-create-command <COMMAND>` | — | Command written to `postCreateCommand`. |
| `--docker-compose` | `false` | Use Docker Compose and also generate `docker-compose.yml`. |
| `--workspace-folder <FOLDER>` | — | Workspace folder path inside the container. |
| `--env <ENV>` | — | Environment variables in `KEY=VALUE` form. |
| `--include-dev-packages` | `true` | Include development packages and tools. |
| `--git-credentials` | `true` | Enable Git credential sharing. |
| `-i\|--interactive` | `false` | Prints a pointer to the wizard and exits. See troubleshooting. |
| `--generate-files <FILES>` | — | Accepted but not read by the command. See troubleshooting. |
| `-o\|--output-path <PATH>` | current directory | Output directory for the generated files. |
| `-v\|--verbose` | `false` | Verbose output, including a stack trace on error. |
| `-f\|--force` | `false` | Overwrite an existing configuration. |
| `--dry-run` | `false` | Print a preview and generate nothing. |

## Troubleshooting

**`Devcontainer already exists`.** A `.devcontainer/devcontainer.json` is present in the output directory. Pass `--force` to overwrite it, or point `-o` at a different directory.

**An unknown template or feature aborts the run.** Both are validated against their registries before any file is written, so a typo fails fast and leaves the directory untouched.

**`-i\|--interactive` does not prompt.** It prints guidance to run `pks devcontainer wizard` and exits with code 0. Run the wizard directly instead.

**`--generate-files` has no effect.** The option is declared on the settings class but is never read while building the configuration. Setting it changes nothing.

**A feature you did not request appears in the output.** Dependency resolution adds missing prerequisite features and warns about each one.

## See also

- [pks devcontainer wizard](/tools/pks/devcontainer/wizard) — the same configuration built through prompts, with template-declared environment variables
- [pks devcontainer validate](/tools/pks/devcontainer/validate) — check the generated file for errors and warnings
- [pks devcontainer spawn](/tools/pks/devcontainer/spawn) — run the configuration as a real container
- [pks devcontainer](/tools/pks/devcontainer) — how the configuration and container commands fit together
