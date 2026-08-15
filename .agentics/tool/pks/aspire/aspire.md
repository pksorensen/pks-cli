---
title: "pks aspire"
description: "Start a .NET Aspire AppHost with its declared parameters already resolved from what you are signed in to, instead of pasting endpoints and keys into prompts."
tags: [reference, cli, aspire, providers]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks aspire <run|init> [options] [-- <apphost args>]"
examples:
  - command: "pks aspire init src/apphost"
    description: "Write PksDeclare.cs into an AppHost so it can say what it needs"
  - command: "pks aspire run -- --ai"
    description: "Start the AppHost, forwarding --ai to it, with its parameters resolved"
  - command: "pks aspire run --dry-run -- --ai"
    description: "Show what would be set, and start nothing"
---

`pks aspire` runs a .NET Aspire AppHost without anybody typing a credential into it.

An AppHost that needs a model endpoint, a key and a deployment name declares them as parameters, and Aspire's honest answer is to stop and ask — every run, or once into user secrets, where the key then lives in plaintext on a laptop for as long as the project does. `pks aspire run` removes the question: the composition says what *kind* of thing it needs, and pks fills it from whatever this machine is already signed in to.

It is the same two-phase handshake as [`pks exec`](../exec/exec.md), with the AppHost's pipeline standing in for `PKS_DISCOVERY=1`.

## The two passes

1. **Declare.** `aspire do pks-declare` builds the AppHost and executes one pipeline step. The step has no dependencies and no resources behind it, so nothing starts — no container, no app, no port. It walks the resource model and writes a `v1` manifest to the file named by `PKS_DECLARE_OUT`.
2. **Run.** pks resolves the manifest — provider per capability, model per role, placeholders expanded — and starts `aspire run` with the answers in its environment as `Parameters__<name>`, which is the first place Aspire looks. The parameters resolve silently and nothing is written to disk.

An AppHost without the step still works. The first pass fails, `pks aspire run` says so, and the run continues exactly as `aspire run` would have.

## Commands

### `pks aspire init [APPHOST]`

Writes `PksDeclare.cs` into the AppHost project. One file, no package reference and no project reference — an AppHost cannot take a dependency on pks-cli, so the contract travels as source. The copy is then that repository's file; `--force` replaces it.

`APPHOST` is a project file or a directory containing exactly one. A directory with several is an error rather than a guess.

### `pks aspire run [-- <apphost args>]`

| Option | Meaning |
| --- | --- |
| `--apphost <PATH>` | Project file or directory, passed through to `aspire` |
| `--provider <KIND>` | Skip the provider prompt: `foundry`, `gemini`, `openai-compatible` |
| `--port <N>` | Bind the managed-identity proxy to a fixed port |
| `--non-interactive` | Take the default for every question — for CI |
| `--dry-run` | Declare, resolve, print what would be set, start nothing |
| `--start` | Use `aspire start` (detached) rather than the foreground `aspire run` |

Everything after `--` goes to the AppHost — **and to both passes.** This matters: an AppHost that only declares its model parameters behind `--ai` will declare nothing if the first pass does not get the flag too.

## Declaring, in the AppHost

```csharp
var aiBaseUrl = builder.AddParameter("ai-base-url");
var aiApiKey  = builder.AddParameter("ai-api-key", secret: true);
var aiModel   = builder.AddParameter("ai-model", new SuggestedValue("gpt-4o-mini"));

builder.AddPksCapability("chat", "The model that writes the answer on Overview")
       .Offers("foundry", "Azure AI Foundry — sign in once with `pks foundry init`")
       .Offers("openai-compatible", "Anything OpenAI-compatible: a local Ollama, a proxy")
       .Binds(aiBaseUrl, "{endpoint:openai}")
       .Binds(aiApiKey,  "{apikey}")
       .Binds(aiModel,   "{model:default}");
```

`Offers` names the kinds of provider that could fill the capability; pks shows only the ones you are signed in to. `Binds` sends one resolved value into one parameter.

### Placeholder vocabulary

| Placeholder | Resolves to |
| --- | --- |
| `{endpoint}` | The chosen provider's endpoint URL |
| `{endpoint:openai}` | The same, in the shape an OpenAI client can be pointed at |
| `{apikey}` | A key for that endpoint, where the provider has one |
| `{model:<role>}` | The model chosen for a named role |
| `{imds:endpoint}` | A loopback managed-identity proxy pks starts for the run |
| `{imds:header}` | That proxy's per-run secret |

Anything else passes through as a literal. A role is discovered from the bindings — binding `{model:default}` is how the composition says there is a role called `default` to ask about.

A capability is optional by default. `AddPksCapability(name, description, required: true)` makes a missing provider stop the run instead of skipping the capability.

## What gets reported

The manifest carries every parameter in the model, not only the bound ones, with whether pks can fill it and whether it already has an answer. `pks aspire run` prints the ones that are neither — a tenant id, a connection string — so a run that is about to stop and ask says so up front rather than after the build.

Values are never in the manifest. Names, descriptions and two booleans; the file lands on disk, and a value there would be a credential on disk.

## Traps

- **`AddParameter("ai-model", "gpt-4o-mini")` cannot be filled by anything.** The string overload pins the value and stops consulting configuration, so the environment variable is ignored without a word and the parameter keeps its old value while everything reports success. Use `new SuggestedValue("gpt-4o-mini")`, which is the default the reading suggests.
- **The declare pass runs in publish mode.** An AppHost that branches on `ExecutionContext.IsPublishMode` declares a different set of parameters than the run will. Register parameters in both modes; gate only the wiring.
- **No shell can export `Parameters__ai-base-url`.** A dash is not a legal variable name to `bash` or `zsh`. pks sets it on the child process directly, which works; a hand-written `export` does not.

## See also

- [`pks exec`](../exec/exec.md) — the same handshake for a command-line tool
- [`pks foundry`](../foundry/foundry.md) — signing in, so there is something to resolve against
