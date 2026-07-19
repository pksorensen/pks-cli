---
title: "Refresh marketplace sources"
description: "Re-fetch registered marketplace sources to pick up new, changed, or removed plugins while keeping the enabled state of the plugins that survive."
tags: [how-to, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace refresh [ID]"
examples:
  - command: "pks marketplace refresh my-marketplace"
    description: "Re-fetch one marketplace source"
  - command: "pks marketplace refresh"
    description: "Re-fetch every registered marketplace"
---

`pks marketplace refresh` re-reads the `marketplace.json` from each registered marketplace's original source and rebuilds the local plugin list from it. Enabled state carries over for plugins that still exist under the same name; anything new arrives disabled; anything that vanished upstream is dropped.

## 1. Prerequisites

- **A registered marketplace.** Add one with [pks marketplace add](/tools/pks/marketplace/add).
- **Network access to the original source.** Refresh re-hits the same URL, or the same `raw.githubusercontent.com` path at the ref recorded when the marketplace was added.
- **A public source.** The fetch is unauthenticated, exactly as at add time.

## 2. Refresh one marketplace

```bash
pks marketplace refresh my-marketplace
```

The plugin list is replaced with the freshly fetched one and the entry's last-fetched timestamp is updated.

## 3. Refresh everything

Omit the ID to sweep every registered marketplace in one pass:

```bash
pks marketplace refresh
```

Failures are handled per marketplace: one bad source is reported and the rest still refresh and save. The command exits non-zero overall if any marketplace failed.

## 4. Verify

Compare the counts, then read the detail:

```bash
pks marketplace list
pks marketplace show my-marketplace
```

New plugins appear with enabled set to no. Re-enable the ones you want with [pks marketplace enable](/tools/pks/marketplace/enable).

## Options

`refresh` takes no flags.

| Argument | Required | Description |
|---|---|---|
| `ID` | no | Marketplace ID to refresh. Omit to refresh every registered marketplace. |

## What refresh does not do

- **It does not re-apply policy.** The `{url}/policy` document that `add` fetches for URL sources is not fetched again. The plugin snapshot is rebuilt carrying over only the enabled flag, not the `required` flag, so required-plugin protection set at add time is effectively reset. Re-run `pks marketplace add` against the URL source when you need the policy applied again.
- **It does not prompt.** There is no interactive selection step, so new plugins are never offered for enabling during a refresh.
- **It does not change the ref.** A `github:` source is re-fetched at the ref stored when it was added.

## Troubleshooting

**Unexpected plugins appeared or disappeared.** A `github:` source pinned to a moving branch such as `main` follows whatever is on that branch. Re-add the marketplace with a pinned ref — `pks marketplace add github:owner/repo@v2` — to stop it drifting.

**A required plugin is now disableable.** Refresh rebuilds plugin entries without the policy-derived `required` flag. Re-run `add` against the URL source to restore it.

**The bulk refresh exited non-zero but some marketplaces updated.** That is the design. Per-marketplace errors are reported individually, the successful refreshes are saved, and the non-zero exit reports that at least one source failed. Read the output to see which one.

**The fetch fails where `add` succeeded.** The source moved, the ref was deleted, or the document is no longer publicly readable. Verify the URL or repository path, then re-add if the source relocated.

## Next steps

- [Enable and disable plugins](/tools/pks/marketplace/enable) — turn on plugins that arrived disabled
- [List and inspect marketplaces](/tools/pks/marketplace/list) — read the rebuilt plugin table
- [Add a marketplace](/tools/pks/marketplace/add) — re-register to reapply a policy or change the pinned ref
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — the full argument and flag surface
