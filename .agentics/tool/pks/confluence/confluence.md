---
title: "Confluence page sync"
description: "Two-way sync between Confluence pages and local markdown, tracked in a private git workspace, with checkout, commit, and staged delete."
tags: [reference, confluence, cli, git]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks confluence <command> [options]"
examples:
  - command: "pks confluence init --space OptiDyna"
    description: "Skip the space picker; still prompt for dir and root page"
  - command: "pks confluence checkout"
    description: "Full sync: rewrite the workspace tree from Confluence"
  - command: "pks confluence checkout 12345678"
    description: "Check out a single page into _pending/ for editing"
  - command: "pks confluence commit"
    description: "Open the interactive picker and push selected changes"
  - command: "pks confluence delete 12345678"
    description: "Stage a page for deletion until the next commit"
---

`pks confluence` syncs Confluence pages to local markdown files tracked in a private git workspace, so you can edit documentation in an editor — or hand it to a coding agent — and review a diff before anything goes back to Confluence. It checks pages out into markdown under `_pending/`, and pushes selected local edits, creates, and deletes back through the Confluence API.

Every command reuses the Atlassian email and API token stored by `pks jira init`. There is no separate Confluence login, and the Confluence site URL is derived from those same stored credentials rather than a setting of its own.

## Prerequisites

- **`pks jira init` must have run first.** Confluence has no independent authentication; `init` and every other subcommand fail immediately with "Not authenticated" if no Jira credentials are stored.
- **A git repository.** `confluence init` walks up from the current directory to find the nearest `.git` and treats that as the project root. Run it from inside, or under, a git checkout.
- **`git` on `PATH`.** Every command — `init`, `checkout`, `commit`, `delete` — shells out to `git` directly for the workspace's own repository, so it must be resolvable on `PATH`.

## How the workspace works

`pks confluence init` creates a working directory (`./docs/confluence` by default) with its **own, separate** git repository, and adds that directory to the parent project's `.gitignore`. The workspace's config lives at the project root, in `.confluence/config.json`, found the same way `init` finds `.git` — by walking up from the current directory.

Inside the working directory, `_pending/` holds files waiting to be pushed — edited pages, brand-new pages queued with `id: new` frontmatter, and `<page-id>.delete` marker files staged by `confluence delete` — and `_committed/` holds files that have already been pushed. Every state-changing command auto-commits its result to the workspace's own git history, separately from Confluence's own page-version history, so `git log`/`git diff` inside the working directory is the audit trail of local edits.

> **Note.** Because the workspace repository is added to the parent project's `.gitignore`, none of this history is visible from the parent project's own git log. The workspace's internal `.gitignore` also excludes `.confluence/`, but that folder actually lives at the project root, not inside the working directory, so the exclusion has no effect unless `--dir` places the workspace at the project root.

## Synopsis

```text
pks confluence <command> [options]
```

```text
init [options]           Bootstrap a local editing workspace for this project
checkout [page]          Full sync, or check out one page into _pending/
commit [options]         Push selected pending edits, creates, and deletes
delete <page-id>         Stage a page for deletion (no API call until commit)
```

## init

Bootstraps a local Confluence editing workspace for the current git project. It verifies Jira authentication, lets you pick a Confluence space — fetched live and shown as a selection prompt, with a manual-entry fallback — and an optional root page to scope the sync, then creates the working directory with its own independent git repository, `_pending/` and `_committed/` subfolders, and a `.confluence/config.json` at the project root recording the space key, root page, site URL, and working directory. Run this once per project before any other `confluence` subcommand.

| Flag | Default | Description |
|---|---|---|
| `-s`, `--space <SPACE>` | — | Confluence space key, e.g. `OptiDyna`. Skips the space picker. |
| `--root-page <ROOT_PAGE>` | — | Root page ID to scope the workspace. Prompted for when omitted. |
| `-d`, `--dir <DIR>` | `./docs/confluence` | Working directory for the workspace. |
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--debug` | `false` | Show HTTP request/response detail for troubleshooting. |

```bash
pks confluence init --space OptiDyna --dir docs/confluence
```

> **Note.** If a workspace already exists (`.confluence/config.json` is found), `init` prompts to reinitialize, defaulting to No. Confirming does not merge the existing config — it re-prompts for every value and overwrites the file.

## checkout

Pulls Confluence content down to local markdown. With no argument, it does a full sync: it fetches the entire page tree under the workspace's configured root page, converts each page's storage-format HTML to markdown with YAML frontmatter nested under a top-level `confluence:` key (`id`, `version`, `space`, `title`, `parent_id`, `last_synced`), writes the tree into a folder hierarchy that mirrors the page tree (`<workdir>/<ancestor-slug>/…/<title-slug>/index.md`), fetches comments as read-only sidecar files, and auto-commits the result. With a page ID or title argument, it does a single-page checkout into `_pending/<slug>.md` for editing — including, if the page doesn't exist yet, preparing a brand-new local page that is only created on Confluence once you run `commit`.

| Argument | Required | Description |
|---|---|---|
| `page` | no | Page ID (numeric) or title to check out. Omit for a full sync. |

| Flag | Default | Description |
|---|---|---|
| `-c`, `--create` | `false` | Create the page on Confluence if it doesn't exist, non-interactively. |
| `-p`, `--parent <PARENT>` | root page | Parent page ID when creating a new page. |
| `--no-comments` | `false` | Skip fetching Confluence comments as sidecar files. |
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--debug` | `false` | Show HTTP request/response detail for troubleshooting. |

