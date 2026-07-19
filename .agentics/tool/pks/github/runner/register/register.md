---
title: "Register a repository with the runner"
description: "Add a repository to the pks runner store: admin-permission check, Agentics Live app access polling, custom labels, and duplicate replacement."
tags: [how-to, github, runner, registration]
category: infrastructure
icon: user-plus
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github runner register <REPO> [options]"
examples:
  - command: "pks github runner register owner/repo"
    description: "Register owner/repo with the default runner label"
  - command: "pks github runner register owner/repo --labels custom-label"
    description: "Register with a custom label instead of the default"
  - command: "pks github runner list"
    description: "Confirm the registration was stored"
  - command: "pks github runner unregister owner/repo"
    description: "Remove the registration again"
---

Registration tells the pks runner daemon which repositories to poll. `pks github runner register owner/repo` checks that your GitHub identity is allowed to run a runner there, then writes a local record — nothing is created on GitHub's side until a job is claimed.

## 1. Prerequisites

- **A stored GitHub token.** If pks is not authenticated, the command runs the device-code login inline before continuing. See [Authenticate pks with GitHub](/tools/pks/github/init).
- **Admin permission on the repository.** Push access is not enough. GitHub's just-in-time runner registration tokens require admin-level access, and the command fails without it.
- **App access to the repository**, if you authenticated through the Agentics Live GitHub App rather than a personal access token.

## 2. Register the repository

```bash
pks github runner register owner/repo
```

The argument must be in `owner/repo` form and must contain a `/`. Anything else fails the format check.

The command runs three checks in order:

1. **Authentication.** A missing or invalid token triggers an inline device-code login.
2. **Repository access.** If the repository comes back as inaccessible, the command prints the app's installation/target-selection URL and polls every five seconds for up to five minutes, waiting for access to appear — you must open the printed link yourself.
3. **Admin permission.** The authenticated identity must hold Admin on the repository. Anything less ends the command.

If all three pass, the registration is written to `~/.pks-cli/runners.json` with the label `devcontainer-runner`.

## 3. Choose labels

```bash
pks github runner register owner/repo --labels gpu,large
```

`--labels` takes a comma-separated list and replaces the default entirely. The labels decide which workflow jobs this registration claims, so they must match the `runs-on` labels in the repository's workflow files.

## 4. Handle an existing registration

If a registration already exists for the same `owner/repo`, the command asks whether to replace it. The prompt defaults to **No**:

- Declining exits with code 0 and changes nothing.
- Confirming deletes every existing registration for that repository, then writes the new one.

To clean up duplicates that earlier runs left behind without registering again, use `pks github runner prune`, which keeps only the most recent registration per repository.

## 5. Verify

```bash
pks github runner list
```

The table shows one row per registration, with a truncated ID, the repository, its labels, when it was registered, and whether it is enabled. Your repository should appear with the labels you chose.

## 6. Options

| Flag | Default | Description |
|---|---|---|
| `--labels <LABELS>` | `devcontainer-runner` | Comma-separated runner labels, replacing the default. |
| `-v`, `--verbose` | `false` | Print the raw repository and permission API response detail during the access check. |

The positional `REPO` argument is required and must be in `owner/repo` format.

## 7. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `must be specified in owner/repo format`. | The argument contained no `/`, or one side was empty. Pass `owner/repo`, not a URL. |
| The command reports missing Admin permission. | Push or maintain access is not enough for runner registration. Ask a repository admin, or register from an account that holds Admin. |
| Access polling runs the full five minutes and gives up. | The app was never granted access to that repository. Open the printed installation link, grant access, then re-run the command. |
| The repository is registered, but jobs are never claimed. | The workflow's `runs-on` labels do not match the registration's labels. Re-register with matching `--labels`. |
| Several rows for the same repository in `runner list`. | Earlier registration attempts left duplicates. Run `pks github runner prune`. |
| Removing a registration reports `No registration found`. | `unregister` matches on the repository string, not the truncated ID shown in the list. Pass `owner/repo`. |

## 8. Next steps

- [Run the runner daemon](/tools/pks/github/runner/start) — start polling the repository you just registered
- [Self-hosted devcontainer runner](/tools/pks/github/runner) — how registration, labels, and the daemon relate
- [Authenticate pks with GitHub](/tools/pks/github/init) — fix an inline login that failed
- [pks github reference](/tools/pks/github/reference) — `unregister`, `list`, and `prune` in full
