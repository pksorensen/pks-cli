---
title: "pks registry"
description: "Store container-registry credentials on a self-hosted runner host so CI job containers can authenticate to a private registry without secrets in workflow YAML."
tags: [reference, registry, runner, credentials]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks registry <command> [hostname]"
examples:
  - command: "pks registry init registry.kjeldager.io"
    description: "Register a private registry on this runner host"
  - command: "pks registry status"
    description: "List every registry registered on this host"
  - command: "pks registry status registry.kjeldager.io"
    description: "Show the stored entry for one hostname"
  - command: "pks registry remove registry.kjeldager.io"
    description: "Delete stored credentials for one registry"
---

`pks registry` manages container-registry credentials on the machine that runs a self-hosted runner. It exists so a CI job container spawned by the runner can `docker login` against a private registry without the username and password ever appearing in workflow YAML, in the repository, or in a GitHub Actions secret.

## Overview

`pks registry` is a three-command branch that writes a hostname, username, and password into `~/.pks-cli/registries.json` on the runner host. The runner daemon reads that store at job time and hands the matching credential to the job container over an authenticated local socket.

- **Host-scoped, not repo-scoped.** Register once per runner host, and every job that lands on that host can reach the registry.
- **Verified at registration.** `pks registry init` calls the local `docker login` to prove the credentials work before storing them.
- **Read live at job time.** The credential server queries the store on every request, so a removal takes effect on the next job without restarting the runner.

## What you get

- **A single credential store per host.** One JSON file, keyed by hostname, holding every private registry this host can authenticate to.
- **Credential-at-registration verification.** Bad passwords fail during `pks registry init` instead of failing halfway through a CI push.
- **A socket endpoint for job containers.** The runner's credential server exposes `GET /registry/credential?hostname=...` so in-container tooling fetches `{username, password}` on demand.
- **An audit command.** `pks registry status` prints what is registered, by whom, and when.

## How it fits together

The store is written by `pks registry init` and read by the credential server that `pks runner start` constructs when the runner daemon boots. When a CI job needs to push an image, the tooling inside the job container asks the credential server for the hostname it is pushing to, gets the username and password back, and logs in. The secret never leaves the runner host's filesystem and the job container's memory.

Registrations are matched on hostname, case-insensitively. `pks registry init` normalizes the hostname it is given — it strips a leading `https://` or `http://` and any trailing slash — so `https://registry.kjeldager.io/` and `registry.kjeldager.io` are the same entry.

- **At a glance, the write path:** you run `pks registry init` on the host, Docker verifies the credential, pks writes `~/.pks-cli/registries.json`.
- **At a glance, the read path:** a job container asks the runner's credential server, which reads that same file and returns the credential for one hostname.

## Commands

`init` · `status` · `remove`

There is no default command on this branch, so a bare `pks registry` prints the subcommand list. All three subcommands take the same single positional argument, `[hostname]`, and no flags at all.

| Command | What it does |
|---|---|
| [`pks registry init`](/tools/pks/registry/init) | Prompt for a username and masked password, verify with `docker login`, and store the credential. |
| [`pks registry status`](/tools/pks/registry/status) | List every registered registry, or show the detail row for one hostname. |
| [`pks registry remove`](/tools/pks/registry/remove) | Delete a hostname's stored credential from this host. |

## Defaults

| Setting | Value |
|---|---|
| Credential store path | `~/.pks-cli/registries.json` |
| Password storage | Plaintext JSON |
| Hostname matching | Case-insensitive |
| Credential endpoint | `GET /registry/credential?hostname=...` |

No environment variable overrides the store path — the location is fixed on every operating system. On Windows it resolves to `C:\Users\<user>\.pks-cli\registries.json`.

> **Note.** The password is stored in plaintext. Treat `~/.pks-cli/registries.json` as a secret file and protect it with filesystem permissions.

## Next steps

- [pks registry init](/tools/pks/registry/init) — register a private registry and understand the Docker verification step
- [pks registry status](/tools/pks/registry/status) — audit what this host can authenticate to
- [pks registry remove](/tools/pks/registry/remove) — revoke a registry's credentials from this host
- [pks registry command reference](/tools/pks/registry/reference) — the full argument surface, exit codes, storage format, and credential-server contract
