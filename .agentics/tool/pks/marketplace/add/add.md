---
title: "Add a marketplace"
description: "Register a Claude Code plugin marketplace from an HTTPS URL or a github:owner/repo source, apply its policy, and choose which plugins are enabled."
tags: [how-to, cli, claude, plugins]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks marketplace add <SOURCE> [options]"
examples:
  - command: "pks marketplace add https://marketplace.agentics.dk/ctx/ctx-core"
    description: "Register an internal marketplace interactively"
  - command: "pks marketplace add github:owner/repo --enable-all --non-interactive"
    description: "Register a GitHub source with every plugin enabled"
  - command: "pks marketplace add github:owner/repo@v2 --non-interactive"
    description: "Pin to a tag and register all plugins disabled"
---

Register a plugin marketplace so pks tracks it locally: point `add` at a URL or a GitHub repository, let it fetch the `marketplace.json`, and choose which of the declared plugins start out enabled. This is the only subcommand that talks to a remote source and the only one that can create a new entry.

## 1. Prerequisites

- **A reachable marketplace source.** Either an HTTPS URL serving a `marketplace.json`, or a public GitHub repository with `marketplace.json` at its root.
- **A `name` field in that document.** `add` requires a non-empty top-level `name` — Anthropic's schema field. Without it the command fails and writes nothing.
- **Public access.** The fetch is unauthenticated, so private repositories and URLs behind auth cannot be added.

## 2. Choose the source form

Two source forms are accepted.

A URL is used verbatim:

```bash
pks marketplace add https://marketplace.agentics.dk/ctx/ctx-core
```

The `github:` shorthand resolves to a raw file on `raw.githubusercontent.com`, with `main` as the ref when you do not name one:

```bash
pks marketplace add github:owner/repo          # ref defaults to main
pks marketplace add github:owner/repo@v2       # pinned to the v2 ref
```

Pin the ref when you want a stable plugin list. A moving branch means a later refresh can pull in unrelated upstream changes.

## 3. Add interactively

Run `add` with no extra flags and pks fetches the document, then presents a multi-select checkbox of the plugins it declares:

```bash
pks marketplace add https://marketplace.agentics.dk/ctx/ctx-core
```

Select the plugins you want enabled and confirm. Plugins you leave unchecked are still registered — they are recorded as disabled and can be turned on later with `pks marketplace enable`.

## 4. Add without prompts

For scripts and CI, `--non-interactive` skips the checkbox entirely. On its own it registers every plugin disabled:

```bash
pks marketplace add github:owner/repo@v2 --non-interactive
```

Add `--enable-all` to register every plugin enabled instead:

```bash
pks marketplace add github:owner/repo --enable-all --non-interactive
```

`--enable-all` has no effect without `--non-interactive`. In an interactive run the checkbox decides.

## 5. Label the entry

The entry is keyed and displayed by the `name` from the fetched document. Pass `--label` to give it a different display label:

```bash
pks marketplace add github:owner/repo --label "Internal tooling" --non-interactive
```

## 6. Verify

List the registry and confirm the new entry and its enabled count:

```bash
pks marketplace list
```

The marketplace appears with its ID, label, source, an enabled-of-total plugin count, and the date it was added. Then read the per-plugin detail:

```bash
pks marketplace show my-marketplace
```

## Options

| Flag | Description |
|---|---|
| `--label <LABEL>` | Optional display label for the marketplace. |
| `--non-interactive` | Skip interactive prompts. |
| `--enable-all` | Enable all plugins when adding. Only used together with `--non-interactive`. |

| Argument | Required | Description |
|---|---|---|
| `SOURCE` | yes | Marketplace source: a URL, `github:owner/repo`, or `github:owner/repo@ref`. |

## Policy documents

For URL sources only, `add` also tries to fetch a policy document at `{url}/policy`. When it succeeds, plugins the policy marks `required` or `installed-default` are force-enabled, and `required` plugins are additionally flagged as non-disableable before the interactive selection runs on whatever remains.

> **Note.** Policy fetching never happens for `github:` sources, and any failure — a 404, a timeout past ten seconds, or invalid JSON — is swallowed. The policy is skipped with no message, so a silent absence of policy enforcement looks identical to a source that has no policy.

## Troubleshooting

**"fetched marketplace.json has no `name` field".** The source document is missing the top-level `name` that Anthropic's schema defines. Nothing is written. Fix the source document; `id` is not a substitute.

**The fetch fails on a private repository or a protected URL.** No credentials are sent with the request. Host the document somewhere publicly readable, or serve it from a URL that does not require authentication.

**A previously registered marketplace was replaced.** Entries are keyed on the fetched `name`, compared case-insensitively, and re-adding the same name overwrites the existing entry rather than merging or erroring. Any manual enable and disable curation on that entry is lost.

**`--enable-all` appeared to do nothing.** It is read only when `--non-interactive` is also present. Pass both flags together.

**Required plugins came back disableable.** Policy flags are applied during `add` only. `pks marketplace disable` does not check them, and `pks marketplace refresh` rebuilds the plugin snapshot without them.

## Next steps

- [List and inspect marketplaces](/tools/pks/marketplace/list) — confirm what landed in the registry
- [Enable and disable plugins](/tools/pks/marketplace/enable) — change the selection you made here
- [Refresh marketplace sources](/tools/pks/marketplace/refresh) — pick up later changes to the source document
- [pks marketplace CLI reference](/tools/pks/marketplace/reference) — the full argument and flag surface
