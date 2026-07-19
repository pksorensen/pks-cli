---
title: "pks fileshare"
description: "Authenticate a machine against Azure Files via OAuth PKCE and check connection status, the setup half of the pair with pks storage."
tags: [reference, cli, auth]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks fileshare <command>"
examples:
  - command: "pks fileshare init"
    description: "Authenticate this machine against Azure Files"
  - command: "pks fileshare status"
    description: "Check auth state and list visible shares"
---

`pks fileshare` manages OAuth authentication and connection state for file share providers. Today that means one provider — Azure Files, Microsoft's file-share service reached over the ARM (Azure Resource Manager) and Storage REST APIs. It is the auth/setup half of a pair: `fileshare` establishes and reports on credentials, and `pks storage` consumes them to actually list, download, upload, or sync files. `fileshare` never moves a file itself.

The group has two commands: `init` performs the login, and `status` reports whether you're authenticated and how many shares are visible.

## When to use it

Run `pks fileshare init` once per machine, before using any `pks storage` command against Azure Files. Run it again with `--force` to switch tenant, subscription, or storage account, or to refresh a stale token. Run `pks fileshare status` any time to check whether you're authenticated, or to be pointed at `init` when you're not. Do not reach for this group to browse or move files — that's `pks storage list`, `pks storage sync`, and the rest of the `storage` command family.

## Prerequisites

- **An interactive terminal.** `init` uses Spectre prompts (`SelectionPrompt`, `TextPrompt`) throughout and is not suitable for unattended or CI use.
- **A way to open or manually visit a browser URL.** The command tries `$BROWSER`, then the platform default opener (`xdg-open`/`open`/ShellExecute), and always prints the authorize URL as a fallback.
- **A free loopback port** the CLI can bind an `HttpListener` to for the OAuth redirect — can fail in locked-down network sandboxes.
- **At least one Azure subscription** on the signed-in account, and **at least one storage account that supports file shares** (non-`BlobStorage`-kind) in the selected subscription. `init` fails outright if either is empty; there is no way to create a storage account from this command.

## Auth model

`pks fileshare init` runs an OAuth 2.0 Authorization Code + PKCE flow against Microsoft Entra ID (Azure AD), using the well-known Azure CLI public client ID (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) — no client secret required. A loopback HTTP listener on a random free port serves as the redirect URI; the CLI tries to open a browser and also prints the authorize URL to stdout as a fallback.

The initial scope is `https://management.azure.com/.default offline_access`. Later calls mint short-lived access tokens for either the ARM management scope or the `https://storage.azure.com/.default` storage scope via a `refresh_token` grant, refreshing and re-persisting the refresh token whenever Azure rotates it.

Credentials — tenant ID, refresh token, selected subscription, storage account, resource group, and timestamps — are stored as JSON under the config key `fileshare.azure.credentials`, written `global: true`. That means they land machine-wide in `~/.pks-cli/settings.json`, **unencrypted and not per-project** — one set of Azure Files credentials is shared across every project on the machine.

## Environment variables

| Variable | Default | Purpose |
|---|---|---|
| `BROWSER` | (unset) | Executable `init` launches with the Azure authorize URL instead of the OS default opener (`xdg-open`/`open`/ShellExecute). A launch failure is swallowed silently — the printed URL is always the fallback. |

## Synopsis

```text
pks fileshare <command>
```

```text
init      Authenticate against a file share provider and store credentials
status    Show auth state and share count for each registered provider
```

## init

Interactively authenticates against a file share provider — today, always Azure Files — and stores machine-wide credentials for later use by `pks storage`. The flow runs in five steps:

1. **Provider selection.** Only one provider (`AzureFileShareProvider`) is registered, so this step is effectively skipped.
2. **Optional email prompt** to discover the Entra tenant via unauthenticated `userrealm`/`.well-known/openid-configuration` lookups, falling back to the multi-tenant `organizations` endpoint if discovery fails or is skipped.
3. **Browser sign-in** — an Authorization Code + PKCE flow against a loopback callback server.
4. **Subscription selection** — lists the account's Azure subscriptions via ARM, prompting if there's more than one. Fails if there are none.
5. **Storage account selection** — lists storage accounts in that subscription that support file shares (excludes `Kind=BlobStorage` accounts), prompting if there's more than one. Fails if there are none.

On success it persists tenant, subscription, storage account, resource group, and refresh token to `~/.pks-cli/`, then prints a summary table.

