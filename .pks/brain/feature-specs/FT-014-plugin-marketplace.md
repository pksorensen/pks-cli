---
id: FT-014
title: Plugin marketplace
domain: plugin-marketplace
status: draft
adrs: []
tests: []
source-files: [src/Commands/Marketplace/MarketplaceAddCommand.cs, src/Commands/Marketplace/MarketplaceListCommand.cs, src/Commands/Marketplace/MarketplaceShowCommand.cs, src/Commands/Marketplace/MarketplaceEnableCommand.cs, src/Commands/Marketplace/MarketplaceDisableCommand.cs, src/Commands/Marketplace/MarketplaceRefreshCommand.cs, src/Commands/Marketplace/MarketplaceRemoveCommand.cs, src/Commands/Marketplace/MarketplaceBranchCommand.cs]
sessions: [024c4bd0-17c6-4b26-90c9-6d16198defab, af53e229-8629-4bff-869e-b78e8e75accf, 76407aa4-d895-44c9-b6bc-b4f24832785c, a160e3cc-2a06-4df0-acc3-8c686583a4fd, a0df4d99-b4d5-4ffe-87af-0c9b7daa4ba4]
---

## Description

The `pks marketplace` branch registers Claude Code plugin marketplaces with the host and applies their policies to the local install. It supports `add`, `list`, `show`, `enable`, `disable`, `refresh`, and `remove` subcommands, persisting state to `~/.pks-cli/claude-marketplaces.json` (modelled on `SshTargetConfigurationService`). `add` parses multiple source forms — `url`, `github` shorthand (`owner/repo[@ref]`), `git+ssh://…#ref`, plus local `directory` / `file` — mirroring Claude Code's own `claude plugin marketplace add` syntax, fetches the marketplace manifest, then pulls `<url>/policy` to auto-enable `installed-default` plugins and lock `required` ones. The branch was lifted from `pks claude marketplace` to a top-level `pks marketplace` so spawned devcontainers (via `pks vibecast` / `pks claude`) can mount marketplace URLs into `/etc/claude-code/managed-settings.json` via `extraKnownMarketplaces`.

## Intent

> "pks claude marketplace add https://marketplace.agentics.dk
> so when we spawn a devcontainer with pks vibecast or pks claude ect -  we mount in the Linux and WSL: /etc/claude-code/  folder and setup settinsg to include our marketplace url so its avaible when working with claude on the devcontainer.
> there are extraKnownMarketplaces  that should be able to be used from managed-settings.json  - cant you look into documentation for claude code how we can use that?"

From session a160e3cc (2026-05-03), prompt:

> "# Task: Move pks-cli marketplace from `claude marketplace` to top-level `marketplace` + add policy support
> 1. Lift `pks marketplace` out from under `pks claude marketplace` to be a top-level command: `pks marketplace add|list|show|...`
> 2. Update `marketplace add` to fetch the policy endpoint (`<url>/policy`) after adding the marketplace and apply it — auto-enabling `installed-default` plugins and marking `required` ones as locked."

From session 76407aa4 (2026-05-03), prompt:

> "You are a child Claude Code session. Your single task is to implement the `pks claude marketplace` feature in pks-cli, end-to-end and TDD-first … **Source-input parser** for `add <source>`: handle `url`, `github` (`owner/repo[@ref]`), `git` (`git+ssh://…#ref`), `directory`, `file`. Mirror Claude Code's own `claude plugin marketplace add` syntax."

From session a0df4d99 (2026-05-03), prompt.

## Key decisions

- **Top-level command, not nested under `claude`.** Marketplaces are consumed by `pks vibecast`, `pks claude`, and devcontainer-mount flows, so the branch lives at `pks marketplace` rather than `pks claude marketplace` (per 76407aa4).
- **Host-wide store at `~/.pks-cli/claude-marketplaces.json`.** Pattern copied from `SshTargetConfigurationService`; constructor accepts an optional `configPath` so unit tests can target a temp dir.
- **Multi-form `add <source>` parser.** Mirrors `claude plugin marketplace add` syntax (`url` / `github` shorthand / `git+ssh` / `directory` / `file`) so users can reuse muscle memory; the fetcher branches per form (HTTP, raw-GitHub URL synthesis, `git ls-remote` + sparse clone, file read).
- **Policy auto-apply on add.** After registering a marketplace, fetch `<url>/policy` and apply: `installed-default` plugins are auto-enabled, `required` plugins are marked locked. Keeps managed installs declarative.
- **DevContainer injection via `extraKnownMarketplaces`.** Registered URLs are written into `/etc/claude-code/managed-settings.json` when spawning devcontainers, so the Claude inside the container sees the same marketplaces as the host.

## Gotchas / known issues

- Stale marketplace URLs are a recurring pain — earlier port-locking workaround referenced in session d8ac83e9; `refresh` exists to force-redownload manifests when the upstream changes.
- After changing a marketplace, a re-fetch step is required for updates to surface (session b9878de6: "last time we chagned the marketplace we had to do soemthing for it to return the updat…").
- Top-level move means older docs / muscle memory pointing at `pks claude marketplace` no longer resolve — no alias was kept.
