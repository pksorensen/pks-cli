---
title: "pks deploy"
description: "Reference for pks deploy, a cosmetic Spectre.Console demo of a deployment flow with no build, registry push, or cluster call behind it."
tags: [reference, cli, demo]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks deploy [options]"
examples:
  - command: "pks deploy"
    description: "Run the simulated flow against the default dev environment"
  - command: "pks deploy --environment staging --replicas 5"
    description: "Same flow with different labels in the printed panel"
  - command: "pks deploy --watch"
    description: "Add a 20-second live metrics table after the results panel"
---

`pks deploy` renders a scripted, cosmetic deployment sequence — a pre-flight checks table, four animated progress bars, a results panel, and an optional live metrics view — using Spectre.Console's UI primitives. It does not build code, does not push to a container registry, and does not talk to Kubernetes, Coolify, Azure, or any cluster API.

## Overview

`pks deploy` is a single leaf command (`DeployCommand`, registered in `Program.cs` with only a description, no `.WithExample()` or `.WithAlias()` entries). Every step in its output is either hardcoded or driven by `Random` and `Thread.Sleep` calls:

- The pre-deployment checks table always shows four green rows, including the literal string `"127/127 tests passing"` — no test suite, security scan, or dependency check actually runs.
- The build/push/deploy/health progress bars advance on timed increments with no underlying build, registry push, or network call.
- The results panel always reports success, a fixed `~42ms average` response time, and fabricated endpoint URLs (`https://api-{environment}.example.com`, `https://web-{environment}.example.com`) that are not real or resolvable.
- `Execute()` always returns `0` — there is no failure path, because nothing real is attempted that could fail.

## When to use it

Use `pks deploy` only to script a terminal recording or demo of what a deployment CLI experience could look like — progress bars, a checks table, a results panel, a live metrics view — for product screenshots, GIFs, or as a Spectre.Console UI reference. Do not run it expecting any real infrastructure change: nothing is built or pushed anywhere, and no service is contacted.

For an actual deployment need in this CLI, use one of these instead:

- **[pks coolify](/tools/pks/coolify)** — registers a real Coolify instance for application deployment.
- **[pks ssh](/tools/pks/ssh)** — registers real SSH remote targets, including for devcontainer deployment.

## Prerequisites

None. `pks deploy` has no external dependencies, no authentication, no network calls, and no file I/O — it runs standalone on any machine with `pks` installed.

## Synopsis

```text
pks deploy [options]
```

```text
deploy    Runs a scripted, simulated deployment sequence for demo purposes
```

`pks deploy` has no subcommands.

## Options

| Flag | Default | Description |
|---|---|---|
| `-e, --environment <ENV>` | `dev` | Target environment label. Accepts any string — the code only uppercases it for display (`settings.Environment.ToUpper()`); there is no validation restricting it to `dev`/`staging`/`prod` despite the flag description implying those three. |
| `-w, --watch` | `false` | After the results panel, run a fixed 20-second live-refreshing metrics table. |
| `--ai-optimize` | `false` | Print two extra lines, including a fixed `"23% faster startup, 15% less memory usage"` claim. No model or service call is made. |
| `-r, --replicas <COUNT>` | `3` | Echoed into the results panel as `"{Replicas} instances running"`. Does not affect any real scaling — no orchestrator call exists to scale. |

## Examples

```bash
pks deploy
```

Runs the simulated flow against the default `dev` environment with 3 replicas: a green checks table, four animated progress bars, then a results panel with fabricated endpoint URLs and a fixed `~42ms` response time.

```bash
pks deploy --environment staging --replicas 5
```

Same simulated flow, but the results panel echoes `STAGING` and `5 instances running`. `--environment` and `--replicas` are cosmetic string substitutions here, not real targeting or scaling parameters.

```bash
pks deploy --ai-optimize
```

Adds `"AI optimization enabled - analyzing optimal deployment strategy..."` and the fixed `"23% faster startup, 15% less memory usage"` line to the results panel. No AI or model call happens anywhere in the command.

```bash
pks deploy --watch
```

After the results panel, enters a 20-iteration (about 20 seconds), 1-second-interval live table of `Random`-generated CPU, memory, requests, and status values for three fictitious services — API Gateway, Database, and Cache — then exits on its own.

## Troubleshooting

> **Note.** `pks deploy` cannot fail. `Execute()` always returns `0` because no real build, push, or deployment is attempted — there is nothing for the command to fail at. A green results panel is not evidence that any application was deployed.

**"The printed endpoint URLs don't resolve."** They are literal placeholder text (`https://api-{environment}.example.com`, `https://web-{environment}.example.com`), not real or project-specific URLs. Do not copy them into user-facing output or documentation as if they were real.

**"`--watch` didn't respond to Ctrl+C the way the on-screen hint suggested."** The watch loop is bounded to 20 iterations and exits automatically; it was never designed to run indefinitely, so there is no graceful-stop behavior to trigger. Ctrl+C during the loop kills the process, as it would for any command.

**"I need `pks deploy` to actually deploy something."** It never will — there is no build, registry push, or cluster call in its implementation. Use [pks coolify](/tools/pks/coolify) to register a real Coolify instance, or [pks ssh](/tools/pks/ssh) to register a real SSH target for devcontainer deployment.

## See also

- [pks coolify](/tools/pks/coolify) — the command group that actually deploys applications, via a registered Coolify instance.
- [pks ssh](/tools/pks/ssh) — register real SSH remote targets, including for devcontainer deployment.
- [pks](/tools/pks) — the full command surface this group belongs to.
