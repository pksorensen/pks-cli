---
title: "pks agentics runner register"
description: "Register this machine as a named runner for one owner/project, store its bearer token, and confirm the runner can clone the project repository."
tags: [how-to, runner, registration, github]
category: infrastructure
platform: [linux, macos, windows]
icon: user-plus
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics runner register <OWNER_PROJECT> [options]"
examples:
  - command: "pks agentics runner register myorg/myproject"
    description: "Register using the hostname as the runner name"
  - command: "pks agentics runner register myorg/myproject --name my-runner"
    description: "Register under an explicit runner name"
  - command: "pks agentics runner register myorg/myproject --server localhost:3000"
    description: "Register against a local development server"
---

Registration is the one-time step that binds a machine to a single Assembly Line Platform project. It creates the runner record on the server, saves the returned bearer token locally, and makes sure this machine can clone the project's repository before any job depends on it.

## 1. Prerequisites

- **A project on the server** in `owner/project` form. The argument must contain a `/`; a bare project name is rejected.
- **Network access to the server.** Default is agentics.dk.
- **A GitHub account with access to the repository**, if the project's git URL is on github.com. The command runs a device-code login for you.

## 2. Register the machine

```bash
pks agentics runner register myorg/myproject
```

The command posts to `{server}/api/owners/{owner}/projects/{project}/runners` and saves the runner id, name, and token locally. The runner name defaults to the machine hostname; override it with `--name`.

Registration labels are always `self-hosted` plus the operating system (`windows`, `macos`, `linux`, or `unknown`). Those labels are what let the server target this runner.

> **Do not commit.** The success table prints the runner token in plaintext. Avoid running this in a recorded or shared terminal.

## 3. Choose the server

Server resolution follows a fixed priority: the `--server` flag, then `AGENTICS_SERVER`, then `AGENTIC_SERVER`, then `agentics.dk`. A bare host starting with `localhost` or `127.0.0.1` gets `http://`; anything else gets `https://`.

```bash
pks agentics runner register myorg/myproject --server localhost:3000
```

## 4. Complete the GitHub login

If the project's git URL points at github.com, the command fetches that URL and starts a GitHub device-code login — skipped when you are already authenticated. Follow the printed URL and code.

Afterwards it calls the GitHub API with the stored token to verify access to that exact repository. If the token cannot reach it, the output includes an install link for the GitHub App.

## 5. Verify

Re-run the command. It is idempotent per project, so a second run is the way to retry a skipped or failed GitHub step.

```bash
pks agentics runner register myorg/myproject
```

You should see the registration table with a runner id, name, and token, and no GitHub access warning.

## Options

| Flag | Default | Description |
|---|---|---|
| `--name <NAME>` | machine hostname | Runner name recorded on the server. |
| `--server <SERVER>` | `agentics.dk` | Agentics server URL. Falls back to `AGENTICS_SERVER`, then `AGENTIC_SERVER`. |
| `-v`, `--verbose` | — | Enable verbose output. |

### Argument

| Argument | Required | Description |
|---|---|---|
| `OWNER_PROJECT` | yes | Target project in `owner/project` form. |

## Troubleshooting

- **"must be specified in owner/project format".** The argument had no `/`. Pass both segments.
- **No GitHub prompt appeared.** The project's git URL is not on github.com, or you are already authenticated. Self-hosted git projects never trigger the login.
- **The GitHub access check says nothing at all.** A transient network failure against the GitHub API is swallowed rather than reported. Re-run the command to get a definitive answer.
- **The runner registers but jobs never arrive.** Check that the server targets the `self-hosted` label and the operating-system label this machine reported.

## See also

- [Start the Agentics runner daemon](/tools/pks/agentics/runner/start) — the next command to run after registering
- [pks agentics runner](/tools/pks/agentics/runner) — how registration, execution, and handoff fit together
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
