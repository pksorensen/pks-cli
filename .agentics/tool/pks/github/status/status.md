---
title: "Check GitHub authentication status"
description: "Confirm that pks holds a valid GitHub token, and inspect its type, scopes, and expiry before a runner job needs to push back to a repository."
tags: [how-to, github, auth, troubleshooting]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks github status [options]"
examples:
  - command: "pks github status"
    description: "Quick authenticated or not-authenticated check"
  - command: "pks github status --verbose"
    description: "Show token type, scopes, created and expiry dates"
---

`pks github status` answers one question: does pks currently hold a valid GitHub token? That token is what decides whether the runner daemon announces the `git:push` capability, so this is the first command to run when a job fails to push.

## 1. Prerequisites

- **A stored token**, from [`pks github init`](/tools/pks/github/init). Without one the command still runs — it reports that pks is not authenticated and exits 1.

## 2. Run the check

```bash
pks github status
```

The command reports whether a valid token is stored. It exits with code 0 when authenticated and code 1 when not, so it works as a gate in a script:

```bash
pks github status && pks github runner start
```

## 3. Inspect the token

```bash
pks github status --verbose
```

Verbose output adds a table describing the stored token:

- **Type** — personal access token or OAuth token, detected from the `ghp_` prefix.
- **Scopes** — the scopes the token carries.
- **Created** and **Expires** — the token's lifetime.
- **Valid** — whether the token still passes validation.

## 4. Options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Add a table with token type, scopes, created and expiry dates, and validity. |

## 5. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Exit code 1 and a not-authenticated message. | No valid token is stored. Run `pks github init`. |
| Reports authenticated, but a job still fails to push with 403. | The token is valid but its identity lacks access to that repository. Store a personal access token with `pks github init --token ghp_xxxxxxxxxxxx`, or grant the app access to the repository. |
| Reports authenticated, but `pks github runner register` rejects the repository. | This command checks the token only. It never checks whether the GitHub App is installed on a specific repository — `pks github init <repoUrl>` and `pks github runner register` do that. |
| Expiry is in the past. | Re-run `pks github init --force`. `pks github runner start` also refreshes the token on startup. |

## 6. Next steps

- [Authenticate pks with GitHub](/tools/pks/github/init) — obtain or replace the stored token
- [Register a repository with the runner](/tools/pks/github/runner/register) — where repository-level access is verified
- [Run the runner daemon](/tools/pks/github/runner/start) — the pre-flight checks that repeat this one
- [pks github reference](/tools/pks/github/reference) — every command in the group in one place
