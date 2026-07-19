---
title: "pks azure"
description: "Sign in to Azure with a browser-based OAuth2 PKCE flow, pick a subscription, and view Cost Management spend and sponsorship credit balance."
tags: [reference, azure, auth, cost]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks azure <command> [options]"
examples:
  - command: "pks azure init"
    description: "Sign in and pick a subscription"
  - command: "pks azure init --force"
    description: "Re-authenticate even though credentials are cached"
  - command: "pks azure init --tenant <tenant-id>"
    description: "Log in against a known tenant, skipping discovery"
  - command: "pks azure usage"
    description: "Show Cost Management spend and credit balance"
---

`pks azure` is the generic Azure authentication branch of pks: a browser-based OAuth2 login that other Azure-backed pks features build on, plus a terminal view of Cost Management spend and Microsoft Customer Agreement sponsorship credit.

## Overview

Sign in once with `pks azure init` and the stored credential backs everything else in the branch — and, indirectly, any other pks feature that needs an Azure management token. `pks azure usage` is the branch's only other command: an interactive cost and credit-balance report scoped to a subscription and a time window you pick.

- **One login, silent refresh.** `init` runs the interactive flow once; `usage` mints a fresh access token from the cached refresh token on every run.
- **No app registration needed.** Both commands authenticate as the Azure CLI's own public client, the same identity `az login` uses.
- **Cost, not provisioning.** This branch reports spend. It does not create, size, or destroy Azure resources.

## When to use it

Run `pks azure init` the first time any Azure-backed pks feature needs a management token on a machine, or when you just want to check spend without opening the Azure portal. Run `pks azure usage` any time after that to see current cost and remaining sponsorship credit.

This branch is not the right tool for provisioning or destroying resources — use [pks vm](/tools/pks/vm) for that — and it is not Azure AI Foundry auth, which has its own credential and its own cost command at [pks foundry](/tools/pks/foundry). It is also not Azure DevOps auth (`pks ado`). All three branches authenticate against Azure independently and keep separate stored credentials, even though they share the same underlying OAuth2 pattern.

## Prerequisites

- **A browser reachable from the machine running pks.** `init` opens one automatically and also prints the authorization URL for headless or remote use, but the OAuth redirect target is `http://localhost:{ephemeral-port}` — a session without access to that loopback listener cannot complete the flow from the printed URL alone.
- **A TTY for interactive prompts.** Tenant discovery (email prompt), subscription selection, and the `usage` time-window picker all use interactive prompts. `--tenant` skips the email prompt on `init`, but the subscription picker still prompts when the account has more than one subscription, and `usage` has no non-interactive way to choose a time window.
- **Billing Reader access on an MCA billing profile**, for the credit-balance section of `usage` to show anything. Classic or legacy "Azure Sponsorship" subscriptions have no billing profile visible through this API and are skipped with a note rather than an error.
- **Cost Management Reader** (or broader) on the subscription, for the cost-query section of `usage` to succeed.

## Commands

```text
pks azure <command> [options]
```

```text
init     Authenticate to Azure and select a subscription
usage    Show Cost Management spend and sponsorship credit balance
```

## pks azure init

Runs the interactive Azure OAuth2 authorization-code flow with PKCE — Proof Key for Code Exchange, the browser-based login that needs no client secret — against the Azure CLI's public client ID `04b07795-8ddb-461a-bbee-02f9e1bf7b46`. If credentials are already stored and `--force` is not given, it prints the cached tenant and subscription and exits without touching the network.

Otherwise it optionally prompts for an email address to discover your Azure AD tenant (falling back to the `common` tenant if discovery fails or the prompt is left empty), opens the system browser against a local loopback listener, and exchanges the returned authorization code for tokens. It stores the tenant and refresh token immediately after that exchange, before doing anything else, so token refresh keeps working even if a later step fails. It then fetches a management-scope access token, lists the account's visible Azure subscriptions, and prompts you to pick one when there is more than one (auto-selecting when there is exactly one). It finishes by storing the full credential record — tenant, refresh token, subscription ID and name — and printing a summary table.

