---
title: "List the model catalog"
description: "Print the models reachable through every provider registered with pks — Foundry, Gemini, an OpenAI-compatible endpoint — as the catalog pks exec serves to companion tools at /pks/v1/models."
tags: [how-to, models, providers, scripting]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks models [--json] [--provider <KIND>] [--role <ROLE>]"
examples:
  - command: "pks models"
    description: "A table of every model across the registered providers"
  - command: "pks models --json"
    description: "The catalog document, exactly as /pks/v1/models serves it"
  - command: "pks models --provider gemini"
    description: "Only the models reachable through the Gemini key"
  - command: "pks models --role decision"
    description: "Only the models that fill the decision role"
---

pks is the gateway between a tool and the model providers you registered with it, and this is the gateway's catalog: one list of every model reachable through those providers, each entry saying which provider it comes from. A tool started by [pks exec](/tools/pks/exec) reads the same document at `GET {proxy:endpoint}/pks/v1/models` — served by pks itself, never forwarded to a provider — so it can offer a model picker without a prompt and without seeing a credential. (`pks model`, singular, is the on-device speech models; this command is the cloud catalog.)

## 1. Which providers are registered

The catalog follows the same test `pks exec` applies when it offers a provider:

| Provider | Registered when | Listed from |
|---|---|---|
| `foundry` | `pks foundry init` completed and a resource is selected | the resource's deployments, joined with what [pks foundry select](/tools/pks/foundry/select) chose |
| `gemini` | `GEMINI_API_KEY` is set | the Gemini API's `/v1beta/models` (the key travels in a header, not in the URL) |
| `openai-compatible` | `OPENAI_BASE_URL` is set (`OPENAI_API_KEY` where the endpoint wants one) | the endpoint's own `/models` listing, OpenRouter extras kept |

With nothing registered the command exits 1 with a pointer to each. A provider that is registered but cannot be listed is reported under the table and does not hide the others.

## 2. Read the table

```bash
pks models
```

| Column | Meaning |
|---|---|
| Model | The name a request's `model` field takes at that provider. `★` marks the default from `pks foundry select` |
| Provider | `foundry`, `gemini` or `openai-compatible` |
| Modality | `input->output`: `text->text` for chat, `audio->text` for transcription, `text->choice` for a Decision model, `text->embedding`, and so on |
| Status | Azure's provisioning status for a Foundry deployment; `available` elsewhere |
| Enabled | Ticked in `pks foundry select`; every model counts as enabled where no selection exists |
| Roles | `text` for the default model, `decision` for the voice classifier — both set in `pks foundry select` |

## 3. Use the document

```bash
pks models --json | jq '.data[] | select(.pks.enabled) | "\(.pks.provider)\t\(.id)"'
```

The shape is OpenAI's `GET /v1/models` list (`{object:"list", data:[{id, object:"model", created, owned_by}]}`) — the one convention every provider and OpenRouter share — with OpenRouter's `name`, `description` and `architecture{modality, input_modalities, output_modalities}` on top, and one `pks` object per entry for what no public catalog knows about your setup:

```json
{"id":"gemini-2.5-pro","object":"model","owned_by":"gemini","name":"Gemini 2.5 Pro",
 "architecture":{"modality":"text+image->text","input_modalities":["text","image"],"output_modalities":["text"]},
 "pks":{"provider":"gemini","enabled":true,"default":false,"roles":[],"status":"available",
        "model":{"name":"gemini-2.5-pro","version":"2.5"},
        "capabilities":{"generateContent":true,"countTokens":true},
        "limit":{"context":1048576,"output":65536}}}
```

`owned_by` and `pks.provider` both carry the provider kind. `pks.capabilities` are the provider's own flags (Azure's base-model capabilities, Gemini's `supportedGenerationMethods`) and `pks.limit` its stated token limits; both are present only when the provider's listing had them.

## 4. One provider, one role

```bash
pks models --provider foundry      # the same as pks foundry models
pks models --role decision
```

`--provider` names a registered kind and fails with a hint when that kind is not registered. A role names a job in a companion tool's manifest; two are assigned today, both from `pks foundry select`: the default model is `text`, and the voice-classifier model is `decision`. Only Foundry carries roles for now, so a `--role` filter over Gemini or an OpenAI-compatible endpoint is empty.

## 5. From a tool started by pks exec

```bash
curl -s -H "Authorization: Bearer $PROXY_TOKEN" "$PROXY_ENDPOINT/pks/v1/models"
```

Each exec proxy fronts one provider, so a tool gets the slice of this catalog that its proxy can route: the Foundry deployments under the `foundry` provider, the endpoint's models under `openai-compatible`. The route is pks's on every kind and never reaches the upstream; a `502` with `catalog unavailable: …` carries the reason the provider could not be listed.

## 6. Verify

- `pks models --provider foundry --json` and `pks foundry models --json` print the same document.
- With `GEMINI_API_KEY` unset, `pks models --provider gemini` exits 1 with `set GEMINI_API_KEY`.

## Related

- [pks foundry models](/tools/pks/foundry/models) — the Foundry slice, with the enabled/default/roles selection explained
- [pks exec](/tools/pks/exec) — how the catalog reaches a companion tool
