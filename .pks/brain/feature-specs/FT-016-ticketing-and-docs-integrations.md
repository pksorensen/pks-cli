---
id: FT-016
title: Ticketing & docs integrations (ADO, Jira, Confluence, GitHub)
domain: integrations-ticketing
status: draft
adrs: []
tests: []
source-files: [src/Commands/Ado/, src/Commands/Jira/, src/Commands/Confluence/, src/Commands/GitHub/, src/Commands/GitHubCommand.cs, src/Commands/AuthCommand.cs, src/Infrastructure/Services/AzureDevOpsAuthService.cs, src/Infrastructure/Services/JiraService.cs, src/Infrastructure/Services/ConfluenceService.cs, src/Infrastructure/Services/GitHubService.cs]
sessions: [de4200b6-f68c-4c7b-910f-7880441ecd58, c2814099-d510-41e4-b7a4-edb84adbd05b, 5d817688-c7ef-4497-988e-53d257d792db, 024c4bd0-17c6-4b26-90c9-6d16198defab, 0ba7cecc-3d89-4bde-93b4-f62c2b9fd45a]
---

## Description

A single command family — `pks ado`, `pks jira`, `pks confluence`, `pks github` —
brings OAuth onboarding for Azure DevOps, Atlassian Jira/Confluence, and
GitHub into the CLI, and persists credentials under `~/.pks-cli/` via
`IConfigurationService` the same way Foundry does. On top of the auth layer
sit two distinct surfaces: a **ticket/page surface** (`pks jira browse|init`,
`pks confluence checkout|commit|delete`) for browsing/exporting tickets and
round-tripping Confluence pages as committed markdown, and a **git-credential
delegation surface** (`pks ado init <repo>`, `pks ado git-proxy`, `pks
git-askpass`, plus the GitHub variants) that registers repos against a local
git proxy so spawned devcontainers can `git clone`/`push` without ever seeing
a refresh token. Both halves share the OAuth client config and the
`SshTarget`-style stored-credentials pattern, and the git-proxy half is what
lets the runner act as the credential boundary for assembly-line distribution
to GitHub/ADO without the central website holding any push tokens.

## Intent

> Building on our idea that the server dont have credencials i am wondering
> i am exploring what we can do so the actually pushing to github is not
> somethign our central website is doing. I am considering if a runner can
> function as a git proxy also. So our server, if the project has a runner
> connected can do git push to the runner which has the credencials to
> github ect somehow. Lets itteration in planning mode on the idea and end
> up with a plan.

From session 0ba7cecc (runner-as-git-proxy framing).

> we basically have done option A with the pks ado init and pks git install
> thing.  Problem is that claude or an agent will just figure out to call
> the token endpoint /rpocses to get a token also like git is doing. So if
> we want to be sure i dont see this as a viable options. Am i wrong?

From session 024c4bd0 (ADO token-endpoint threat model).

> We have the service that is essensial a task queue. It dont have sensitive
> access to anything and people can implement it ontop of their desired
> project managemnte system / backlog. Jira or Azure Devops ect.

From earlier b4de500f, framing Jira/ADO as pluggable backlog backends behind
the same task-queue surface.

## Key decisions

- **One auth pattern, four providers**: ADO, Jira, Confluence, and GitHub all
  use `*InitCommand` + `*Service` + stored creds in `~/.pks-cli/`, mirroring
  the Foundry shape — `AzureDevOpsAuthService`, `JiraService`,
  `ConfluenceService`, `GitHubAuthenticationService` are siblings, not a
  generalised provider abstraction.
- **Git-proxy delegation over token-injection**: `pks ado init <repo>`
  registers a repo URL for the local `ado git-proxy` + `GitAskPassCommand`
  pair, so credentials never reach the agent's `~/.git-credentials`. Direct
  token copying into devcontainers was rejected because the agent could
  enumerate the token endpoint exactly like `git` does.
- **GitHub uses a GitHub App, not an OAuth App**: client ID
  `Iv23liFv43zosMUb8t9y` is a GitHub App so user-to-server tokens only reach
  installed repos — chosen to bound blast radius, but requires App
  installation on every target repo before `pks github init` is useful.
- **Confluence round-trips through markdown commits**: `ConfluenceCheckout`
  pulls pages via `ConfluenceMarkdownConverter`, `ConfluenceCommit` pushes
  edits back as page versions, so docs flow through the same git+editor loop
  as code rather than a Confluence-native editor.
- **Jira/ADO are task-queue backends, not first-class models**: `JiraBrowse`
  exports tickets for the agent to consume; the runtime never depends on a
  specific tracker, so swapping Jira ↔ ADO ↔ GitHub Issues is a config
  choice, not a code change.

## Gotchas / known issues

- `ado init` persists `ado.auth.credentials` but **not** `ado.git.repos`
  unless a repo URL was passed — running bare `ado init` then trying to
  clone a registered URL silently falls back to interactive auth.
- The Azure CLI client id `872cd9fa-d31f-45e0-9eab-6e460a02d1f1` is reused
  for the ADO OAuth flow; the GitHub flow uses a different (GitHub App)
  client, so a working ADO token tells you nothing about GitHub auth health.
- ADO credentials copied into a devcontainer at `~/.pks-cli/` do not
  automatically become git-proxy entries — `ado git-proxy` must be able to
  read both the copied creds and the host's `ado.git.repos`, or clones for
  unregistered URLs hang on `terminal prompts disabled`.
- `pks ado init` from a clipboard-pasted Azure DevOps clone URL does not
  reliably register the repo for the proxy; a `CLAUDE.md` system-prompt
  hint may be needed so the agent knows to re-init before cloning.
