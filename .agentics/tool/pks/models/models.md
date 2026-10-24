---
title: "List the model catalog"
description: "Print the models reachable through every provider registered with pks — Foundry, Anthropic, OpenAI, Gemini, Moonshot, TypeSafe, an OpenAI-compatible endpoint — as the catalog pks exec serves to companion tools at /pks/v1/models and routes at /pks/v1/chat/completions."
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

pks is the gateway between a tool and the model providers you registered with it, and this is the gateway's catalog: one list of every model reachable through those providers, each entry saying which provider it comes from. A tool started by [pks exec](/tools/pks/exec) reads the same document at `GET {proxy:endpoint}/pks/v1/models` — served by pks itself, never forwarded to a provider — and can send any text model in it to `POST {proxy:endpoint}/pks/v1/chat/completions`, which pks routes to the provider that owns it. So a tool offers a model picker without a prompt, without a second credential and without seeing any credential at all. (`pks model`, singular, is the on-device speech models; this command is the cloud catalog.)

## 1. Which providers are registered

The catalog follows the same test `pks exec` applies when it offers a provider:

| Provider | Registered when | Listed from |
|---|---|---|
| `foundry` | `pks foundry init` completed and a resource is selected | the resource's deployments, joined with what [pks foundry select](/tools/pks/foundry/select) chose |
| `anthropic` | `pks providers init --only anthropic` stored a key, or `ANTHROPIC_API_KEY` is set | the Claude API's `/v1/models`, every page |
| `openai` | `pks providers init --only openai` stored a key, or `OPENAI_API_KEY` is set without `OPENAI_BASE_URL` | `api.openai.com/v1/models` |
| `gemini` | `pks google init` stored a key, or `GEMINI_API_KEY` is set | the Gemini API's `/v1beta/models` (the key travels in a header, not in the URL) |
| `moonshot` | `pks moonshot init` stored a key | `api.moonshot.ai/v1/models` |
| `typesafe` | `pks typesafe init` stored a key | TypeSafe's `/v1/models` — judges (`text->choice`), listed but not chat-routable |
| `openai-compatible` | `OPENAI_BASE_URL` is set (`OPENAI_API_KEY` where the endpoint wants one) | the endpoint's own `/models` listing, OpenRouter extras kept |

The order of this table is the catalog order, and it matters once: a bare model id that two providers both list resolves to the first of them (section 5). A stored key wins over the environment variable of the same provider. With nothing registered the command exits 1 with a pointer to each. A provider that is registered but cannot be listed is reported under the table and does not hide the others; the union is held for two minutes so a tool that asks per utterance does not fan out a listing per provider each time.

## 2. Read the table

```bash
pks models
```

| Column | Meaning |
|---|---|
| Model | The name a request's `model` field takes at that provider. `★` marks the default from `pks foundry select` |
| Provider | one of the kinds in section 1 — prefix a request's `model` with it (`gemini/gemini-2.5-flash`) to pin the provider |
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

`--provider` names a registered kind and fails with a hint when that kind is not registered. A role names a job in a companion tool's manifest; two are assigned today, both from `pks foundry select`: the default model is `text`, and the voice-classifier model is `decision`. Only Foundry carries roles for now, so a `--role` filter over any other provider is empty.

## 5. From a tool started by pks exec

```bash
curl -s -H "Authorization: Bearer $PROXY_TOKEN" "$PROXY_ENDPOINT/pks/v1/models"

curl -s -H "Authorization: Bearer $PROXY_TOKEN" -H "Content-Type: application/json" \
  -d '{"model":"gemini/gemini-2.5-flash","messages":[{"role":"user","content":"Ryd op i denne diktat …"}]}' \
  "$PROXY_ENDPOINT/pks/v1/chat/completions"
```

Whichever provider the tool's manifest bound the proxy to, the catalog is the whole union, and the chat route goes wherever the model lives: pks looks the `model` up, rewrites it to the provider's own name, signs the request with that provider's credential and relays the answer — including `stream: true` server-sent events — through the provider's OpenAI-compatible chat endpoint (Foundry's `/openai/v1`, Gemini's `/v1beta/openai`, Anthropic's `/v1/chat/completions`, and so on). The body is otherwise sent as the tool wrote it, so a parameter one provider rejects is rejected by that provider, in that provider's words.

Naming a model:

- `gemini/gemini-2.5-flash` — `provider/id` pins the provider. The prefix is stripped only when it names one of the kinds in section 1, so an OpenRouter id such as `openai/gpt-5.5` on an `openai-compatible` endpoint still arrives whole; write `openai-compatible/openai/gpt-5.5` to pin it.
- `gpt-5.2` — a bare id resolves to the first provider in catalog order that lists it.

The route answers with an OpenAI-shaped error, never by forwarding, when it cannot route: `404 model_not_found` for an id not in the catalog, for a pinned provider that is not registered, and for a TypeSafe judge (which has no chat route; a tool wanting a verdict posts `/v1/systemone` to its bound provider as before). Both routes are pks's on every proxy kind; a `502` with `catalog unavailable: …` carries the reason when no provider at all could be listed.

## 6. Verify

- `pks models --provider foundry --json` and `pks foundry models --json` print the same document.
- With no Google key stored and `GEMINI_API_KEY` unset, `pks models --provider gemini` exits 1 with `pks google init, or set GEMINI_API_KEY`.
- Through an exec proxy, `POST /pks/v1/chat/completions` with `{"model":"no-such-model"}` is `404` with `"code":"model_not_found"`, and nothing reaches the bound provider.

## Related

- [pks foundry models](/tools/pks/foundry/models) — the Foundry slice, with the enabled/default/roles selection explained
- [pks exec](/tools/pks/exec) — how the catalog reaches a companion tool
