---
title: "pks agentics task submit"
description: "File a task onto an assembly line stage from a script or a failing GitHub Actions job, with workflow context and job logs attached automatically."
tags: [how-to, alp, ci-cd, github-actions]
category: infrastructure
platform: [linux, macos, windows]
icon: send
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics task submit --assembly-line-url <URL> --title <TITLE> [options]"
examples:
  - command: "pks agentics task submit --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id --title \"Fix failing tests\""
    description: "File a task on the stage's first column"
  - command: "pks agentics task submit --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id --title \"CI Failure\" --description \"Build step failed\""
    description: "Attach your own error output to the task"
  - command: "pks agentics task submit --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id --title \"Flaky e2e\" --priority medium --labels ci,flaky"
    description: "Set priority and labels on the new task"
---

`pks agentics task submit` puts a task onto a specific Assembly Line Platform stage. Its main use is a pipeline that files its own follow-up work: a build breaks, the job calls this command, and a task appears on the line with the failure already written into the description.

## 1. Prerequisites

- **An assembly-line URL** in the exact shape `https://<host>/p/{owner}/{project}/assembly-lines/{stageId}`. Any other path shape fails to parse.
- **Credentials** from one of the sources in step 4.
- **`permissions: id-token: write`** on the workflow job, if you rely on GitHub OIDC (OpenID Connect workload identity), and a project that trusts the calling repository.

## 2. Submit a task

```bash
pks agentics task submit \
  --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id \
  --title "Fix failing tests"
```

The command parses owner, project, and stage id out of the URL, resolves a token, and posts the title, description, column id, priority, and labels to `{server}/api/owners/{owner}/projects/{project}/assembly-lines/{stageId}/tasks`.

Both `--assembly-line-url` and `--title` are required. Omitting either prints a red error and exits 1.

## 3. Choose column, priority, and labels

Without `--column-id`, the task lands in the stage's first column.

```bash
pks agentics task submit \
  --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id \
  --title "Flaky e2e suite" \
  --description "Playwright run timed out on the login spec" \
  --column-id backlog \
  --priority medium \
  --labels ci,flaky
```

Priority is `low`, `medium`, or `high`, defaulting to `high`. Labels are comma-separated.

## 4. Understand the auth chain

The token is resolved in this order, first hit wins:

1. **`--token`** — an explicit bearer token, skipping the chain. Useful for one-off scripts.
2. **GitHub OIDC** — used when `GITHUB_ACTIONS` is `true`, the job has `id-token: write`, and the project trusts the calling repository.
3. **The stored user token** — from [`pks agentics init`](/tools/pks/agentics/init), refreshed in place if expired.
4. **A matching runner registration's token** — from [`pks agentics runner register`](/tools/pks/agentics/runner/register), used only when no stored user token is available (or its refresh fails).

If nothing resolves, the error names all three remedies.

## 5. Run it from GitHub Actions

Inside GitHub Actions the command adds a `## CI/CD Failure` block to the description containing the workflow, job, commit, actor, and trigger. When a stored GitHub token is available it also appends the failed step names and the last 100 lines of that job's log, fetched from the GitHub Actions API.

```yaml
permissions:
  id-token: write
  contents: read
steps:
  - if: failure()
    run: |
      pks agentics task submit \
        --assembly-line-url https://agentics.dk/p/owner/project/assembly-lines/stage-id \
        --title "CI failure on ${{ github.ref_name }}"
```

The enrichment reads the standard runner variables: `GITHUB_REPOSITORY`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, `GITHUB_JOB`, `GITHUB_WORKFLOW`, `GITHUB_SHA`, `GITHUB_REF_NAME`, `GITHUB_ACTOR`, `GITHUB_EVENT_NAME`, and `GITHUB_SERVER_URL`.

## 6. Verify

Open the assembly line in the browser. The task appears in the target column with your title, priority, and labels, and — from a failing Actions job — the CI/CD failure block in its description.

## Options

| Flag | Required | Description |
|---|---|---|
| `--assembly-line-url <URL>` | yes | Full URL to the assembly line, in the form `/p/{owner}/{project}/assembly-lines/{stageId}`. |
| `--title <TITLE>` | yes | Task title. |
| `--description <TEXT>` | no | Task description or raw error output. |
| `--column-id <ID>` | no | Target column id. Defaults to the stage's first column. |
| `--priority <PRIORITY>` | no | Task priority: `low`, `medium`, or `high`. Defaults to `high`. |
| `--labels <LABELS>` | no | Comma-separated labels to apply. |
| `--server <URL>` | no | Override the server URL. Defaults to the runner registration's server, then `agentics.dk`. |
| `--token <TOKEN>` | no | Bearer token override that skips the auth chain. |
| `-v`, `--verbose` | no | Enable verbose output. |

## Troubleshooting

- **"Could not parse assembly line URL".** The path is not `/p/{owner}/{project}/assembly-lines/{stageId}`. Copy the URL from the assembly-line page in the browser.
- **A red required-argument error instead of usage text.** `--assembly-line-url` or `--title` is missing. Both are validated by the command, not by the parser.
- **No credentials resolved.** Run `pks agentics init` once, or add `permissions: id-token: write` and make the project trust the calling repository, or register a runner for the project.
- **The CI block has no log excerpt.** Log enrichment is best-effort and never fails the submission. It no-ops when the stored GitHub token is missing, the jobs or logs API call fails, or no job name matches. When `GITHUB_JOB` does not match by name, any job with conclusion `failure` is used instead.
- **`--verbose` reports an auth source you did not expect.** The reported source is display logic inferred after the fact, not necessarily the exact source used internally.

## See also

- [Log in to agentics.dk](/tools/pks/agentics/init) — the credential this command falls back to
- [Register a machine as an Agentics runner](/tools/pks/agentics/runner/register) — the runner-token path in the auth chain
- [pks agentics](/tools/pks/agentics) — how login, runners, and task filing relate
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
