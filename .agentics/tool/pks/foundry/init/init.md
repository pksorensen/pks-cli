---
title: "Sign in to Azure AI Foundry"
description: "Run the browser login for Azure AI Foundry, choose a subscription and Foundry resource, and enable the model deployments pks should use."
tags: [how-to, azure, auth, setup]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry init"
examples:
  - command: "pks foundry init"
    description: "Interactive sign-in and resource selection"
  - command: "pks foundry init --force"
    description: "Re-authenticate even though credentials exist"
  - command: "pks foundry init --tenant 00000000-0000-0000-0000-000000000000"
    description: "Authenticate directly against a known tenant"
---

Get pks talking to Azure AI Foundry in a few minutes: sign in through the browser, pick the subscription and Foundry resource, enable the model deployments you want, and confirm the stored selection. Run this once per machine; afterwards use [Change resource and model selection](/tools/pks/foundry/select) for changes.

## 1. Prerequisites

- **An Azure account with access to a Foundry resource.** The command lists Cognitive Services accounts of kind `AIServices`, plus any account whose endpoint contains `.services.ai.azure.com`.
- **At least one model deployment on that resource.** `init` fails when the chosen resource has no deployments.
- **A browser on the machine running pks.** The login uses a loopback redirect, so a headless machine has no way to complete it.
- **Your work email, or the tenant ID.** Without `--tenant`, the command asks for an email and resolves the tenant from it.

## 2. Start the sign-in

```bash
pks foundry init
```

If credentials are already stored, the command prints the current resource and exits without prompting. Add `--force` to redo the flow.

To skip email-based tenant discovery, pass the tenant directly:

```bash
pks foundry init --tenant 00000000-0000-0000-0000-000000000000
```

Tenant discovery uses the Azure AD user-realm and OpenID discovery endpoints, and falls back to the `common` tenant when discovery fails.

## 3. Complete the browser login

pks opens your browser and starts a local callback listener for the OAuth redirect. Sign in with the Azure account that owns the Foundry resource.

When the exchange succeeds, pks stores the tenant and refresh token immediately — before you pick anything else. A crash later in the flow therefore leaves a usable credential that [pks foundry select](/tools/pks/foundry/select) can finish from.

## 4. Choose subscription and resource

pks lists your Azure subscriptions, then the Foundry resources in the one you pick. Selecting a resource fixes the inference endpoint, which pks derives as:

```text
https://{resourceName}.services.ai.azure.com
```

That is deliberately not the `cognitiveservices.azure.com` endpoint Azure Resource Manager returns, because the Anthropic-compatible API lives on the `services.ai.azure.com` host.

## 5. Enable model deployments

pks lists every deployment on the resource — chat models, embeddings, text-to-speech, and anything else deployed there. Tick the ones pks may use. At least one selection is required. Then pick which of them is the default model.

## 6. Store an optional API key

The last prompt offers to store the resource API key. It is optional. Without it, launching `claude` in a devcontainer against Foundry falls back to `az login` or to `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` through `DefaultAzureCredential`.

## 7. Verify

```bash
pks foundry status
```

You should see the tenant, subscription, resource name and endpoint, resource group, default model, and a refresh token marked as present.

Then confirm a live token can be minted:

```bash
pks foundry token
```

A bearer token prints for the selected resource. A failure here means the refresh token is not usable — rerun with `--force`.

## Options

| Flag | Default | Description |
|---|---|---|
| `-f`, `--force` | `false` | Force re-authentication even when credentials are already stored. |
| `-t`, `--tenant <id>` | `common` | Azure AD tenant ID to authenticate against, skipping email-based discovery. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Troubleshooting

**The command prints the current resource and exits.** Credentials already exist. Use `pks foundry init --force`.

**The browser never opens, or login times out.** The flow needs a browser and a reachable loopback listener. On a headless machine, container, or CI runner the redirect cannot complete. Run `init` on a workstation instead.

**"No subscriptions found".** The signed-in account has no Azure subscriptions visible in the chosen tenant. Pass the correct tenant with `--tenant`.

**"No Foundry resources found".** The subscription has no Cognitive Services account of kind `AIServices` and none whose endpoint contains `.services.ai.azure.com`. Pick another subscription, or create the resource in Azure first.

**"No model deployments".** The resource exists but nothing is deployed on it. Deploy a model in Azure AI Foundry, then rerun. If the resource is Speech-only and you only need `pks voice`, use [pks foundry select](/tools/pks/foundry/select) instead, which tolerates zero deployments.

**Login succeeded but selection broke halfway.** The tenant and refresh token are already stored. Run `pks foundry select` to finish choosing subscription, resource, and deployments without signing in again.

## Next steps

- [Change resource and model selection](/tools/pks/foundry/select) — re-pick resource or deployments without a browser
- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — confirm what was saved
- [Print a Foundry access token](/tools/pks/foundry/token) — use the credential from a script
- [pks foundry reference](/tools/pks/foundry/reference) — full flag and scope reference
