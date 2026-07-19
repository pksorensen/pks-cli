---
title: "pks report"
description: "Package a bug report, feature request, or question into a GitHub issue with CLI version, environment, and telemetry details pre-filled automatically."
tags: [reference, cli, github, feedback]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks report [MESSAGE] [options]"
examples:
  - command: "pks report \"Found a bug in the init command\""
    description: "Report an issue, prompted for title and type"
  - command: "pks report --bug \"Application crashes when saving\""
    description: "Pre-tag the issue as a bug"
  - command: "pks report --feature \"Add dark mode support\""
    description: "Pre-tag the issue as a feature request"
  - command: "pks report --dry-run \"Preview what the report looks like\""
    description: "Preview the issue without posting or authenticating"
  - command: "pks report --repo myorg/myrepo --question \"Does this support X?\""
    description: "File the issue against a different repository"
---

`pks report` is a single leaf command — no subcommands — that turns your feedback into a formatted GitHub issue on the `pks-cli` repository, or wherever you point it with `--repo`. It collects a message, a title, and an issue type (bug, feature, question, or general), then assembles a Markdown issue body from up to three optional sections: CLI version info, environment/system diagnostics, and anonymized local telemetry stats.

## Overview
`pks report` exists so maintainers don't have to ask "what version and OS are you on" in every issue thread. Any of the message, title, or type can be omitted from the command line — the command falls back to interactive Spectre prompts for whichever piece is missing, so it works both as a scriptable one-liner and as a guided wizard.

- **Interactive by default.** Run `pks report` with no arguments and it prompts for everything.
- **Scriptable with flags.** Supply the message as a positional argument and `--title` plus one of `--bug`/`--feature`/`--question` to skip all three prompts.
- **Safe to preview.** `--dry-run` renders the full issue content locally and requires no GitHub authentication.

## What you get
- **A pre-populated GitHub issue body.** A "## User Report" section with your message, plus optional Version Information, Environment Information, and Usage Statistics sections.
- **Automatic labeling.** Every issue always gets the base `pks-cli-report` label, plus one type label: `--bug` adds `bug`, `--feature` adds `enhancement`, `--question` adds `question`, and choosing "General Feedback" in the interactive prompt yields `feedback`.
- **A dry-run preview.** `--dry-run` shows the repo, title, labels, and full body in a console panel instead of posting — no GitHub token needed for this path.
- **A configurable target repo.** `--repo owner/repository` files the issue anywhere you have issue-create access, defaulting to `pksorensen/pks-cli`.

## How it fits together
`pks report` delegates GitHub issue creation and auth-token lookup to the same `IGitHubService` / `IGitHubAuthenticationService` / `IConfigurationService` infrastructure used by the [pks github](/tools/pks/github) command group, so a token established with `pks github init` is picked up automatically. Authentication is only checked when actually creating an issue — `--dry-run` skips it entirely, which makes it the safe first step on a machine with sensitive local details.

The three optional sections are each backed by a different subsystem: version info comes from the assembly metadata baked into the build, environment info is gathered live (OS, .NET runtime, Docker/Git/Node/Kubernetes presence), and telemetry pulls from the CLI's local, in-memory-then-summarized usage counters.

## Prerequisites
- **A GitHub token**, for any non-dry-run submission — either configuration key `github.token` or a stored OAuth token from `pks github init`. Run [pks github init](/tools/pks/github) first if you haven't authenticated.
- **Network access to GitHub's API**, to create the issue.
- **Write/issue-create access on the target repo** — the default `pksorensen/pks-cli`, or whatever you pass to `--repo`.

## Synopsis

```text
pks report [MESSAGE] [options]
```

```text
report    Create or preview a GitHub issue bundling feedback, version, environment, and telemetry info
```

## Arguments

| Argument | Description |
|---|---|
| `MESSAGE` | The feedback text included under "## User Report" in the issue body. Optional; if omitted, the command interactively prompts "What would you like to report or share?" |

