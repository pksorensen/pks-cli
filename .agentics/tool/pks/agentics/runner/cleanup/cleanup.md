---
title: "pks agentics runner cleanup"
description: "Find and remove Docker containers left behind by a previous Agentics runner process, with a dry run first and a confirmation before anything is deleted."
tags: [how-to, runner, docker, maintenance]
category: infrastructure
platform: [linux, macos, windows]
icon: trash-2
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics runner cleanup [options]"
examples:
  - command: "pks agentics runner cleanup --dry-run"
    description: "List removal candidates without removing them"
  - command: "pks agentics runner cleanup"
    description: "Remove orphans after a confirmation prompt"
  - command: "pks agentics runner cleanup --yes"
    description: "Remove orphans without prompting"
  - command: "pks agentics runner cleanup --all"
    description: "Remove every pks.agentics container, live or not"
---

Restarting the runner strands its containers. Each spawned container is bound to the runner process that created it through a `pks.agentics.runner-instance` label, and its bind mounts point at temp directories the new process does not share — so the old containers can never be reused. `runner cleanup` finds them and removes them.

## 1. Prerequisites

- **The `docker` CLI on `PATH`** with a reachable daemon. If listing containers fails, the command aborts with exit code 1 rather than reporting zero containers.
- **No confusion about scope.** This removes containers, not volumes and not remote containers. A runner handed off over SSH keeps its containers on the remote machine.

## 2. See what would go

Always start with a dry run.

```bash
pks agentics runner cleanup --dry-run
```

The command lists every container carrying the `pks.agentics.fingerprint` label, running or stopped, and marks which ones it considers orphaned.

## 3. Understand the liveness heuristic

There is no out-of-band registry of live runner-instance ids, so liveness is approximated: the command looks for a running `pks-cli agentics runner start` process with `pgrep`.

- **No runner process running** — every labelled container is treated as an orphan and becomes a removal candidate.
- **A runner process running** — the check cannot tell which instance it belongs to, so it stays conservative: only label-less containers are treated as orphans, and anything carrying a runner-instance label is left alone even if it is stale.

## 4. Remove the orphans

```bash
pks agentics runner cleanup
```

You are asked `Remove N container(s)?`, defaulting to No, then each candidate is removed with `docker rm -f`. To skip the prompt in a script:

```bash
pks agentics runner cleanup --yes
```

## 5. Remove everything, deliberately

`--all` bypasses the liveness heuristic and removes every `pks.agentics.*` container regardless of whether a runner is using it — including a container a running runner may reuse for a warm or in-flight job.

```bash
pks agentics runner cleanup --all --dry-run
```

Confirm the list first, then re-run without `--dry-run`.

## 6. Verify

```bash
docker ps -a --filter label=pks.agentics.fingerprint
```

You should see only containers belonging to a runner you intend to keep.

## Options

| Flag | Description |
|---|---|
| `-n`, `--dry-run` | Show what would be removed without removing anything. |
| `-y`, `--yes` | Skip the confirmation prompt. |
| `--all` | Remove all `pks.agentics.*` containers, including those of currently-running runners. |
| `-v`, `--verbose` | Enable verbose output. |

## Troubleshooting

- **Exit code 1 with a listing failure.** Docker is not installed or the daemon is down. Fix Docker, then re-run.
- **Nothing is removed while a runner is running.** That is the conservative branch of the heuristic. Stop the runner and re-run, or use `--all` after checking a dry run.
- **A stale container survives repeated cleanups.** It carries a runner-instance label and a runner process is live. Stop the runner first.
- **Containers reappear after every restart.** Expected — each restart orphans the previous instance's containers. Add a cleanup to your restart routine.

## See also

- [Start the Agentics runner daemon](/tools/pks/agentics/runner/start) — the process whose restarts create orphans
- [pks agentics runner](/tools/pks/agentics/runner) — how the runner lifecycle fits together
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — every flag and environment variable
