---
title: "pks ms-graph"
description: "Authenticate pks-cli against Microsoft Graph via OAuth2 device code flow and store the tokens that `pks email export` uses to read your mailbox."
tags: [reference, cli, auth, microsoft-graph]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks ms-graph <command> [options]"
examples:
  - command: "pks ms-graph register --client-id your-client-id --tenant-id your-tenant-id"
    description: "Register a known Azure AD app and sign in"
  - command: "pks ms-graph register"
    description: "Walk through app-registration setup, then sign in"
---

`pks ms-graph` authenticates pks-cli against Microsoft Graph so other commands can read a signed-in user's Microsoft 365 mailbox and profile. The group has a single command, `register`, which both configures the Azure AD app and runs the sign-in flow.

## Overview

Microsoft Graph is reached with an OAuth 2.0 access token issued by Microsoft Entra ID. `pks ms-graph register` runs the **device code flow**: it requests a short code from Entra ID, shows it to you in a bordered panel alongside `https://microsoft.com/devicelogin`, and polls the token endpoint while you complete sign-in in any browser, including one on another device. On success it validates the token against Graph's `/me` endpoint and stores the access token, refresh token, and expiry alongside the app's client ID and tenant ID.

Downstream, `pks email export` reads this stored record to authenticate its own Graph calls, refreshing the access token silently from the stored refresh token when it has expired.

## Prerequisites

- **An Azure AD app registration** with **"Allow public client flows" enabled** and **no redirect URI configured**. `register` does not create this app registration — if you omit `--client-id`, it prints the exact Azure Portal steps for creating one before it asks for the ID.
- **A browser reachable from any device**, not necessarily the machine running pks — the device code flow only needs you to open `https://microsoft.com/devicelogin` and type the displayed code within that code's expiry window.

## Synopsis

```text
pks ms-graph <command> [options]
```

```text
register    Configure the Azure AD app and sign in via device code flow
```

### Global environment variables

`pks ms-graph` reads no environment variables of its own.

## register

Authenticates pks-cli against Microsoft Graph using the OAuth2 device code flow. If `--client-id` is omitted, the command prompts interactively for it, printing an Azure Portal walkthrough for creating a public-client app registration first.

It then stores the `client_id`/`tenant_id` config, requests a device code from `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/devicecode`, displays the user code and verification URL in a bordered panel, and polls `https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token` until sign-in completes, the device code expires, or Entra ID returns a terminal OAuth error.

Requested scopes are fixed: `User.Read`, `Mail.Read`, `Mail.ReadBasic`, and `offline_access`. On success it validates the token against Graph's `/me` endpoint and persists the access token, refresh token, scopes, and expiry, then prints a summary table of display name, UPN, and expiry.

Without `--force`, an already-valid registration short-circuits the whole flow and prints only the existing identity and token status.

| Flag | Default | Description |
|---|---|---|
| `--client-id <CLIENT_ID>` | — | Azure AD app client ID. Prompted interactively, with portal setup instructions, when omitted. |
| `--tenant-id <TENANT_ID>` | `common` | Azure AD tenant. `common` accepts both work/school and personal Microsoft accounts. |
| `--force` | `false` | Re-register even if already authenticated — runs a brand-new device code sign-in instead of reusing the stored token. |
| `-v, --verbose` | `false` | Show detailed output; echoes the stored `clientId`/`tenantId` after config save. |

```bash
pks ms-graph register --client-id your-client-id --tenant-id your-tenant-id
```

Registers the given app and tenant non-interactively, then runs the device code flow.

```bash
pks ms-graph register
```

Prompts for a client ID, printing Azure Portal setup instructions first, then prompts for a tenant ID (defaulting to `common`), and runs the device code flow.

```bash
pks ms-graph register --force
```

Skips the "already authenticated" short-circuit and starts a new device code sign-in, even if a valid token is already stored.

## Troubleshooting

> **Note.** The device code has its own expiry window separate from the polling loop — if you don't complete sign-in in the browser in time, `register` fails with a timeout rather than hanging indefinitely.

- **`register` keeps printing the same identity instead of signing in again.** Without `--force`, a stored valid token short-circuits the command entirely — it never refreshes or rotates credentials on its own. Pass `--force` to force a new device code sign-in.
- **Sign-in fails or times out.** You must open `https://microsoft.com/devicelogin` (or the printed verification URL) and enter the displayed code before the code's own expiry window closes — the flow does not wait indefinitely for you to notice the panel.
- **The Azure Portal steps mention public client flows.** The app registration must have "Allow public client flows" enabled and no redirect URI set. `register` prints the exact steps when `--client-id` is omitted, but it never creates the app registration itself.
- **Scopes can't be changed at the command line.** Requested scopes (`User.Read`, `Mail.Read`, `Mail.ReadBasic`, `offline_access`) are hardcoded — there is no `--scopes` flag, even though the underlying service supports arbitrary scopes.
- **Consent or token errors on a work/school-only registration.** `--tenant-id` defaults to `common`, which targets multi-tenant and personal accounts alike; an app registration restricted to a single organization can produce confusing consent errors under that default. Pass the organization's own tenant ID explicitly.
- **Tokens are stored in the open.** The access token, refresh token, and app config land in pks-cli's shared on-disk store, with no OS keychain or secret-manager integration — anyone with filesystem read access to that store can read them.

## See also

- [pks](/tools/pks) — the CLI's full command inventory, of which `ms-graph` is one auth group among several
- [pks github](/tools/pks/github) — another pks command group authenticating via the OAuth2 device code flow
- [pks foundry](/tools/pks/foundry) — a comparable credential-store command group, using authorization-code-with-PKCE instead of device code
</content>
