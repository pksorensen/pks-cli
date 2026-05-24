---
id: FT-010
title: Exec — protocol for tools requesting model/AI access
domain: agentic-runtime
status: draft
adrs: []
tests: []
source-files: [src/Commands/Exec/PksExecCommand.cs]
sessions: [808c2e3d-f1ee-4ddf-920a-93c69080374a, 99d43188-9dec-41ea-afb7-3d91da321907, 67ce78cd-d99e-4b15-a5dc-df9d16d734f7, cd04962e-09d1-4d84-a6bb-69df5ede0f04, 07b6cd63-ef0f-4da8-a1af-2347f743b091]
---

## Description
`pks exec <tool> [args...]` is a generic wrapper that lets any external tool ask
pks-cli for access to a model/AI provider without baking credential handling into
the tool itself. The tool is invoked once with `PKS_DISCOVERY=1` and must emit a
v1 JSON capability manifest on stdout; pks-cli then prompts the user for provider
and per-role model choices, starts a local managed-identity (IMDS) proxy if
needed (FT-005), resolves a placeholder vocabulary
(`{endpoint}`, `{apikey}`, `{imds:endpoint}`, `{imds:header}`, `{model:<role>}`)
in the manifest's env bindings, and re-execs the tool with the composed
environment. Providers currently recognised are `foundry` (FT-004), `gemini`
(env-keyed) and `openai-compatible` (env-keyed). This is the single seam through
which sibling Go CLIs like `pks-agent-photographer` and `pks-agent-photofly`
obtain model access — they ship only a manifest, never a token.

## Intent
> From session 99d43188 (2026-05-15), prompt:
> "So we make a pks foundry exec \"path to some other exe\" args to other tool which will wire up the foundry token proxy and then just start that other tool setting all the environment variables that is needed for it to talk with foundry. this way we can make it easy to start by pks-cli foundry exec pks-agent-photographer.exe ? any feedback on this?"

> From session 99d43188 (2026-05-15), prompt:
> "could we make this even more generic, lets move the exec up in pks so its \"pks exec tool.exe\" and pks exec will call the tool.exe args --help with an Environment variable \"PKS_AGENTICS_DISCOVERY=true\" which is a way to indicate that the tool should report its capability/requirments in some json output that pks-cli can read and understnad and then call it again injecting the needed environments when it call the tool correct."

> From session cd04962e (2026-05-18), prompt:
> "look at how we in pks-cli designed a discovery model and pks-cli exec <tool> call thata can provide the AI model access. i wrote up a blog post here about it also: /workspaces/agentic-live-www/blog-posts/pks-cli-discovery-protocol"

## Key decisions
- **Two-phase exec**: discovery run (`PKS_DISCOVERY=1`, stdout-only JSON, 10s timeout, exit 0 required) is separate from the real run. Keeps the tool stateless and lets pks-cli reason about requirements before any side effects.
- **Manifest is the contract, not a library**: tools depend on a JSON shape (`manifestVersion: v1`, `capabilities[].providers[].models[]`) rather than linking a pks-cli SDK. Reference Go implementation lives in the sibling repo's `internal/pksmanifest` and is copied verbatim per project.
- **Placeholder vocabulary instead of env templates**: env bindings carry tokens like `{imds:endpoint}` and `{model:fast}` resolved by pks-cli at exec time. Adding a provider only requires extending the resolver, not the tool.
- **Generalised away from `pks foundry exec`**: original proposal scoped exec to foundry; promoted to a top-level command so gemini / openai-compatible / future providers all flow through one prompt UX and one set of placeholders.
- **Availability gating drives the picker**: `IsAvailable(kind)` short-circuits providers the user has no creds for (`IsAuthenticatedAsync` for foundry, env-var probe for the others), so the selection prompt only offers what will actually work.

## Gotchas / known issues
- Spectre.Console markup eats `[role]` in the model prompt label — code uses `[[role]]` escaping (`PksExecCommand.cs:251`) and the inline comment flags this as a footgun for anyone adding new prompt strings.
- Tools that emit log lines before their JSON manifest are tolerated by `IndexOf('{')` scanning, but anything after the first `{` is parsed as JSON — a stray trailing log line will fail decode rather than be ignored.
- No tests exist for this command yet (gap noted in front-matter `tests: []`); discovery-protocol regressions today are caught only by the consuming Go CLIs at runtime.