If already authenticated and `--force` is not passed, `init` short-circuits with a green "already authenticated" message and makes no network calls.

| Flag | Default | Description |
|---|---|---|
| `-f, --force` | `false` | Force re-authentication even if already authenticated. |
| `-v, --verbose` | `false` | Enable verbose output. |

```bash
pks fileshare init
```

You should see a browser open (or a URL printed) for sign-in, followed by a subscription/storage-account prompt (if more than one exists) and a summary table of the stored credentials.

```bash
pks fileshare init --force
```

Re-runs the full auth flow even if already authenticated — use this to switch subscription or storage account, or to refresh a stale token.

### Gotchas

- `-v, --verbose` is declared but never read anywhere in the command's execution — currently a complete no-op.
- Requires an interactive terminal and a way to open or manually visit a browser URL — not suitable for unattended or CI use.
- Needs a free loopback TCP port and permission to bind an `HttpListener` on `http://localhost:{port}/`.
- The auth callback times out after 120 seconds if the browser flow isn't completed in time, surfaced as a red "Authentication timed out." message.
- Fails outright (exit `1`) if the signed-in account has zero Azure subscriptions, or if the selected subscription has zero non-`BlobStorage`-kind storage accounts.
- `--force` overwrites the stored subscription and storage-account selection — every subsequent `pks storage` call now points at whatever you pick this time, not the previous selection.
- Credentials are stored globally, not per-project, and unencrypted at `~/.pks-cli/settings.json` under `fileshare.azure.credentials` — one set of Azure Files credentials is shared across every project on the machine.
- An OAuth state mismatch on the callback (possible CSRF or stale-tab reuse) throws and aborts the flow rather than retrying silently.
- Only one provider (Azure Files) is wired in; the provider-selection prompt is currently unreachable in practice.

## status

Renders a table — Provider, Status, Details — for every registered file share provider. For each provider it checks whether stored credentials exist with a non-empty refresh token (no live token validation against Azure) and, if so, mints a fresh ARM management-scope access token and lists file shares in the stored storage account, reporting the count or "No shares found". If not authenticated, the Details column tells you to run `pks fileshare init`.

If zero providers are registered at all, `status` prints a yellow warning and exits `0` — this is not treated as an error.

| Flag | Default | Description |
|---|---|---|
| `-v, --verbose` | `false` | Enable verbose output. |

```bash
pks fileshare status
```

You should see a table with one row per registered provider, showing whether it's authenticated and how many file shares are visible.

### Gotchas

- The authenticated check only confirms a refresh token is stored locally — it does not validate it against Azure. A revoked or expired refresh token still shows "Authenticated"; the failure instead surfaces as "No shares found", because the list call catches all exceptions, logs them, and returns an empty list rather than surfacing an error.
- Populating the Details column for an authenticated provider triggers a real network round trip — an ARM token refresh plus a list-shares call. `status` is not purely local despite looking like a simple check.
- `-v, --verbose` is declared but never read anywhere in the command's execution — currently a complete no-op.

## Troubleshooting

> **Note.** `pks fileshare` only authenticates and reports status. It does not list, download, upload, or sync files — that's `pks storage`.

- **"already authenticated" but you need a different subscription or storage account** — run `pks fileshare init --force` to re-run the full flow and pick again.
- **`init` fails with no subscriptions found** — the signed-in Azure account has zero subscriptions. Sign in with an account that has at least one, or create a subscription first; `init` cannot do this for you.
- **`init` fails with no eligible storage accounts** — the selected subscription has no storage account that supports file shares (accounts of `Kind=BlobStorage` are excluded). Create or pick a subscription with a general-purpose or `FileStorage`-kind account, then re-run.
- **"Authentication timed out."** — the browser flow wasn't completed within 120 seconds. Re-run `init` and complete sign-in promptly; check that the loopback port isn't blocked by a firewall or sandbox.
- **`status` shows "Authenticated" but "No shares found" where you expect shares** — the stored refresh token may have been revoked or expired server-side. `status` doesn't distinguish this from a genuinely empty storage account; run `pks fileshare init --force` to get a fresh token and confirm.
- **Credentials seem wrong across projects** — `fileshare` credentials are global, not per-project. Every project on the machine shares the same Azure Files login; there is no per-project override.

## See also

- [pks](/tools/pks) — command families and the operator mental model this group belongs to.
