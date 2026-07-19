---
title: "Change resource and model selection"
description: "Re-pick the Azure subscription, Foundry resource, enabled model deployments, and default model using the stored refresh token — no browser login."
tags: [how-to, azure, models, configuration]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry select"
examples:
  - command: "pks foundry select"
    description: "Re-pick subscription, resource, and enabled models"
---

Change which Azure AI Foundry resource and which model deployments pks uses, without repeating the browser login. `pks foundry select` reuses the refresh token stored by [pks foundry init](/tools/pks/foundry/init) and re-runs only the selection walk.

Reach for it when you deploy a new model, move to another region, switch resources, or need to finish a setup that broke after login.

## 1. Prerequisites

- **A completed `pks foundry init`.** The command exits immediately when no refresh token is stored.
- **Access to the target subscription and resource** under the same signed-in identity.

## 2. Run the selection walk

```bash
pks foundry select
```

pks lists your subscriptions, then the Foundry resources in the one you pick. If a resource is already saved, its deployments come pre-ticked, so accepting the defaults changes nothing.

## 3. Pick deployments and a default model

Tick the deployments pks may use, then choose the default model from among them.

Zero deployments is tolerated here, unlike in `init`. That is the expected path for a Speech-only resource: `pks voice` uses the Azure Speech REST API and needs no chat-style deployment.

## 4. Pick a voice-classifier model

The walk also asks for a voice-classifier model, used to turn spoken phrases into pks subcommands. It is chosen from the deployments you just enabled, and is separate from the default model. Choosing none falls back to simple text matching.

## 5. Store the resource key

pks tries to fetch the resource's Cognitive Services subscription key itself, using the management token against the Azure Resource Manager `listKeys` operation. If that call fails — missing permissions, network trouble — the command falls back to a manual entry prompt rather than failing.

## 6. Verify

```bash
pks foundry status
```

You should see the new resource name, endpoint, and default model.

If the chosen resource is not of kind `AIServices`, `CognitiveServices`, or `SpeechServices`, the command prints a yellow warning after saving. `pks voice` returns 404 against other resource kinds, such as a pure OpenAI resource.

## Options

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Troubleshooting

**The command exits straight away.** No refresh token is stored. Run [pks foundry init](/tools/pks/foundry/init) first.

**The resource you want is not listed.** Only Cognitive Services accounts of kind `AIServices`, and accounts whose endpoint contains `.services.ai.azure.com`, appear. Check you picked the right subscription.

**pks asks you to type the API key.** The automatic `listKeys` call failed. Read the key from the resource's Keys and Endpoint page in the Azure portal, or grant the signed-in identity permission to list keys and rerun.

**A yellow resource-kind warning after saving.** The selection is stored and chat models still work, but Speech-backed commands will not. Select an `AIServices`, `CognitiveServices`, or `SpeechServices` resource if you need `pks voice`.

**Selection changed and other commands broke.** `token`, `proxy`, and `usage` all read the same stored record. Run `pks foundry status` to see what they now point at.

## Next steps

- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — confirm the new selection
- [Sign in to Azure AI Foundry](/tools/pks/foundry/init) — redo the full login when the refresh token is dead
- [Report Foundry resource cost](/tools/pks/foundry/usage) — check spend on the resource you just picked
- [pks foundry reference](/tools/pks/foundry/reference) — stored fields and scopes
