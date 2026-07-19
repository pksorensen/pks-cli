---
title: "Run a command on an SSH target"
description: "Execute one non-interactive command on a registered SSH target with local stdin forwarded and remote output streamed straight through to your shell."
tags: [how-to, ssh, automation]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ssh run <TARGET> -- <command>"
examples:
  - command: "pks ssh run hetzner -- uname -a"
    description: "Run one command and print its output"
  - command: "pks ssh run hetzner -- \"cd ~/dst && tar xzf -\""
    description: "Feed piped local stdin into a remote command"
  - command: "pks ssh list"
    description: "See which targets are available"
---

`pks ssh run` executes a single command on a registered SSH target and behaves like plain `ssh host cmd`: local stdin is forwarded to the remote process, remote stdout and stderr stream back untouched, and the exit code mirrors the remote command's. That makes it usable inside ordinary shell pipelines.

## 1. Prerequisites

- **A registered target.** Create one with [pks ssh register](/tools/pks/ssh/register).
- **The `ssh` binary on PATH.**
- **Key-based access that needs no prompt.** `run` uses `BatchMode=yes`, so no password or passphrase prompt is possible.
- **An enrolled authenticator, if two-factor is required.** The `ssh.connect` guard applies to `run` exactly as it does to `connect`.

## 2. Run a command

Put the remote command after `--`:

```bash
pks ssh run hetzner -- uname -a
```

The remote kernel line prints on stdout. `--` is the preferred form; when a positional command is also present, everything after `--` wins.

Quote the whole command when passing it positionally, so the local shell hands it over as one argument.

## 3. Pipe data through it

Because stdin is forwarded, `run` slots into a pipeline:

```bash
tar czf - dir | pks ssh run hetzner -- "cd ~/dst && tar xzf -"
```

The local archive streams into the remote `tar`. The pks banner is suppressed for `pks ssh run`, so the stream stays clean.

## 4. Verify

```bash
pks ssh run hetzner -- "echo ok"
```

`ok` is printed and the command exits 0. A failing remote command propagates its own exit code, so `pks ssh run hetzner -- false` exits non-zero.

## Arguments

| Argument | Required | Description |
|---|---|---|
| `TARGET` | yes | Target label or host from the registry. |
| `CMD` | no | Command to run. Prefer passing it after `--` instead. |

`pks ssh run` takes no flags of its own.

## Troubleshooting

**A usage error and exit code 1, with no connection attempt.** No command was supplied — neither after `--` nor as `CMD`. Add the command.

**The remote command hangs.** `BatchMode=yes` blocks interactive prompts, so a command expecting input on a terminal never completes. Feed it from local stdin instead, or use [pks ssh connect](/tools/pks/ssh/connect) for anything interactive.

**Permission denied straight away.** With `BatchMode=yes` there is no passphrase prompt to fall back on. Load the key into your agent, or bind the target to a pks-held key — see [pks ssh key](/tools/pks/ssh/key).

**The command exits 1 with a guard message.** The `ssh.connect` action guard denied the connection. Complete the second factor and retry.

**Multi-part shell logic behaves oddly.** Quote it as a single argument (`-- "cd ~/dst && tar xzf -"`) so the remote shell, not your local one, interprets the `&&`.

## Next steps

- [Copy files to and from a target](/tools/pks/ssh/copy) — scp transfers when a pipeline is the wrong shape
- [Connect to an SSH target](/tools/pks/ssh/connect) — interactive sessions on the same target
- [Register an SSH target](/tools/pks/ssh/register) — add the target this command addresses
- [pks ssh reference](/tools/pks/ssh/reference) — the complete flag and file-path surface
