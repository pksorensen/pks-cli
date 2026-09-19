---
title: "pks typesafe"
description: "Store a TypeSafe (Jev) API key on a runner host, decide which repositories' jobs may spend it, and let stations call the System One API through the credential socket without ever holding the key."
tags: [reference, typesafe, jev, auth, runner, assembly-line]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks typesafe <command> [options]"
examples:
  - command: "pks typesafe init"
    description: "Store and validate a TypeSafe API key (prompted, never in argv)"
  - command: "pks typesafe allow pksorensen/commuteconnects"
    description: "Let that repository's jobs call TypeSafe through the runner's credential socket"
  - command: "pks typesafe ask --file request.json"
    description: "Send one { state, questions } request and print the JSON answer"
---

`pks typesafe` manages the API key for [TypeSafe AI](https://typesafe.ai)'s System One model,
**Jev** — the model that answers typed questions (Noul, Choice, Score) with calibrated
probabilities instead of generating text. The key is stored encrypted on the runner host and
spent on behalf of jobs through the runner's credential socket, so an assembly-line station or a
self-hosted CI job can route on a Jev answer without a `TYPESAFE_API_KEY` anywhere in the container.

## Overview

- **Stored encrypted, validated first.** `init` lists the models the key may call
  (`GET /v1/models`) before writing anything, and the write goes through the `cloud.auth.write`
  two-factor gate like every other provider key.
- **Spent by proxy, not exported.** A job sends its request to `POST /typesafe/systemone` on the
  credential socket with its per-job token; the runner adds the key, forwards to
  `https://api.typesafe.ai/v1/systemone`, and returns the answer. The container only ever sees
  answers.
- **Allow-listed per repository.** `allow owner/repo` names the git repository a job's token was
  minted for. That covers a GitHub Actions job on a self-hosted runner and an assembly-line station
  whose project `gitUrl` points at the repository. Nothing is allowed by default.
- **One default model.** Requests that name no `model` get `jev-latest` (or what `init --model`
  set). Pin a versioned id such as `jev-1.13.0` when thresholds have been tuned against it.

## Commands

| Command | What it does |
|---|---|
| `init [--stdin] [--force] [--model M]` | Prompt for the key (or read stdin), validate, store. `--force` replaces. |
| `status [-v]` | Presence, fingerprint, endpoint, default model, allowed repositories. Never prints the key. |
| `allow owner/repo` / `revoke owner/repo` | Edit the allow-list. Takes effect on the next request. |
| `ask [--file PATH] [--model M]` | Send one request body from a file or stdin and print TypeSafe's JSON. Exit 1 with the error on stderr otherwise. |
| `remove` | Delete the stored key. The allow-list is kept. |

### Request body for `ask`

The body is TypeSafe's own, so a file that works here works from a station unchanged:

```json
{
  "state": { "files_changed": 3, "touches_migrations": false, "ci": "success" },
  "questions": {
    "risk": {
      "type": "choice",
      "instructions": "How risky is merging this pull request without a person reading it?",
      "criteria": { "low": "Docs, tests, CI config", "medium": "Product code without schema or auth changes", "high": "Auth, payments, migrations, deploy" }
    },
    "needs_human": { "type": "noul", "instructions": "Should a person review this before merge?" }
  }
}
```

## From inside a job

Both routes live on the credential socket every runner container already has
(`/var/run/pks-creds/creds.sock`) and take the job's own token (`PKS_TOKEN`):

```bash
# Preferred: the key never enters the container.
curl -s --unix-socket /var/run/pks-creds/creds.sock \
  -H "Authorization: Bearer $PKS_TOKEN" -H "Content-Type: application/json" \
  --data @request.json http://localhost/typesafe/systemone

# Only when an SDK must hold the key itself.
curl -s --unix-socket /var/run/pks-creds/creds.sock \
  -H "Authorization: Bearer $PKS_TOKEN" http://localhost/typesafe/token
#  → { "apiKey": "…", "model": "jev-latest", "baseUrl": "https://api.typesafe.ai" }
```

| Status | Meaning |
|---|---|
| `401` | No or invalid job token. |
| `403` | The job's repository is not on the allow-list — `pks typesafe allow owner/repo` on the host. |
| `404` | No key stored — `pks typesafe init` on the host. |
| `422` / `429` / `529` | TypeSafe's own answer, passed through with its body. |
| `502` | The **host's** key was rejected, or TypeSafe was unreachable. Not a job-token problem. |
| `503` | The runner was started without the TypeSafe service wired in. |

## Where it is used

The `alp-pr-review` assembly line's `route` station sends the review station's structured verdict
to Jev through this proxy and turns the answer into `success` (merge) or `failure` (a person
looks first). The questions and thresholds live in that line's `tools/jev-questions.mjs`, not
in pks-cli.

## Environment

| Variable | Where | Effect |
|---|---|---|
| `TYPESAFE_BASE_URL` | Runner host | Overrides `https://api.typesafe.ai` for both validation and the proxy. |
| `PKS_TOKEN` | Job container | The per-job token the socket routes require; the runner sets it. |
