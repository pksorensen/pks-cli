---
id: FT-015
title: Vibecast — agentic-operator broadcast (+ Voice)
domain: broadcast / alp-operator
status: draft
adrs: []
tests: []
source-files: [src/Commands/Vibecast/VibecastCommand.cs, src/Commands/Vibecast/VibecastGameCommand.cs, src/Commands/Voice/, src/DevcontainerSpawnCommand.cs]
sessions: [64fd343a-ff75-446b-b5e9-349427867e0c, f7a4850a-b97b-43f9-867d-a1b4849ef95b, e16fa8b7-7344-4fa8-89d7-fa0913b6b919, af8f146f-a46e-4f3e-8d0e-71e318904b67, 024c4bd0-17c6-4b26-90c9-6d16198defab]
---

## Description

Vibecast is a separate Go tool tightly integrated as the *operator* of the ALP
(Agentic Live Protocol) spec inside the agentic runner: it captures the agent's
tmux/ttyd terminal, opens the broadcaster WebSocket against `agentics.dk`, and
emits the metadata/session events that drive the viewer page. `pks vibecast`
(and `pks vibecast game`) wrap the same `DevcontainerSpawnCommand` plumbing that
`pks claude` uses, so the broadcaster runs *inside* the spawned devcontainer
with foundry MSI tokens, marketplace plugins, and a `--session-id` /
`--broadcast-id` / `--attr` argument surface plumbed through `os.Args` parsing
into the stream config. Voice (`heypoul`) is the push-to-talk surface bundled
alongside: a small Go binary launched by `pks voice` that talks to Azure AI
Foundry realtime speech and dictates into the same terminal — making Vibecast
the broadcast half and Voice the input half of the same operator experience.

## Intent

> New Idea. I want to use a voice to dictate my computer. i am looking at those
> realtime trascription or fast trascripions. I want to build a copy of whisper
> flow ect - and we tryed with hviske-v2 but found out it was to slow. I leaed
> that people use services, so i am looking at foundry since i have acces sthere.
> ... should do another attempt to do a program in sandbox called heypoul ? we
> will jsut do it in go so i can be small and it can talk with the foundry
> models. ... I basiicaly want to launch it with pks-cli voice which then let
> me pick any configuration needed and then that starts the little go program
> that does the actually processing and drawing that voice indication when push
> to talk

From session 64fd343a (2026-05-13), prompt above.

> You are implementing three things for the vibegame feature: (1) a generic
> --attr flag system in vibecast (Go), (2) a pks-cli vibecast game subcommand
> (C#), and (3) a vibegame Claude plugin in pks-claude-plugins. Work in
> /workspaces/agentic-live-www. ... Find where `os.Args` is parsed (around
> lines 64-90 where `--session-id` and `--broadcast-id` are parsed). Add
> --attr parsing in the same style ... Then pass `attrs` and `pluginNames` to
> the stream runner.

From session d30fe666 (2026-05-04), prompt above.

> We have gotten our pks-cli claude to work which spawn a claude. It can use
> foundry tokens and all that. With a marketplace url to install plugins. All
> good. ... I would like to test a prototype for pks claude where same as
> token service for getting foundry tokens, ... to implement such we copy over
> the tokens to the vm running the docker with devcontainer at ~/.pks-cli
> similar we did for foundry. then i want to run pks-cli as a proxy there that
> can be mounted into the devcontainer

From session 024c4bd0 (2026-05-04), prompt above.

## Key decisions

- **Vibecast inherits from `DevcontainerSpawnCommand`** (sibling to
  `ClaudeSpawnCommand`): the broadcaster is spawned inside the same
  devcontainer the agent runs in, so foundry MSI tokens, marketplace plugins,
  and SSH target plumbing are reused — not re-implemented in Go.
- **Go for the wire, .NET for the spawn**: the actual ALP operator
  (capture, ttyd, ws push to `agentics.dk`) stays in `external/vibecast` for
  footprint and standalone distribution; `pks vibecast` is only the launcher.
  Same rationale applied to `heypoul` for Voice (small Go binary, .NET picks
  the foundry config and starts it).
- **`--attr` is the extension point**: rather than baking game/plugin
  knowledge into vibecast, a generic `--attr key=value` array is parsed at the
  same layer as `--session-id` / `--broadcast-id` and forwarded into the
  stream config — `vibecast game` and the vibegame Claude plugin ride on top.
- **Voice (heypoul) is bundled but separate**: launched via `pks voice`, not
  embedded in vibecast, because push-to-talk + sound indicator is a desktop
  surface that should run on the host while the broadcast/agent run inside
  the devcontainer.
- **Reuse `BuildFoundryEnvArgsAsync`**: Voice and Vibecast both need foundry
  tokens (realtime speech for Voice, model access for the agent that
  Vibecast broadcasts), so both go through `DevcontainerSpawnCommand`'s MSI
  env-arg builder rather than reading credentials independently.

## Gotchas / known issues

- Realtime transcription via `hviske-v2` was tried first and rejected as too
  slow — Voice depends on Foundry-hosted speech being available; falling back
  to local is not viable.
- The vibecast binary in `external/vibecast/` is rebuilt on every restart and
  always reflects current source — do not blame "old binary" when diagnosing
  broadcast/operator issues (echoed in repo CLAUDE.md).
- `os.Args` parsing in `external/vibecast/cmd/root.go` is hand-rolled around
  the `--session-id` / `--broadcast-id` / `--attr` triple; new flags must
  follow the same pattern or the stream config will drop them silently.
- ttyd + tmux are required at runtime on the broadcasting host — Vibecast
  assumes they are present in the devcontainer image, not bootstrapped.