## Options

| Flag | Default | Description |
|---|---|---|
| `-t, --title <TITLE>` | — | Title for the issue. If omitted, the command shows a generated default (for example `"Bug Report: "`) and still prompts interactively for the real title — it never silently uses the default. |
| `--bug` | `false` | Marks this as a bug report and adds the `bug` label (on top of the base `pks-cli-report` label every issue gets). |
| `--feature` | `false` | Marks this as a feature request and adds the `enhancement` label (on top of the base `pks-cli-report` label every issue gets). |
| `--question` | `false` | Marks this as a question and adds the `question` label (on top of the base `pks-cli-report` label every issue gets). If none of `--bug`, `--feature`, or `--question` is passed, the command prompts you to choose "Bug Report," "Feature Request," "Question," or "General Feedback" (the last adds the `feedback` label). |
| `--include-telemetry` | `true` | Includes a Usage Statistics section: total commands run, most-used command, days active, total errors, top-5 command usage counts, template usage, and which feature areas (agentic features, MCP integration, devcontainers, GitHub integration, Kubernetes deployment) have been used. If telemetry is disabled locally, the section states that instead. |
| `--include-environment` | `true` | Includes an Environment Information section: OS description and architecture, logical core count, WSL detection, current shell, .NET runtime/framework/RID/target framework, process architecture, and presence and version of Docker, Git, Node.js, and Kubernetes tooling. |
| `--include-version` | `true` | Includes a Version Information section: `pks` CLI version, assembly version, product version, git commit, build date (UTC), and build configuration. |
| `--dry-run` | `false` | Previews the assembled issue (repo, title, labels, body) in a console panel instead of creating it. Does not require GitHub authentication. The previewed issue URL is a template link (`https://github.com/{repo}/issues/new`), not a real created issue. |
| `--repo <REPOSITORY>` | `pksorensen/pks-cli` | Target repository in `owner/repository` form. Must contain exactly one `/`; anything else fails with `Invalid repository format. Expected format: owner/repository`. Changes only where the issue is filed, not the auth token used. |

## Examples

```bash
pks report "Found a bug in the init command"
```

Prompts for a title and, since no type flag was given, an issue type.

```bash
pks report --bug "Application crashes when saving"
```

Skips the type-selection prompt and tags the issue `bug`.

```bash
pks report --dry-run "Preview what the report looks like"
```

Renders the full issue content locally without posting or requiring GitHub auth.

```bash
pks report --repo myorg/myrepo --question "Does this support X?"
```

Files (or previews) the issue against a non-default repository.

## Troubleshooting

**"GitHub authentication is required to create reports. Please configure your GitHub token."** No usable token was found via configuration key `github.token` or the stored OAuth token. Run [pks github init](/tools/pks/github) to authenticate, or add `--dry-run` to preview without posting.

**A plain `pks report` sends more than expected.** `--include-environment` and `--include-telemetry` both default to `true`, so a bare submission discloses OS/runtime/tooling details and anonymized local usage counts in the issue body. Pass `--include-environment=false` and/or `--include-telemetry=false`, or run with `--dry-run` first to see exactly what would be sent.

**"Invalid repository format. Expected format: owner/repository"** `--repo` only accepts exactly one `/` — no URLs, no trailing slash, no missing owner. Pass a plain `owner/repository` string.

**An issue was created immediately after the prompts, with no separate confirmation step.** This is expected: without `--dry-run`, `pks report` creates the GitHub issue right after the message/title/type prompts are answered. Use `--dry-run` first if you want a chance to review before anything is posted.

**The command hangs or blocks in a script.** If message, title, and type are all omitted, `pks report` becomes fully interactive across three prompts and is not suitable for unattended use. Supply `MESSAGE`, `--title`, and one of `--bug`/`--feature`/`--question` up front.

## See also

- [pks github](/tools/pks/github) — the GitHub integration commands that share `pks report`'s underlying auth and API services