```text
pks azure init [options]
```

### Options

| Flag | Description |
|---|---|
| `-f`, `--force` | Force re-authentication even if credentials are already stored. |
| `-t`, `--tenant <tenant-id>` | Azure AD tenant ID to sign in against, skipping the email-based discovery prompt. Defaults to `common`. |

### Examples

```bash
pks azure init
```

First-time sign-in: opens the browser, discovers the tenant from an email address, and prompts for a subscription if there is more than one.

```bash
pks azure init --force
```

Re-run the login even though cached credentials exist — the fix `pks azure usage` itself suggests when the stored refresh token has gone stale.

```bash
pks azure init --tenant <tenant-id>
```

Sign in directly against a known tenant, skipping the email prompt. The subscription picker still prompts if the account has more than one subscription.

## pks azure usage

Shows Azure Cost Management spend and Microsoft Customer Agreement sponsorship credit balance for a subscription, over a time window you choose interactively. It requires a completed `pks azure init` — it does not authenticate on its own.

The command verifies stored credentials exist, mints a fresh management-scope access token from the cached refresh token, lists subscriptions and prompts for one when there is more than one, then prompts for a time window (this month, last month, last 30 days, last 90 days, or a custom date range — the same prompt `pks foundry usage` reuses).

It lists visible Billing Profiles and, for each, fetches active Microsoft Customer Agreement credit lots, rendering one table per profile with original and remaining amounts, currency, and expiry.

Profiles or subscriptions with no billing profile visible through this API — expected for personal or legacy "Azure Sponsorship" offers — are skipped with a note pointing at the legacy `microsoftazuresponsorships.com` balance page instead of erroring.

It then queries Cost Management for the chosen subscription scope: an ungrouped total-cost summary, a daily cost series rendered as a terminal chart, and cost grouped by meter, top 15 by spend.

```text
pks azure usage
```

`pks azure usage` takes no options beyond the ones every subcommand's `CommandSettings` type inherits from Spectre.Console.Cli.

### Examples

```bash
pks azure usage
```

Walks through the subscription pick (when there is more than one), then the time-window pick, then prints the credit balance, the cost summary, the daily chart, and the top-15 cost-by-meter table.

## Troubleshooting

**"Run pks azure init to authenticate first".** `usage` found no stored Azure credentials. Run [pks azure init](/tools/pks/azure).

**"Try pks azure init --force".** `usage` printed this because the cached refresh token failed to mint a fresh management token — it does not recover automatically. Re-run `pks azure init --force`.

**No credit-balance tables print, but the command does not fail.** The account has no visible MCA billing profile or credit lots — expected for a personal or legacy "Azure Sponsorship" subscription, which uses a different billing model. `usage` catches this case and downgrades it to a note rather than an error.

**The cost-query section fails and the command exits 1.** Unlike the credit-balance section, any exception fetching the cost summary, daily series, or by-meter breakdown is fatal. Confirm the signed-in identity has at least Cost Management Reader on the subscription — Contributor or Owner is not automatically sufficient if Cost Management access has been restricted separately.

**`init` exits 1 with a raw exception message.** There is no retry or backoff on OAuth failures. Re-run `pks azure init`; if it keeps failing, add `--tenant` to bypass tenant discovery.

**Prompts hang or the command appears stuck non-interactively.** Both the tenant-discovery email prompt on `init` and the time-window prompt on `usage` require a TTY. Neither command can be scripted or piped as-is.

## Next steps

- [pks foundry](/tools/pks/foundry) — separate Azure AI Foundry credential and cost command, structurally similar but independently authenticated
- [pks vm](/tools/pks/vm) — provision and destroy Azure resources, rather than just reading cost
- [pks](/tools/pks) — the full command surface `pks azure` belongs to
