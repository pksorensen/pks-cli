---
id: FT-012
title: Brain — cross-project session knowledge graph
domain: knowledge
status: draft
adrs: []
tests: []
source-files: [src/Commands/Brain/, src/Infrastructure/Services/Brain/]
sessions: [db1daceb-3ef0-4dab-8f2e-1523508106b2, 64fd343a-ff75-446b-b5e9-349427867e0c, e16fa8b7-7344-4fa8-89d7-fa0913b6b919, da2af576-277c-44e8-94c6-73170c6bf047, 6ee255bd-6de0-40a5-9331-a45c8c76f00f]
---

## Description
`pks brain` is a generic, user-scoped capability that ingests every Claude Code session JSONL the user has on disk — across all projects, not just this repo — and crunches it into a layered knowledge graph. Phase 1 (`brain ingest`) is fully deterministic: it scans `~/.claude/projects/**` and emits firehose rows (prompts, tools, files, errors) plus a per-session metadata index into `~/.pks-cli/brain/`. Phase 2 (`brain extract`) runs an editable `brain-extract` skill against each session to produce a markdown summary; Phase 3 (`synth`) clusters the extracts; Phase 4/5 (`wiki`, `adr`) render wiki pages and ADRs per detected cluster/project. The repo `.pks/brain/` is also git-aware so per-project artefacts (feature-specs, ADRs) land beside the code that produced them — this repo is the first consumer, but the design is for every project the user touches.

## Intent
> Jeg tror rigtig meget på den her "start vibe" => "go professionel" — og jeg har mange af mine projekter jeg starter uden noget som helst. Men jeg vil også gerne have alt det nince engineering ned af vejen men jeg gider ikke betale overhead i starten hvis jeg ikke ved om projektet bliver til noget. … jeg leger meget med tanken at … jeg bare kan have en background agent der står og cruncher det for at så på bagsiden faktisk bygge ADR, feature besrkivelser og whatnot op automatisk løbende uden jeg behøver at tænkte over det, jeg viber bare der ud af.

From session db1daceb (2026-05-14), prompt: the founding idea — drift from L2→L4 without paying engineering overhead up front.

> samme concept, jeg vil gerne have en agent til at stå og snore over "hvad kan jeg opsamle af bad habbits fra poul og give ham gode råd" — det kan jo også være. "Poul gør det her hver dag, skal vi bygge et skill til ham og send næste gang du gør det der så prøv xxx"

From session db1daceb (2026-05-14), prompt: secondary intent — habit-mining and skill-suggestion loop on top of the same firehose.

> i think pks-cli should deterministic extract all the tool calls into a compact format so its easy for ai to analyse so we dont waste tokens on reading everyhing — we are interesting in finding all the bottlenecks, errors, problems. token usage , what else makes sense here … 4. We will start working on feature, adr, specs more formal … i think based on the history of the project we can reconstruct alot of feature descriptions and user stories of what things can do.

From session db1daceb (2026-05-14), prompt: shape of the pipeline — deterministic firehose first, AI synthesis on top, ADR/feature reconstruction as the long-tail payoff.

## Key decisions
- **Two-tier store**: `~/.pks-cli/brain/` holds the cross-project firehose + per-session metadata; `./.pks/brain/` inside a git repo holds the synthesised wiki/ADRs/feature-specs for *that* project. Initialisation in a non-repo directory still works — only the project-local tier is skipped.
- **Phase split is deliberate and resumable**: `ingest → extract → synth → wiki → adr`. Each phase has its own command and its own cursor file so the user can rerun any single phase, and `brain refresh` chains them. Rationale: keeps the cheap deterministic step decoupled from the expensive AI step.
- **Skill-driven prompts**: the extract/synth/wiki prompts live as editable skills (see `BrainSkillReader`, `make-skill`) rather than hard-coded strings. Rationale stated in-session: "We set it up in a skill … so we can edit it." Same pattern reused for synth and wiki so each consumer can tune what the model emits.
- **Project detection is data-driven, not configured**: the synthesis pipeline clusters sessions by tools/files/projects, so wiki pages auto-organise across sub-projects without the user having to declare them. Cited directly: "smart detect different tools, projectss and such based on data."
- **Tool calls and prompts get deterministic firehoses up front** (`tools.jsonl`, `prompts.jsonl`, `files.jsonl`, `errors.jsonl`) so the AI phases consume compact rows instead of re-reading raw JSONL — explicit token-budget decision.

## Gotchas / known issues
- Session-file basenames in the firehose come in two shapes: real Claude Code UUIDs and synthetic `agent-*` IDs from sub-agent runs. The extract/synth phases must filter to UUID sessions (matches the regex in this spec's `sessions:` array) — the `agent-*` transcripts are short-lived and have no standalone narrative.
- The brain operates over `~/.claude/projects/**` which is mutable: Claude Code rotates session files after ~30 days by default. Spec author flagged this in-session ("default er at man har alt session files i claude 30dage, kan extendes") — extracts must be durable independently of the source JSONL still being on disk.
- `pks brain scan` was referenced in the backwatch tasking but is **not** a top-level command; the implemented surface is `brain ingest` (+ `search`, `status`). `BrainScanFilepathCommand` exists but is wired as an internal helper, not a public verb. Documentation drift to clean up.
- Cross-project ingest means session content from unrelated repos is co-mingled in `~/.pks-cli/brain/`. No masking is applied at this layer — sensitive prompts/tool outputs from any project are readable by any future `pks brain search`. Treat the brain directory as user-private.
