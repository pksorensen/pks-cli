---
title: "pks foundry"
description: "Sign in to Azure AI Foundry once, pick a resource and model deployments, then hand short-lived tokens to every pks command that needs them."
tags: [concept, azure, auth, models]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry <command> [options]"
examples:
  - command: "pks foundry init"
    description: "Sign in and pick a resource plus model deployments"
  - command: "pks foundry status"
    description: "Show the stored tenant, resource, and default model"
  - command: "pks foundry select"
    description: "Change resource or enabled models without signing in again"
  - command: "pks foundry token"
    description: "Print a bearer token for the selected resource"
  - command: "eval $(pks foundry proxy)"
    description: "Start a local proxy that injects the real Azure token"
  - command: "pks foundry usage"
    description: "Show cost for the selected Foundry resource"
---

`pks foundry` is the credential and model-selection layer for Azure AI Foundry inside pks. Sign in once, choose which Azure subscription, Foundry resource, and model deployments pks should use, and every later command that needs a Foundry token gets one without another browser round trip.

## Overview

Azure AI Foundry resources are reached with an Azure Active Directory bearer token, and that token lives for about an hour. `pks foundry init` runs an OAuth 2.0 authorization-code flow with PKCE — Proof Key for Code Exchange, the browser-based login that needs no client secret — against the Azure CLI's well-known public client ID, so no app registration is required. What it keeps is the long-lived refresh token, plus your resource and model choices.

- **One sign-in, many commands.** Everything else in the group reads the same stored record and refreshes the access token silently.
- **Selection is a first-class step.** Which resource, which deployments, which default model, and which voice-classifier model are stored choices you can change at any time.
- **Tokens are handed out, not embedded.** `pks foundry token` prints one for scripts; `pks foundry proxy` lends one to a process that should never hold your Azure credential.

## What you get

- **Browser sign-in with tenant discovery.** `pks foundry init` resolves your tenant from an email address, or takes a tenant ID directly with `--tenant`.
- **Guided resource and model selection.** Pick a subscription, then a Foundry resource, then tick the deployments you want enabled and a default model.
- **A stored, refreshing credential.** The refresh token, resource endpoint, and model selection persist globally for your user; the access token is minted on demand.
- **A token printer for scripts.** `pks foundry token` writes a plain bearer token when stdout is redirected, so `TOKEN=$(pks foundry token)` works.
- **A credential-free local proxy.** `pks foundry proxy` accepts a throwaway token and swaps in a fresh Azure token per request.
- **Per-resource cost reporting.** `pks foundry usage` scopes Azure Cost Management to the selected resource.

## How it fits together

`init` is the only command that opens a browser. It stores the tenant and refresh token as soon as login succeeds, then walks you through subscription, resource, and deployments. `select` re-runs that walk using the stored refresh token alone — no browser, no re-login. `status` reads the stored record and calls nothing.

`token`, `proxy`, and `usage` are consumers. Each asks the auth service for a live access token, which refreshes against Azure transparently and rewrites the stored record if Azure rotates the refresh token. Because all three read one shared record, re-running `select` or `init --force` changes what every one of them talks to.

- **State that matters:** tenant, refresh token, subscription, resource endpoint, enabled deployments, default model, optional resource API key.
- **State that does not persist:** the access token itself — it is fetched per command.

## Commands

`init` · `select` · `token` · `status` · `proxy` · `usage`

Every subcommand accepts the inherited `-v|--verbose` flag. See the [pks foundry reference](/tools/pks/foundry/reference) for the full flag surface.

## Next steps

- [Sign in to Azure AI Foundry](/tools/pks/foundry/init) — first-time browser login, resource pick, and model selection
- [Change resource and model selection](/tools/pks/foundry/select) — switch resource or enable a new deployment without logging in again
- [Print a Foundry access token](/tools/pks/foundry/token) — token output for scripts and manual API calls
- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — read-only view of what the other commands will use
- [Run the local Foundry proxy](/tools/pks/foundry/proxy) — lend a Foundry endpoint to a process without giving it your credential
- [Report Foundry resource cost](/tools/pks/foundry/usage) — cost summary, chart, and per-meter breakdown
- [pks foundry reference](/tools/pks/foundry/reference) — every command, flag, scope, and stored field in one place
