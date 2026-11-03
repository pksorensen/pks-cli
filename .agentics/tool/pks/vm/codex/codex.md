---
title: "Run Codex on a remote box"
description: "Install the Codex CLI on a machine over SSH and create a dedicated non-root account for it, so the Codex desktop app can open projects on that box instead of your laptop."
tags: [how-to, vm, codex, ssh, remote]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks vm codex [TARGET] [options]"
examples:
  - command: "pks vm codex"
    description: "Pick a registered SSH target and make it Codex-ready"
  - command: "pks vm codex hetzner"
    description: "Bootstrap a named target without the picker"
  - command: "pks vm codex --host root@example.com"
    description: "Bootstrap a box that is not a registered target"
  - command: "pks vm codex arvo --docker"
    description: "Also put the account in the docker group"
  - command: "pks vm init --codex"
    description: "Provision a VM and make it Codex-ready in one command"
  - command: "pks vm codex --print"
    description: "Read the script without running it anywhere"
---

The Codex desktop app can work on a remote machine over SSH — the chat runs commands, reads files and writes changes on that box rather than on your laptop. It needs two things on the far end that a bare server does not have: the Codex CLI, and an account to run it as. Without the first, the app reports **"Codex CLI is not installed on this remote machine"**. Without the second, it runs as whatever you connect as, which on most of these boxes is root.

`pks vm codex` supplies both over SSH and adds the host to your own `~/.ssh/config`. What is left depends on who Codex talks to: one sign-in against OpenAI, or nothing at all when a provider is already configured on the box.

The quickest path from nothing to a working remote box is one command:

```bash
pks vm init --codex
```

That provisions the machine, registers it as an SSH target, installs the Codex CLI and the account, and writes the ssh config alias — using the key `vm init` just generated, which is already on this machine. All that remains is authentication, which is §4.

## 1. Run it

```bash
pks vm codex hetzner
```

| Argument / option | Default | Description |
|---|---|---|
| `TARGET` | picker | A registered SSH target, by label or host. See `pks ssh list`. |
| `--host <USER_AT_HOST>` | — | Bootstrap a box that is not a registered target, e.g. `root@example.com`. |
| `--port <PORT>` | 22 | SSH port for `--host`. |
| `--key <PATH>` | ssh's choice | Private key for `--host`. |
| `--user <NAME>` | `codex` | The account to create and run Codex as. `root` is refused. |
| `--authorized-key <KEY_OR_PATH>` | inherit | Public key to authorise, repeatable. Takes a literal key or a path to a `.pub`. |
| `--node <MAJOR>` | 22 | Node major to install when the box's node is older. A newer node is left alone. |
| `--codex-version <VERSION>` | `@latest` | Pin the CLI version. |
| `--docker` | off | Add the account to the `docker` group. |
| `--sudo` | off | Grant the account passwordless sudo. |
| `--alias <ALIAS>` | `<target>-<user>` | Host alias written into `~/.ssh/config`. |
| `--no-ssh-config` | off | Leave `~/.ssh/config` alone and print the stanza to paste instead. |
| `--print` | — | Print the script instead of running it. |

With no `--authorized-key`, the new account inherits the keys the account you are connecting as already trusts. That grants it nothing the connecting account did not already have, which is why one argument is enough for the common case.

The command needs root or passwordless sudo on the box, and exits 78 if it has neither — there is no terminal on the far end to answer a password prompt with.

## 2. What happens on the machine

1. `curl` and `ca-certificates` are installed.
2. Node is installed from NodeSource **only if** the box's node is older than `--node`. A newer node is never downgraded.
3. `@openai/codex` is installed globally, as root, so the binary lands in `/usr/bin`.
4. The account is created with `--create-home --shell /bin/bash`, its shadow password field set to `*`, and `~/.ssh` and `~/.codex` created `0700`.
5. `authorized_keys` is written to a temp file and moved into place, never truncated in position.
6. `--docker` and `--sudo` are applied if asked; dropping `--sudo` on a later run **revokes** the grant.
7. The CLI is probed through the account's own login shell, and the sign-in state is reported.

Every step is idempotent. Re-running against a box that already has Codex is the supported way to upgrade the CLI, add a key, or change the privilege flags.

## 3. The ssh config entry, written for you

The app only offers hosts it finds in `~/.ssh/config` — on Windows, `C:\Users\<name>\.ssh\config`. It reads **concrete** `Host` aliases, resolves them with OpenSSH, and ignores pattern-only ones. And the alias has to carry the new account, or the app goes on connecting as whoever it connected as before.

Because `pks` runs on the same machine as the app, it writes that entry itself, inside markers it can find again:

```
# >>> pks vm codex: pks-vm-a1b2-codex >>>
Host pks-vm-a1b2-codex
    HostName 20.1.2.3
    User codex
    IdentityFile /home/you/.pks-cli/keys/pks-vm-a1b2
    IdentitiesOnly yes
# <<< pks vm codex: pks-vm-a1b2-codex <<<
```

The write is conservative, because that file is yours and usually hand-edited:

- Re-running is a **no-op** when nothing changed, and replaces our block **in place** when it did — never appends a second one, since ssh resolves options from the *first* matching `Host`.
- An alias already declared **outside** our markers is **refused, not overwritten**. The command says so and prints the stanza; use `--alias` to pick another name.
- Content outside the block comes back byte for byte, and the block adopts the file's own line endings, so a CRLF config stays CRLF.
- A config file the command creates is written `0600`, because ssh refuses a group- or world-readable one with *"Bad owner or permissions"* — which would break every ssh from the machine, not just Codex.

