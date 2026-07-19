---
title: "Authenticate pks with GitHub"
description: "Sign pks in to GitHub with the device-code flow or a personal access token, and grant the Agentics Live app access to the repositories you build."
tags: [quickstart, github, auth, oauth]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github init [repoUrl]"
examples:
  - command: "pks github init"
    description: "Authenticate only, with no repository install step"
  - command: "pks github init https://github.com/owner/repo"
    description: "Authenticate and grant the app access to owner/repo"
  - command: "pks github init --force"
    description: "Re-run the login even if a token is already stored"
  - command: "pks github init --token ghp_xxxxxxxxxxxx"
    description: "Store a personal access token instead of logging in"
---

Get pks holding a working GitHub credential in a few minutes: run the login, approve the device code in a browser, and grant the GitHub App access to the repository you want to build. That credential is what later lets `pks github runner start` claim jobs and lets containers push back to the repository.

This page covers `pks github init` only. The full flag surface for the group is on the [pks github reference](/tools/pks/github/reference).

## 1. Prerequisites

- **A GitHub account** with permission to install a GitHub App on the target repository or its owning organization. Only step 3 needs this; step 2 works with any account.
- **A browser you can reach.** The device-code flow needs one browser session to approve the code. It does not have to be on the same machine.
- **A personal access token**, if you prefer to skip the device-code flow. Required only for the `--token` path in step 2.

## 2. Authenticate

Run the login with no arguments:

```bash
pks github init
```

The command starts GitHub's OAuth device-code flow against the Agentics Live GitHub App (client ID `Iv23liFv43zosMUb8t9y`). It prints a user code and a verification URL, then polls GitHub every five seconds — backing off when GitHub answers `slow_down` — until you approve the code or it expires. The requested scopes are `repo`, `user:email`, and `write:packages`.

When approval lands, the token is stored in the pks credential store under `~/.pks-cli/`. If a valid token is already stored, the command skips the device-code flow and reports that it's already authenticated — but if a repository URL was also given, it still proceeds to step 3 and checks/grants app access to that repository. Add `--force` to log in again anyway.

### Option A — device code (recommended)

```bash
pks github init
```

### Option B — personal access token

```bash
pks github init --token ghp_xxxxxxxxxxxx
```

The `--token` path stores the token and exits. It runs no device-code flow and performs no repository check, even if you also pass a repository URL.

### Option C — your own GitHub App

```bash
pks github init https://github.com/owner/repo --client-id Iv1abcdefgh --app-slug my-app
```

`--client-id` replaces the Agentics Live client ID used for the device-code flow, and `--app-slug` replaces the slug used to build the app's install URL. Pass both when you run your own app.

## 3. Grant repository access

Pass a repository URL to have `pks github init` check that the app can actually see that repository:

```bash
pks github init https://github.com/owner/repo
```

If the app is not installed for that owner, the command opens the app's installation page in your browser and waits for you to press Enter. If the app is installed but scoped to a list of repositories that does not include this one, it opens the installation's settings page instead, waits for Enter, and then re-checks.

Accepted URL forms are `https://github.com/owner/repo`, `http://github.com/owner/repo`, `github.com/owner/repo`, and `git@github.com:owner/repo`, with or without a trailing `.git`. Anything else fails with a parse error.

> **Note.** Once a repository URL triggers the install check, the command blocks on keyboard input. Run it interactively — this branch is not scriptable. Use `pks github init` with no URL, or `--token`, in automation.

## 4. Verify

```bash
pks github status
```

The command reports that pks is authenticated and exits with code 0. If it exits with code 1, no valid token is stored — go back to step 2. For token type, scopes, and expiry, add `--verbose`.

## 5. Options

| Flag | Default | Description |
|---|---|---|
| `-f`, `--force` | `false` | Force re-authentication even when a valid token is already stored. |
| `-t`, `--token <token>` | — | Store a personal access token directly and skip the device-code flow and the repository check. |
| `--client-id <id>` | Agentics Live app | GitHub App or OAuth App client ID to authenticate against. |
| `--app-slug <slug>` | `agentics-live` | GitHub App slug used to build the installation URL. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

The positional `repoUrl` argument is optional. Omitting it authenticates without checking any repository.

## 6. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| The command exits 1 after the code expires. | The device code was never approved in the browser. Re-run `pks github init` for a fresh code. |
| `Could not parse repository URL`. | The URL is not one of the four accepted forms. Use `https://github.com/owner/repo`. |
| The command sits at a prompt and never returns. | The install check is waiting for Enter after opening the browser. Grant access, then press Enter in the terminal. |
| The install check keeps re-opening the settings page. | Access to the target repository was never granted. Add the repository to the installation, or select all repositories. |
| A token was stored, but `pks github runner register` still fails. | `--token` skips the app-install check. The token's identity also needs Admin permission on the repository — see [Register a repository with the runner](/tools/pks/github/runner/register). |

## 7. Next steps

- [Check GitHub authentication status](/tools/pks/github/status) — inspect the token you just stored
- [Register a repository with the runner](/tools/pks/github/runner/register) — the next command in the runner setup path
- [Self-hosted devcontainer runner](/tools/pks/github/runner) — what the credential is ultimately for
- [pks github reference](/tools/pks/github/reference) — every command in the group in one place
