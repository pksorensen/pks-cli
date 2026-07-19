---
title: "pks registry status"
description: "List the container registries registered on a runner host, or inspect a single hostname, reading the local credential store without any network call."
tags: [how-to, registry, runner, audit]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks registry status [hostname]"
examples:
  - command: "pks registry status"
    description: "List every registry registered on this host"
  - command: "pks registry status registry.kjeldager.io"
    description: "Show the stored entry for one hostname"
---

See which private container registries a runner host can authenticate to, and when each credential was registered. This is the audit command for the store that [`pks registry init`](/tools/pks/registry/init) writes.

## 1. Prerequisites

- **A host with at least one registration.** On a host with an empty store the command prints a "No registries registered" message and exits successfully.
- **Nothing else.** No Docker, no network, no login. The command reads `~/.pks-cli/registries.json` and prints it.

## 2. List every registration

```bash
pks registry status
```

The output is a table with three columns — Hostname, Username, and Registered — one row per registry, with the registration timestamp in UTC.

## 3. Inspect one hostname

```bash
pks registry status registry.kjeldager.io
```

This prints the single row for that hostname. If the hostname is not registered, the command prints an error and exits with code 1, which makes it usable as a provisioning check in a shell script.

## 4. Verify

Run the filtered form for a hostname you expect to be present:

```bash
pks registry status registry.kjeldager.io
```

A row means the credential server can serve that hostname to a job container. An error and a non-zero exit means it cannot, and the fix is to run `pks registry init` for that hostname.

## 5. Next steps

- [pks registry init](/tools/pks/registry/init) — add a registration that is missing
- [pks registry remove](/tools/pks/registry/remove) — delete a registration you no longer want
- [pks registry command reference](/tools/pks/registry/reference) — exit codes and the storage format behind this table

## Arguments

| Argument | Required | Description |
|---|---|---|
| `[hostname]` | no | Show only this registration. When omitted, every registration is listed. |

There are no flags on this command.

## Troubleshooting

**A registration exists but CI still fails to authenticate.** This command performs no network call. It proves that an entry is in the local store, not that the stored password is still valid. A rotated or revoked token looks identical to a working one here. Re-run [`pks registry init`](/tools/pks/registry/init) to have Docker verify the credential again.

**The command reports nothing on a host you provisioned.** The store is per-user as well as per-host: it lives under the invoking user's home directory. Registering as `root` and reading as another user gives two different files. Run both commands as the same user that runs the runner daemon.

**A hostname lookup fails despite looking correct.** `pks registry init` stores the normalized hostname, with the scheme and any trailing slash removed. Query with the same bare hostname, not a URL.

> **Note.** The branch help text for this command reads "List registered registries and check connections". No connection check is performed — the command is a local file read only.

## See also

- [pks registry](/tools/pks/registry) — the branch overview and mental model
- [pks registry init](/tools/pks/registry/init) — register a private registry on this host
- [pks registry remove](/tools/pks/registry/remove) — revoke a registration from this host
- [pks github runner](/tools/pks/github/runner) — the runner whose job containers consume these credentials
