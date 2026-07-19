---
title: "pks registry command reference"
description: "Complete argument, exit-code, storage-format, and credential-server reference for the pks registry branch — init, status, and remove."
tags: [reference, registry, runner, credentials]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks registry <command> [hostname]"
examples:
  - command: "pks registry init registry.kjeldager.io"
    description: "Register a registry and verify it with docker login"
  - command: "pks registry status"
    description: "List every registration on this host"
  - command: "pks registry remove registry.kjeldager.io"
    description: "Delete one registration"
---

`pks registry` stores container-registry credentials on a runner host, keyed by hostname, so job containers can authenticate to a private registry through the runner's local credential server. The branch has three commands, one shared positional argument, and no flags.

The store is a JSON file at `~/.pks-cli/registries.json`, written by this branch and read by the credential server that the runner daemon starts. `pks` is distributed as a .NET global tool (`dotnet tool install -g pks-cli`) and as npm packages under `@pks-cli/cli`.

## Synopsis

```text
pks registry <command> [hostname]
```

```text
init      Register a container registry (persists credentials for CI)
status    List registered registries
remove    Remove a registered registry
```

There is no default command on the branch, so a bare `pks registry` prints this list rather than performing an action.

## Arguments

All three commands share one argument declaration.

| Argument | Required | Description |
|---|---|---|
| `[hostname]` | no | Registry hostname. Optional at the parsing level for every subcommand. `remove` enforces it in its own body and errors when it is missing. |

No `pks registry` command defines any flag.

## init

Registers a container registry on this host. The hostname comes from the argument or from an interactive prompt. The command then prompts for a username and for a masked password.

Before storage, the hostname is normalized: a leading `https://` or `http://` is stripped, and a trailing slash is removed. The credential is verified by invoking the local Docker client:

```text
docker login --username <user> --password-stdin <hostname>
```

On success, the entry is persisted with its hostname, username, password, generated id, and a UTC `registeredAt` timestamp, and a confirmation table is printed. An existing entry for the same hostname is overwritten, matched case-insensitively, with no prompt.

| Exit | Condition |
|---|---|
| `0` | Credentials verified and stored. |
| `1` | The `docker` binary could not be started, or `docker login` rejected the credentials. |

## status

Reads the local store and prints it. With no argument, every registration is listed as a table of Hostname, Username, and Registered. With a hostname, only that registration is shown.

No network request is made. The branch description reads "List registered registries and check connections", but no connectivity check is implemented — a stored credential that the registry has since revoked still lists normally.

| Exit | Condition |
|---|---|
| `0` | Registrations printed, or the store is empty and a "No registries registered" message was printed. |
| `1` | A hostname was passed and no matching registration exists. |

## remove

Deletes a hostname's entry from the store. Destructive, irreversible, and unprompted. Unlike `init`, it does not fall back to an interactive prompt when the hostname is omitted.

| Exit | Condition |
|---|---|
| `0` | The registration was deleted. |
| `1` | No hostname was given, or the hostname is not registered. |

## Storage

| Setting | Value |
|---|---|
| Path | `~/.pks-cli/registries.json` |
| Windows path | `C:\Users\<user>\.pks-cli\registries.json` |
| Format | JSON, one object per registry |
| Fields per entry | `hostname`, `username`, `password`, `id`, `registeredAt` |
| Password protection | None — plaintext |
| Hostname matching | Case-insensitive |

The file is guarded by an in-process semaphore, not a cross-process file lock. Two `pks registry` invocations running as separate processes at the same instant can in principle race. That is unlikely for a manual provisioning command, but it rules out driving this branch from parallel automation.

> **Do not commit.** The password is stored unencrypted. Protect `~/.pks-cli/registries.json` with filesystem permissions and keep it out of repositories and off shared backups.

## Credential server contract

The runner daemon constructs a credential server over an authenticated unix domain socket and passes it this store. Job containers fetch a credential with:

**Endpoint:** `GET /registry/credential?hostname=...`, which returns the `username` and `password` for that hostname.

The server queries the store on every request, with no caching. A registration added or removed with this branch therefore takes effect on the next request, without restarting the runner. Requests already answered keep the credential they received.

This is why the store exists: the registry secret stays on the runner host and inside the job container, and never appears in workflow YAML, in the repository, or in a CI secret.

## Environment variables

This branch reads no environment variables. The store path is fixed and cannot be relocated.

## See also

- [pks registry](/tools/pks/registry) — the branch overview and mental model
- [pks registry init](/tools/pks/registry/init) — step-by-step registration, including the Docker verification requirement
- [pks registry status](/tools/pks/registry/status) — auditing a host's registrations
- [pks registry remove](/tools/pks/registry/remove) — revoking a registration
- [pks](/tools/pks) — the full command surface of the CLI
