---
title: "pks registry init"
description: "Register a private container registry on a runner host: enter a hostname, username, and password, have Docker verify them, and persist the credential for CI."
tags: [how-to, registry, runner, credentials]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks registry init [hostname]"
examples:
  - command: "pks registry init registry.kjeldager.io"
    description: "Register that hostname, then prompt for credentials"
  - command: "pks registry init"
    description: "Prompt for hostname, username, and masked password"
---

Give a self-hosted runner host the credentials it needs to push and pull images from a private container registry: run `pks registry init`, enter the username and password, and let Docker verify them before they are stored. After this, CI jobs on that host authenticate without a registry secret in the workflow.

## 1. Prerequisites

- **A runner host you control.** Run this on the machine that runs the runner daemon, not on a developer laptop — the store is host-local and job containers read it through the runner's credential server.
- **The `docker` CLI installed, with its daemon running.** `pks registry init` verifies credentials by calling `docker login`. Without a working Docker the command cannot complete.
- **A registry username and password.** For most registries this is a robot account or a personal access token rather than an interactive password.

## 2. Run the command

Pass the hostname as an argument:

```bash
pks registry init registry.kjeldager.io
```

Or run it bare and answer the hostname prompt:

```bash
pks registry init
```

The hostname is normalized before anything else happens. A leading `https://` or `http://` is stripped, and so is a trailing slash, so `https://registry.kjeldager.io/` is stored as `registry.kjeldager.io`.

## 3. Enter the credentials

The command prompts for a username, then for a password. The password prompt is masked — the characters are not echoed and the value does not enter your shell history.

Because the prompts are interactive, this command is not scriptable. Run it by hand when you provision the host.

## 4. Let Docker verify

pks pipes the password to `docker login --username <user> --password-stdin <hostname>`. This proves the credential works against the real registry before it is written to disk. Nothing is stored if the login fails.

The login also leaves the host's own Docker client authenticated to that registry, which is a side effect of verification, not the purpose of the command.

## 5. Verify

List what is now registered:

```bash
pks registry status
```

The hostname you registered appears in a table with its username and a registration timestamp in UTC. If the row is there, the credential is in `~/.pks-cli/registries.json` and the runner's credential server can serve it.

## 6. Next steps

- [pks registry status](/tools/pks/registry/status) — audit every registry this host holds credentials for
- [pks registry remove](/tools/pks/registry/remove) — revoke a registration when the credential rotates
- [pks registry command reference](/tools/pks/registry/reference) — exit codes, storage format, and the credential-server contract

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[hostname]` | no | Registry hostname, for example `registry.kjeldager.io`. Scheme and trailing slash are stripped. When omitted, the command prompts for it. |

There are no flags on this command.

## Troubleshooting

**`Failed to run docker login`.** The `docker` binary could not be started. Install Docker on the runner host, or make sure it is on the `PATH` of the shell running `pks`.

**`docker login failed: <stderr>`.** Docker started but the registry rejected the credentials. The command exits with code 1 and stores nothing. Check the username, re-issue the token, and run `pks registry init` again.

**The registration replaced an older one.** Re-registering a hostname overwrites the previous entry silently, with no confirmation prompt. That is the intended way to rotate a credential — there is no separate update command.

**Two different hostnames for one registry.** Matching is by hostname string, case-insensitively. A registry reachable under two names needs two registrations, one per name.

> **Do not commit.** `~/.pks-cli/registries.json` holds the password in plaintext. Keep it off backups that leave the host and out of any repository.

## See also

- [pks registry](/tools/pks/registry) — the branch overview and mental model
- [pks registry status](/tools/pks/registry/status) — audit what this host can authenticate to
- [pks registry remove](/tools/pks/registry/remove) — revoke a registration from this host
- [pks registry command reference](/tools/pks/registry/reference) — arguments, exit codes, and the credential-server contract
