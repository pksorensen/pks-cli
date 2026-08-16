# Agentics runner lifecycle

How a runner is started, found, watched and stopped — on this machine and on one you handed it to
over SSH.

## The verbs

| Command | What it does |
| --- | --- |
| `pks agentics runner run` | Foreground. Blocks until Ctrl+C. Everything interactive lives here. |
| `pks agentics runner start` | Preflight in the foreground, then detach. Returns immediately. |
| `pks agentics runner list` | Every registration this machine knows, and whether it is running. |
| `pks agentics runner status <selector>` | One runner's state plus its last output. |
| `pks agentics runner logs <selector>` | That runner's output. |
| `pks agentics runner stop <selector>` | Stop it. |
| `pks agentics runner register <owner/project>` | Register without starting. `start`/`run` auto-register, so this is rarely needed. |
| `pks agentics runner claude-login <target>` | Seed an SSH target's Claude credentials volume. |
| `pks agentics runner cleanup` | Remove devcontainers left by previous runner instances (ADR 0002). |

`run` vs `start` is the same split as `aspire run` vs `aspire start`. It is deliberately not a
`--persist` flag: whether a command returns is a *mode*, and `list`, `logs` and `stop` all need
that mode to be something they can enumerate. A flag leaves them guessing.

**`start` used to mean `run`.** Before this change `agentics runner start` blocked, and the SSH
handoff launched exactly that string inside a remote tmux session. Both moved together — the
handoff now launches `runner run`. Anything else that shells out to `agentics runner start` and
expects it to block (a systemd unit, a supervisor config, a script) wants `run` now.

## One selector, two worlds

`status`, `logs` and `stop` take a single positional argument:

```bash
pks agentics runner status pksorensen/museliving   # a runner on this machine
pks agentics runner status hetzner                 # one handed off to an SSH target
```

They cannot collide: an `owner/project` always contains a slash, an SSH target label never does.
Remoteness is an attribute of the registration (`Profile.SshTargetLabel`), not a separate command
family — a runner is identified by owner/project, and *where* it runs is a property of it.

Omit the selector entirely and the command resolves it when there is exactly one candidate.

## How detaching works

tmux, not systemd. It is already the house dependency (vibecast requires it), it is what the SSH
handoff has always used, and it makes `logs` free via `capture-pane` — a nohup+pidfile gives you a
file but no live pane to attach to. Where tmux is missing, `start` falls back to a background
process with its output redirected to `~/.pks-cli/runners/<owner>-<project>.log`.

Both modes launch through a generated script (`~/.pks-cli/runners/<owner>-<project>.sh`) rather
than an inline command string. That keeps quotes out of `tmux new-session '...'`, and it leaves an
artifact you can read to see exactly what your runner was started with.

The tmux session name is shared with the SSH handoff (`RunnerTmuxSession.Name`), which is what lets
local and remote `status`/`logs`/`stop` be one code path with a different command runner behind it.

Three things `start` does that are easy to get wrong:

- **It passes `--no-prompt`.** A tmux pane has a real TTY, so every interactive gate in `run` fires
  inside it. Without this, a first start parks on the capability-configure prompt in a pane nobody
  is watching — which looks exactly like a runner that started fine and never claims a job.
- **It pins an absolute `--work-dir`** (`~/.pks-cli/agentics-work/<owner>-<project>` by default).
  `run` resolves `.agentics/_work` relative to the current directory, and a detached process's
  working directory is an accident of whoever launched it.
- **It re-invokes this very executable** (`Environment.ProcessPath`), so the background runner is
  the same build as the foreground command you just typed. No version skew between what you tested
  and what ends up in tmux.

Interactive work stays in the foreground, before detaching — the same ordering the SSH handoff uses
(ship the registration, *then* launch). If you have never logged in to GitHub on this machine, or
want to choose capabilities explicitly, run `pks agentics runner run` (or `run --configure`) once.

## Launchers

The SSH handoff used to hardcode `dnx pks-cli --`, which assumes a working .NET SDK on the target.
It now probes and picks, in this order:

1. `pks` — the self-contained binary on the target's PATH. No runtime, no download.
2. `dnx pks-cli --` — needs a working dotnet install.
3. `npx -y @pks-cli/cli@latest` — needs node.

`@latest` is load-bearing. npx resolves a bare package name against whatever is already in
`~/.npm/_npx` and reuses it without consulting the registry, so a machine that ran
`npx @pks-cli/cli` once keeps running that version indefinitely. Measured: `projects.si14agents.com`
answered 6.15.0 while the registry's `latest` was 6.25.0. That box is also why the hardcoded `dnx`
had to go — its `/usr/lib/dotnet` has no `host/fxr`, so dnx cannot run there at all, while
`/usr/local/bin/pks` works fine.