```bash
pks confluence checkout
```

```bash
pks confluence checkout "New Page Title" --create --parent 12345678
```

A full sync requires a configured root page; without one, `checkout` fails with "Full checkout requires a root page ID. Reinitialize with --root-page." Single-page checkout tries a numeric page ID first, then a title search scoped to the workspace's space; if neither resolves and `--create` isn't passed, it asks interactively whether to prepare a new local page.

> **Note.** Full sync rewrites every page's `index.md` in place. Any uncommitted local edits under the workspace's own git history are overwritten — recoverable through `git log`/`git diff` inside the working directory, since every prior action was auto-committed there.

## commit

Interactively selects staged changes from `_pending/` — edited or new markdown files, and `.delete` marker files — through a multi-select checklist, then pushes each selected item to Confluence: creates new pages, updates existing ones with an optimistic-concurrency version check, uploads any local image attachments the markdown references, deletes pages staged with `confluence delete`, moves successfully-committed files to `_committed/`, and auto-commits each result to the workspace git repository. After any successful commit against a configured root page, `commit` re-pulls the full page tree from Confluence to resync local state; a resync failure is only warned, not fatal.

| Flag | Default | Description |
|---|---|---|
| `-a`, `--all` | `false` | Commit all pending files without interactive selection. |
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--debug` | `false` | Show HTTP request/response detail for troubleshooting. |

```bash
pks confluence commit
```

> **Note.** `--all` is declared but never read by the command — `commit` always shows the interactive multi-select prompt, even when `--all` is passed. It cannot currently be used non-interactively or from a script.

Any local, non-`http(s)` image reference in a pending file that can't be resolved on disk — checked against the markdown's own directory, then the workspace directory, then the project root — aborts that file's commit entirely and lists the missing paths; `commit` will not create a page with a broken image reference.

A new page whose `parent_id` points at another not-yet-created pending page (`pending:<slug>`) must be committed in the same run as its parent, with the parent selected too; `commit` orders parents before children automatically, but a child fails with "Parent not yet created — commit parent first" if its parent wasn't selected.

A `409 Conflict` (the page changed on Confluence since checkout) fails that page with "Conflict — page modified on Confluence. Re-checkout needed." rather than overwriting it; a `404` on update means the page was deleted on Confluence since checkout, reported per row and not fatal to the run.

Deleting a page without permission returns `403 Forbidden` and offers to discard the local `.delete` marker, since the API call can never succeed for it.

## delete

Stages a Confluence page for deletion without touching Confluence yet. It looks up the page by ID to confirm it exists and capture its title, writes a `_pending/<page-id>.delete` marker file, and auto-commits that marker to the workspace git repository. The actual delete call only happens later, when this staged item is selected during `pks confluence commit`.

| Argument | Required | Description |
|---|---|---|
| `page-id` | yes | Confluence page ID to stage for deletion. |

| Flag | Default | Description |
|---|---|---|
| `-v`, `--verbose` | `false` | Enable verbose output. |
| `--debug` | `false` | Show HTTP request/response detail for troubleshooting. |

```bash
pks confluence delete 12345678
```

`delete` fails with "Page not found: <id>" if the page ID doesn't resolve, so it can't pre-stage deletion of a page that doesn't exist yet. Unlike the other three commands, it does not explicitly check authentication before the page lookup — a missing Jira credential surfaces as a generic lookup error instead of the friendly "Run pks jira init first" message the other commands show.

## Troubleshooting

- **"Not authenticated. Run pks jira init first."** (`init` shows the superset wording "Not authenticated with Atlassian. Run pks jira init first.") No Atlassian credentials are stored. Run `pks jira init`, which stores the email and API token every `confluence` command reuses.
- **"No git repository found. Run from inside a git project."** `confluence init` found no `.git` walking up from the current directory. Run it from inside, or under, a git checkout.
- **"No Confluence workspace found. Run pks confluence init first."** No `.confluence/config.json` was found walking up from the current directory. Run `pks confluence init` in this project.
- **"Full checkout requires a root page ID. Reinitialize with --root-page."** The workspace was initialized without a root page, so `checkout` with no argument has nothing to scope a full sync to. Re-run `confluence init` and set one, or use single-page `checkout <page>` instead.
- **A selected `commit` fails with "Conflict — page modified on Confluence."** Someone (or something) changed the page on Confluence after your `checkout`. Run `checkout <page>` again before re-editing and re-committing.
- **`commit` exits 1 even though most pages pushed.** Exit code `1` means at least one selected item failed; it does not mean the whole run failed. Check the per-row output — successfully pushed pages already moved to `_committed/`.

## See also

- [pks](/tools/pks) — the complete command surface `confluence` is part of
- [pks agent](/tools/pks/agent) — spawn a coding agent that can edit the checked-out markdown
