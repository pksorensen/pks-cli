---
title: "List the model catalog"
description: "Print the deployments on the selected Azure AI Foundry resource as the catalog pks exec serves to companion tools: what is enabled, which is the default, and which role each one fills."
tags: [how-to, azure, models, scripting]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks foundry models [--json] [--role <ROLE>]"
examples:
  - command: "pks foundry models"
    description: "A table of every deployment with modality, status, enabled and roles"
  - command: "pks foundry models --json"
    description: "The catalog document, exactly as /pks/v1/models serves it"
  - command: "pks foundry models --role decision"
    description: "Only the deployments that fill the decision role"
---

List what the selected Foundry resource can serve, joined with what you chose in `pks foundry select`. This is the Foundry slice of [pks models](/tools/pks/models), which lists every registered provider; the same document is served to a tool started by [pks exec](/tools/pks/exec) with the `foundry` provider at `GET {proxy:endpoint}/pks/v1/models`, behind the per-run proxy token, so a tool can pick its own model without a prompt and without seeing a credential.

## 1. Prerequisites

- **A completed `pks foundry init`** with a selected resource. Without one the command exits with a pointer back to [pks foundry init](/tools/pks/foundry/init).
- The token the command mints is for the data plane (`https://cognitiveservices.azure.com/.default`), so no management-plane role is needed.

## 2. Read the table

```bash
pks foundry models
```

| Column | Meaning |
|---|---|
| Deployment | The deployment name, which is what a request's `model` field takes. `★` marks the default from `pks foundry select` |
| Modality | `input->output`: `text->text` for chat, `audio->text` for transcription, `text->choice` for a Decision model, and so on |
| Status | Azure's provisioning status; anything but `succeeded` is not usable |
| Enabled | Ticked in `pks foundry select`. When nothing was ticked, every deployment counts as enabled |
| Roles | `text` for the default model, `decision` for the model picked as *voice classifier* in `pks foundry select` |

## 3. Use the document

```bash
pks foundry models --json | jq '.data[] | select(.pks.enabled) | .id'
```

The shape is OpenAI's `GET /v1/models` list (`{object:"list", data:[{id, object:"model", created, owned_by}]}`) with OpenRouter's `name`, `description` and `architecture{modality, input_modalities, output_modalities}` on top, and one `pks` object per entry for what no public catalog knows:

```json
{"id":"gpt-5.6-luna","object":"model","owned_by":"foundry","name":"gpt-5.6-luna",
 "architecture":{"modality":"text->text","input_modalities":["text"],"output_modalities":["text"]},
 "pks":{"provider":"foundry","enabled":true,"default":true,"roles":["text"],"status":"succeeded",
        "model":{"name":"gpt-5.6-luna","version":"2026-06-01"},
        "capabilities":{"chat_completion":true,"embeddings":false}}}
```

`pks.capabilities` are Azure's own flags for the base model and are present only when the resource's `/openai/models` list could be read; the catalog is still complete without them.

## 4. Filter by role

```bash
pks foundry models --role text
pks foundry models --role decision
```

A role names a job in a companion tool's manifest. Two are assigned today, both from `pks foundry select`: the default model is `text`, and the voice-classifier model is `decision`. A tool such as heypoul takes the `decision` deployment as the judge for spoken answers, so pick a Decision model (for example `Decision-1`) there; a chat model in that slot is ignored by tools that need the Decision protocol.

## 5. From a tool started by pks exec

```bash
curl -s -H "Authorization: Bearer $FOUNDRY_TOKEN" "$FOUNDRY_ENDPOINT/pks/v1/models"
```

with the `{proxy:endpoint}` and `{proxy:token}` the manifest bound. The route is answered by the proxy itself and never reaches Azure; a `502` with `catalog unavailable: …` means the deployments list could not be read. A tool started with the `openai-compatible` provider reads the same route and gets that endpoint's models instead — the route is pks's on every provider kind, never the upstream's.

## 6. Verify

- The `★` row matches the default `pks foundry status` prints.
- `--role decision` lists exactly the deployment `pks foundry select` shows under *voice-classifier model*, or nothing when none was chosen.

## Related

- [Change resource and model selection](/tools/pks/foundry/select) — where enabled, default and the voice classifier are set
- [pks exec](/tools/pks/exec) — how the catalog reaches a companion tool
