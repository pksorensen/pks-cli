---
title: "Inspect stored Foundry credentials"
description: "Show the tenant, subscription, resource, endpoint, default model, and refresh-token state that every other pks foundry command operates against."
tags: [how-to, azure, diagnostics]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry status"
examples:
  - command: "pks foundry status"
    description: "Show current Foundry auth and selection state"
---

Find out what `pks foundry token`, `pks foundry proxy`, and `pks foundry usage` will talk to. `pks foundry status` reads the stored credential record and prints it as a table. It makes no network call, so it is the fastest check available.

## 1. Prerequisites

None. The command runs whether or not you have signed in.

## 2. Read the state

```bash
pks foundry status
```

The table covers the tenant, subscription, resource name and endpoint, resource group, default model, the authentication and refresh timestamps, and whether a refresh token is present.

When nothing is stored, the command prints a yellow hint that you are not authenticated — and still exits 0. Do not branch on the exit code to detect auth state in a script; parse the output, or attempt [pks foundry token](/tools/pks/foundry/token) and check that instead.

## 3. Verify against a live call

Because `status` never contacts Azure, a refresh token that Azure has since revoked still shows as present. Confirm the credential actually works:

```bash
pks foundry token
```

A printed token means the stored credential is live.

## Options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Troubleshooting

**"Not authenticated" with exit code 0.** Expected behavior. Run [pks foundry init](/tools/pks/foundry/init).

**Refresh token shows Present but commands still fail.** The stored token is on disk but no longer valid at Azure. Run `pks foundry init --force`.

**The resource is not the one you expected.** Selection is global to your user, not per repository, and `select` or `init --force` overwrites it. Run [pks foundry select](/tools/pks/foundry/select) to change it back.

**No default model listed.** Resource selection never completed, or the resource has no deployments. Run `pks foundry select`.

## Next steps

- [Sign in to Azure AI Foundry](/tools/pks/foundry/init) — create or repair the stored credential
- [Change resource and model selection](/tools/pks/foundry/select) — point pks at a different resource
- [Print a Foundry access token](/tools/pks/foundry/token) — confirm the credential is live
- [pks foundry reference](/tools/pks/foundry/reference) — every stored field explained