`IdentityFile` can only name a key that exists on *this* machine, and is written as an absolute path — ssh resolves a relative one against the working directory, and Codex spawns ssh from wherever it pleases. A leading `~` is left for ssh to expand.

After `pks vm init` the path is real: `~/.pks-cli/keys/<vm-name>`, the key it generated. For a target whose key pks holds in its own store there is no such file, because the copy it hands to ssh is a short-lived temp that is shredded when the command returns — so the stanza carries a placeholder to fill in instead.

## 4. The last step depends on who Codex talks to

There are two entirely different endings, and the command reports whichever applies.

### Against a custom provider (a gateway): nothing left

If the account's `~/.codex/config.toml` names a `model_provider`, the box authenticates through that provider's header. There is no sign-in: **the configuration is the authentication**, and `codex login` is not a step that exists. The command says which provider it found and stops.

**This command does not author that file.** It installs the CLI, creates the account and makes the box reachable under an alias; what the provider is, which model, and which credential belong to whoever owns the estate — for the Agentics/Context& gateway that is the `Install-FoundryCodex.ps1` / `install-foundry-codex.sh` installer, which already knows how to compose the file and can write it over the alias this command wrote. The two are deliberately not symmetrical.

> **If you write that file on a remote box, use `http_headers`, not `env_http_headers`.**
>
> `env_http_headers` names an environment variable, which has to be present for whatever starts `codex`. Over SSH that is a **non-interactive** shell, and it reads neither `~/.bashrc` — Ubuntu's begins with `case $- in *i*) ;; *) return;; esac` — nor, unless it is a login shell, `~/.profile`. Measured 2026-11-02 in a container: for `ssh box 'echo $VAR'` both were empty; through a login shell `~/.bashrc` still was.
>
> So a key written into a shell profile is present for a human at a prompt and **absent for the app server**, and the gateway answers **401** — which reads as a bad key rather than a missing variable. On a laptop this never shows: macOS gets `launchctl setenv` and Windows a User-scope variable. It only bites over SSH.
>
> `http_headers` carries the value in the file, which at `0600` owned by the account is tighter than an `export` line in a `0644` profile. The command warns when it finds `env_http_headers` in a remote config.
>
> The other remote trap: `base_url` must be reachable **from the box**. A loopback URL copied from a laptop config gives a connection refused that also looks like a bad key.

### Against OpenAI: one sign-in

`codex login` needs a human. Its default browser flow opens a callback on the **box's** loopback, which a browser on your machine cannot reach, so on a headless host use device auth. The command prints it against the alias it just wrote, which also makes running it a check that the stanza resolves:

```bash
ssh pks-vm-a1b2-codex -t codex login --device-auth
```

Device code login has to be enabled on the account (ChatGPT security settings, or workspace permissions). Otherwise forward the callback port instead:

```bash
ssh -L 1455:localhost:1455 pks-vm-a1b2-codex -t codex login
```

(When the config was not written — `--no-ssh-config`, or a refused alias — both are printed in the explicit `codex@host` form instead.)

Then, in Codex: **Settings → Connections**, enable the host, and pick a project folder.

## Gotchas

- **The account must have a real login shell.** The app starts `codex app-server` *through* it. A `nologin` service account cannot host Codex, and the command repairs one it finds.
- **`codex` has to be on that login shell's PATH.** This is why the install is global rather than per-user: a `~/.local/bin` install does not reliably survive the app's non-interactive probe.
- **One Codex account per box.** The managed remote app server binds a fixed port (9234). A second Codex account on the same host collides with the first, and the reported symptom is attaching to the other account's running server and showing its configuration ([openai/codex#19590](https://github.com/openai/codex/issues/19590)). The command warns when the port is already taken.
- **A `useradd` default leaves the account locked, and sshd refuses a locked account outright.** Plain `useradd` writes `!` into the shadow password field; sshd rejects such an account *before* reading `authorized_keys`, so a correct key fails with a bare `Permission denied (publickey)` while the box's log says `not allowed because account is locked`. Cloud images hide this by setting `UsePAM yes`; a minimal or hardened `sshd_config` does not. The command creates the account with `--password '*'` — no password can match it, but it is not the locked flag — and repairs an account an earlier run left at `!`. Measured 2026-11-02 against a real sshd. Note that a `su - codex` probe cannot see this at all, because `su` bypasses sshd.
- **`AllowUsers`/`AllowGroups` in `sshd_config` silently wins.** The new account is rejected however correct its keys are, and the app shows that as a plain connection failure. The command warns; it never edits `sshd_config`, because a bad edit there locks everyone out of the box.
- **`--docker` is root by another name.** Docker group membership on the host is equivalent to root, so it undoes most of what a separate account bought. Opt in knowingly.
- **A credential in a shell profile does not reach the app server.** The thing Codex starts over SSH is a non-interactive shell, which reads no `~/.bashrc` and usually no `~/.profile`, so `env_http_headers` resolves to nothing and the gateway answers 401. See §4 — this is the single most expensive trap in the remote setup, because it looks like a key problem.
- **A Windows remote host is not supported** by the app's SSH bootstrap, which assumes a POSIX login shell. Linux is the working path.

## When `vm init --codex` is used

The Codex half never fails the provisioning. If it does not complete, the VM is still created, registered and reachable — reporting non-zero would suggest otherwise — and `pks vm codex <name>` retries just that part. Both entry points run the same script, so they cannot drift.

## Related

- [pks vm](/tools/pks/vm) — the command group
- [pks vm init](/tools/pks/vm/init) — provision a machine
- [pks ssh](/tools/pks/ssh) — register and connect to SSH targets
