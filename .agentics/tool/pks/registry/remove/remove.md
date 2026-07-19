---
title: "pks registry remove"
description: "Delete a container registry's stored credentials from a runner host so job containers can no longer authenticate to that registry from this machine."
tags: [how-to, registry, runner, credentials]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks registry remove <hostname>"
examples:
  - command: "pks registry remove registry.kjeldager.io"
    description: "Delete stored credentials for one registry"
---

Revoke a registry registration from a runner host. After removal, the runner's credential server has nothing to serve for that hostname, so any CI job on this host that needs to push or pull from it fails until the registry is registered again.

## 1. Prerequisites

- **The exact hostname.** Run [`pks registry status`](/tools/pks/registry/status) first and copy the hostname from the table. This command takes no interactive prompt and no fuzzy match.
- **Awareness of what depends on it.** Every job that lands on this host loses access at once, including jobs already queued.

## 2. Confirm what you are about to delete

```bash
pks registry status registry.kjeldager.io
```

A row confirms the entry exists and shows the username it was registered with.

## 3. Remove the registration

```bash
pks registry remove registry.kjeldager.io
```

The entry is deleted from `~/.pks-cli/registries.json` immediately. There is no confirmation prompt and no undo. If the hostname is not registered, the command prints an error and exits with code 1.

## 4. Verify

```bash
pks registry status
```

The hostname is gone from the table. The runner's credential server reads the store live on every request, so the change applies to the next credential request without restarting the runner daemon. Requests already served keep the credential they were given.

## 5. Next steps

- [pks registry init](/tools/pks/registry/init) — register the hostname again with a rotated credential
- [pks registry status](/tools/pks/registry/status) — confirm the remaining registrations on this host
- [pks registry command reference](/tools/pks/registry/reference) — exit codes, storage format, and the credential-server contract

## Arguments

| Argument | Required | Description |
|---|---|---|
| `<hostname>` | yes | Registry hostname to remove. Omitting it prints `Hostname is required.` and exits with code 1. |

There are no flags on this command.

## Troubleshooting

**`Hostname is required.`** Unlike `pks registry init`, this command has no interactive fallback. Pass the hostname on the command line. The argument is declared as optional at the parsing level and enforced inside the command itself, which is why the error is a message rather than a usage error.

**The hostname is reported as not registered.** Match the string exactly as [`pks registry status`](/tools/pks/registry/status) prints it. Entries are stored normalized, with no scheme and no trailing slash.

**Rotating a credential.** Removal is not needed. Run `pks registry init` for the same hostname — it overwrites the existing entry.

**Removing did not stop an in-flight job.** The credential server re-queries the store per request, so removal only affects requests made after it. A job container that already holds the credential keeps working until it exits.

## See also

- [pks registry](/tools/pks/registry) — the branch overview and mental model
- [pks registry status](/tools/pks/registry/status) — confirm what is registered before and after removal
- [pks registry init](/tools/pks/registry/init) — re-register a hostname, which is also how you rotate a credential
- [pks registry command reference](/tools/pks/registry/reference) — arguments, exit codes, and the storage format
