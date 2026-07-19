---
title: "pks appinsights"
description: "Point pks otel at an Azure Application Insights resource by signing in with Azure AD, picking a resource, and verifying the connection is live."
tags: [reference, azure, observability, configuration]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks appinsights <command> [options]"
examples:
  - command: "pks appinsights init"
    description: "First-time setup: sign in, pick a subscription and resource"
  - command: "pks appinsights init --force"
    description: "Sign in again and re-pick the resource"
  - command: "pks appinsights status"
    description: "Show the configured resource and test the connection"
---

`pks appinsights` configures which Azure Application Insights resource the [`pks otel`](/tools/pks/otel) command group queries. It stores a pointer — an App ID, resource name, and subscription ID — and delegates every sign-in to the same Azure AD credential `pks foundry` already manages.

## Overview

The group has two commands and no state of its own beyond that pointer. `init` discovers a resource and writes it to config; `status` reads the config back and makes a live call to confirm it still works.

- **One-time setup.** Run `init` once per machine before any `pks otel` command works.
- **Shared credential.** Sign-in reuses the Azure AD identity from `pks foundry` — there is no separate appinsights login and no API key.
- **Machine-wide, not per-project.** The stored resource applies to every project on the machine, not just the current repository.

## When to use it

Run `pks appinsights init` once per machine or environment before using any [`pks otel`](/tools/pks/otel) subcommand (`errors`, `traces`, `logs`, `spans`) — those commands read the App ID stored here and query `https://api.applicationinsights.io/v1/apps/{appId}/query`. Run `pks appinsights status` to confirm what's configured and check that the API is actually reachable before spending time debugging why an `otel` query returns nothing.

## Prerequisites

- **Azure AD account with Reader-level ARM access.** `init` lists subscriptions and discovers Application Insights components through the Azure Resource Manager (ARM) API, so the signed-in account needs at least Reader access on the target subscription or resource group.
- **Network access to three hosts.** `login.microsoftonline.com` for Azure AD sign-in, `management.azure.com` for resource discovery, and `api.applicationinsights.io` for the connection test and later telemetry queries.
- **A browser, or willingness to open a printed URL.** `init` opens a browser for interactive sign-in and falls back to printing the URL if the browser doesn't launch.
- **No prior `pks foundry` setup required.** If valid `pks foundry` credentials already exist, `init` reuses them and skips the sign-in prompt.

## Commands

`init` · `status`

Both subcommands accept the inherited `-v|--verbose` flag.

## Synopsis

```text
pks appinsights <command> [options]
```

```text
init      Discover and configure the Application Insights resource pks otel queries
status    Show the configured resource and test the connection
```

## init

Interactively discovers and configures the Application Insights resource that [`pks otel`](/tools/pks/otel) queries against. If you aren't already authenticated with Azure, it prompts for an email or tenant ID (or Enter for the `common` tenant), attempts tenant auto-discovery from the email, then opens a browser for Azure AD sign-in.

Once signed in, it lists your Azure subscriptions — auto-selecting when there's only one, otherwise prompting you to pick — then lists Application Insights resources in the chosen subscription and prompts you to pick one if there's more than one. It persists the selected resource's App ID, name, and subscription ID as global config keys (`appinsights.app_id`, `appinsights.resource_name`, `appinsights.subscription_id`, `appinsights.registered_at`) in `~/.pks-cli/settings.json`.

If a resource is already configured and `--force` isn't passed, `init` short-circuits and prints the existing resource name plus a hint to use `--force`.

| Flag | Default | Description |
|---|---|---|
| `-f, --force` | `false` | Re-configure even if a resource is already stored. |
| `-t, --tenant <text>` | `(none)` | Azure AD tenant ID. Defaults to `common`, or a tenant auto-discovered from the email you enter at the sign-in prompt. |
| `-v, --verbose` | `false` | Enable verbose output. Inherited — shared with `status`. |

```bash
pks appinsights init
```

Prompts for email or tenant, opens a browser for Azure AD sign-in, then walks subscription and resource selection.

```bash
pks appinsights init --force
```

Clears the stored Azure AD credential and re-runs the full discovery flow, even if a resource is already configured.

```bash
pks appinsights init --tenant 11111111-2222-3333-4444-555555555555
```

Skips tenant auto-discovery and signs in against the given tenant directly.

> **Note.** `--force` clears the credential store shared with `pks foundry`, not an appinsights-specific credential — re-running `init` with `--force` forces a fresh Azure sign-in for every pks command that uses that credential, not just `appinsights`.

## status

Shows the currently configured Application Insights resource — App ID, resource name, subscription ID, and the timestamp `init` was last run — read from `~/.pks-cli/settings.json`. It then actively tests connectivity with a live query against `api.applicationinsights.io` and prints `Connected` or `Connection failed` with the resource name or error message.

If nothing is configured yet, it prints a hint to run `pks appinsights init` and exits without error.

`status` takes no group-specific flags, only the inherited `-v, --verbose` flag.

```bash
pks appinsights status
```

Prints a config table, runs a live connection test, and reports `Connected` or `Connection failed`.

## Troubleshooting

- **`pks otel` commands return nothing.** Run `pks appinsights status` first — it makes a real network call every time, unlike `pks otel`'s own commands, so a failed or expired credential shows up here before you spend time on the `otel` query itself.
- **`init` fails with "No Azure subscriptions found."** The signed-in account has no subscriptions visible to it. Nothing is stored in this case, so a later `otel` command still reports unconfigured — sign in with an account that has ARM Reader access on the target subscription.
- **`init` fails with "No Application Insights resources found in this subscription."** The chosen subscription has zero Application Insights components. Nothing is stored; pick a different subscription or create a resource first.
- **Sign-in lands on the wrong tenant.** Tenant auto-discovery from an email silently falls back to the `common` tenant when discovery fails, which can route multi-tenant accounts to the wrong tenant. Pass `--tenant <text>` explicitly to bypass discovery.
- **The `Auth` row on `status` always says the same thing.** It's a static label (`Azure AD (via pks foundry)`) and doesn't reflect whether the stored credential is currently valid — only the connection-test result below it does.

> **Availability.** ARM Reader-level access alone is required for `init`'s discovery step. An account with App Insights query access but no ARM role on the subscription or resource cannot complete `init`, even if it could query telemetry directly once configured.

## See also

- [pks otel](/tools/pks/otel) — the telemetry-query command group that reads the resource configured here.
- [pks foundry](/tools/pks/foundry) — the shared Azure AD credential `pks appinsights init` reuses and clears with `--force`.
- [pks](/tools/pks) — the tool's landing page and full command family map.
