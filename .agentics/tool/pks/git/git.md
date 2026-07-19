---
title: "pks git"
description: "The pks git askpass helper answers Git's GIT_ASKPASS credential prompts for Azure DevOps remotes using pks's stored ADO OAuth token."
tags: [reference, cli, auth]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks git askpass [prompt] [options]"
examples:
  - command: "pks git askpass --install"
    description: "Install the wrapper script and export GIT_ASKPASS"
  - command: "pks git askpass \"Password for 'https://dev.azure.com':\""
    description: "How Git itself invokes the helper (not typed by hand)"
---

`pks git` is a single-purpose branch that answers Git's own `GIT_ASKPASS` credential prompts for Azure DevOps remotes, using an OAuth token pks already holds.

## Overview

The `git` branch exposes exactly one command, `askpass`. Git invokes `GIT_ASKPASS` itself whenever a remote needs a username or password it doesn't already have; `pks git askpass` answers that call on stdout with no decoration, backed by the Azure DevOps OAuth2 credentials `pks ado init` already stored. `--install` performs the one-time wiring instead: it writes a small wrapper script and points `GIT_ASKPASS` at it.

- **Silent by design:** stdout carries only the credential value Git expects — a username string or a token — nothing else.
- **No login of its own:** `askpass` only refreshes an already-stored Azure DevOps refresh token. Initial consent happens in the separate `pks ado init` flow.
- **Not invoked by hand day to day:** once installed, Git calls it automatically on every `dev.azure.com` credential prompt.

## When to use it

Run `pks git askpass --install` once per machine (or once per devcontainer image) so any `git clone`, `fetch`, or `push` against an Azure DevOps remote authenticates silently instead of prompting interactively or requiring a personal access token. This is unrelated to GitHub authentication, which pks handles through its own separate device-code flow, and it is not a general git porcelain — it exists purely as Git's `GIT_ASKPASS` extension point.

## Prerequisites

- **Run `pks ado init` first.** It completes the Azure DevOps OAuth2/PKCE browser login and stores the refresh token `askpass` refreshes from. Without it, `askpass` has no credential to mint and exits `1` with no output.
- **`GIT_ASKPASS` must point at the installed wrapper.** `--install` writes it and exports the variable; without that wiring, Git never calls this command at all.
- **The remote must be an Azure DevOps URL** (`https://dev.azure.com/...` or a classic `*.visualstudio.com` URL) whose prompt text contains the literal word "Username" or "Password" — that substring match is how `askpass` decides what to emit.

## Synopsis

```text
pks git askpass [prompt] [options]
```

```text
askpass    Answer a Git credential prompt for Azure DevOps, or install the GIT_ASKPASS wiring
```

## pks git askpass

Git invokes this command itself, passing the exact prompt text as a single positional argument; you normally never type it. If the prompt contains "Username", it writes the literal string `pks` to stdout — Azure DevOps doesn't care what username accompanies a Bearer-style token, so this is a placeholder, not your account name. If the prompt contains "Password", it refreshes the stored Azure DevOps access token and writes that token to stdout as the password. Any other prompt text, or any error while refreshing, exits `1` with no output — by design, since Git expects a clean, silent failure from an askpass helper. The CLI's startup banner is force-suppressed for this exact command so nothing but the credential ever reaches stdout.

With `--install`, `askpass` instead does one-time local setup: it writes a wrapper script to `~/.local/bin/pks-git-askpass` (a `.cmd` file on Windows) that execs `pks git askpass "$@"`, and appends `export GIT_ASKPASS=...` to the first shell rc file it finds (`.zshrc`, `.bashrc`, `.profile`) — or prints the line to add manually if none exist or `GIT_ASKPASS` is already set.

| Argument | Required | Description |
|---|---|---|
| `prompt` | no | The credential prompt string Git passes verbatim, e.g. `Password for 'https://dev.azure.com':`. Matched case-insensitively for "Username" or "Password". Omitted or empty matches neither branch and exits `1`. |

| Flag | Description |
|---|---|
| `--install` | Installs the `GIT_ASKPASS` wrapper script and attempts to export `GIT_ASKPASS` in the shell rc file. When set, the prompt argument is ignored. |

### Global environment variables

| Variable | Default | Purpose |
|---|---|---|
| `GIT_ASKPASS` | `(unset)` | Git reads this to find the credential-helper executable to call on username/password prompts. `--install` sets it (via a shell rc file, or a `.cmd` wrapper on Windows) to the installed `pks-git-askpass` shim, which execs `pks git askpass "$@"`. |

```bash
pks git askpass --install
```

One-time setup: installs the wrapper script and configures `GIT_ASKPASS` in the shell profile.

```bash
pks git askpass "Password for 'https://dev.azure.com':"
```

How Git itself invokes the helper when prompting for a password against an Azure DevOps remote — not something you normally type by hand.

```bash
GIT_ASKPASS="pks git askpass" git clone https://dev.azure.com/org/project/_git/repo
```

Manual one-off usage without installing the wrapper: point `GIT_ASKPASS` directly at the pks binary for a single clone.

> **Note.** `--install` checks rc files in a fixed priority order — `.zshrc`, then `.bashrc`, then `.profile` — and inspects only the **first one that exists** on disk; it never reads a second or third file, regardless of which shell is actually active. If that one file already contains `GIT_ASKPASS`, it skips writing the export line but still reports the script as reinstalled. Otherwise it appends the export to that file, even if a later file in the list already has the variable set — so re-running after, say, creating a `.zshrc` for the first time (with an older `.bashrc` already exporting `GIT_ASKPASS`) will append a duplicate export to `.zshrc` instead of detecting the existing one.

## Troubleshooting

- **Git prompts interactively instead of using pks.** `GIT_ASKPASS` isn't set, or points somewhere other than the installed wrapper. Run `pks git askpass --install` and open a new shell so the exported variable takes effect.
- **Opaque authentication failure, no error text.** `askpass` exited `1` with no output — the documented failure mode when the stored Azure DevOps refresh token is missing or invalid. Run `pks ado init` (or `pks ado init --force` to re-authenticate) and retry.
- **A Git operation against a non-`dev.azure.com` remote fails the same way.** This helper only understands Azure DevOps prompt wording. For GitHub remotes, use pks's separate GitHub authentication instead.
- **`--install` reports success but `GIT_ASKPASS` still isn't set in the current shell.** The export was written to an rc file, not the live environment. Open a new shell, or source the rc file it wrote to.
- **The wrong username string shows up somewhere in logs or UI.** The username branch always returns the literal `pks`, never your real Azure DevOps identity — this is expected: Azure DevOps accepts an OAuth access token as the password alongside any non-empty username.

## See also

- [pks ado CLI reference](/tools/pks/ado) — runs `pks ado init`, the OAuth2/PKCE login this helper's tokens come from, and the separate `git-proxy` daemon.
