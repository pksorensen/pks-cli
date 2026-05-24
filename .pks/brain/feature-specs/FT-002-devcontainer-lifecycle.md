---
id: FT-002
title: Devcontainer lifecycle (init / spawn / connect / destroy)
domain: agent-infra
status: draft
adrs: []
tests: []
source-files: [src/Commands/Devcontainer/DevcontainerInitCommand.cs, src/Commands/Devcontainer/DevcontainerSpawnCommand.cs, src/Commands/Devcontainer/DevcontainerConnectCommand.cs, src/Commands/Devcontainer/DevcontainerDestroyCommand.cs, src/Commands/Devcontainer/DevcontainerListCommand.cs, src/Commands/Devcontainer/DevcontainerContainersCommand.cs, src/Commands/Devcontainer/DevcontainerValidateCommand.cs, src/Commands/Devcontainer/DevcontainerWizardCommand.cs, src/Commands/Devcontainer/DevcontainerSettings.cs, src/Commands/Devcontainer/DevcontainerCommand.cs, src/Infrastructure/Services/DevcontainerService.cs, src/Infrastructure/Services/DevcontainerSpawnerService.cs, src/Infrastructure/Services/DevcontainerFeatureRegistry.cs, src/Infrastructure/Services/DevcontainerFileGenerator.cs, src/Infrastructure/Services/DevcontainerTemplateService.cs]
sessions: [5d817688-c7ef-4497-988e-53d257d792db, 8c06326a-e824-40eb-92b2-eac15a60c19b, c53335ab-d0a7-4bd5-9e3a-a00de05504e6, agent-af943d816f970e718, 024c4bd0-17c6-4b26-90c9-6d16198defab]
---

## Description
Generate, validate, and remotely spawn devcontainers over SSH; the substrate for
hosting agents on remote hosts. The `pks devcontainer` family covers the full
lifecycle: `init`/`wizard` scaffold a `.devcontainer/` from templates and
features; `validate` checks the produced JSON; `spawn` copies the workspace to a
registered `SshTarget`, builds and starts the container on that host via the
`ssh`/`scp` binaries (no SSH library), and threads through Foundry MSI tokens
and other credentials; `connect`/`list`/`containers` inspect or attach to a
running container; `destroy` tears it down. Subclasses
(`ClaudeSpawnCommand`, `VibecastCommand`) extend `DevcontainerSpawnCommand` by
overriding `OnAfterRemoteSpawnAsync` to drop in agent-specific prompts and
runtime state, so the devcontainer is the unit a runner job actually executes in.

## Intent
> From session 024c4bd0 (2026-05-05), prompt: "We have gotten our pks-cli
> claude to work which spawn a claude. It can use foundry tokens and all that.
> With a marketplace url to install plugins. All good. … i want to run pks-cli
> as a proxy there that can be mounted into the devcontainer as a proxy to
> azure devobs git endpoint somehow so when we inside devconatiner does git
> push, git pull, git fetch against a remote … I would like to somehow
> intercept that inside the devcontainer and route it to our git endpoint
> which then goes out though the host and proxy/apply the credencials we have
> setup".

> From session agent-af943d816f970e718 (2026-05-13), prompt: "Explore the PKS
> CLI runner (C#) to understand exactly what's happening between job claim and
> the first PATCH to in_progress … The devcontainer spawn that happens BEFORE
> the in_progress PATCH at ~line 805. … Read
> `Infrastructure/Services/DevcontainerSpawnerService.cs` (or similar — the
> service that does the actual Docker work)."

> From session 8c06326a (2026-05-14), prompt: "new test same result … back at
> cat: /tmp/vibecast-job-5957771d-e312-49b5-bce6-3159eae4647e/initial-prompt.txt:
> No such file or directory" — the spawn copied the workspace but the
> per-job prompt file the agent expects inside the container was missing.

## Key decisions
- Shell out to system `ssh`/`scp` via `Process` instead of pulling in an SSH
  library (`RunSshCommandAsync` is the shared helper). Keeps the dependency
  surface tiny and lets users rely on their existing SSH config / agents.
- Split spawn into a base `DevcontainerSpawnCommand` plus agent-specific
  subclasses (`ClaudeSpawnCommand`, `VibecastCommand`) that hook
  `OnAfterRemoteSpawnAsync`, so the lifecycle (copy → build → start) is shared
  but each agent owns its own post-spawn provisioning (prompt files, tokens).
- Registered SSH targets persist to `~/.pks-cli/ssh-targets.json` via
  `ISshTargetConfigurationService`, so `spawn`/`connect`/`destroy` all address
  hosts by label rather than re-typing connection details.
- `BuildFoundryEnvArgsAsync` in `DevcontainerSpawnCommand` is the single place
  that provisions Azure AI Foundry MSI token access into the devcontainer's
  environment — agents inside the container get tokens through env, not by
  baking creds into the image.
- Initializer pipeline (`IInitializer` implementations under
  `Infrastructure/Initializers/Implementations/`) drives `init` — new
  initializers auto-register via reflection and run in `Order`, with
  `ShouldRunAsync` gating per-context, which is how `devcontainer init`
  composes templates + features without a hand-maintained registry.

## Gotchas / known issues
- Per-job prompt files are written to predictable paths inside the spawned
  container (e.g. `/tmp/vibecast-job-<id>/initial-prompt.txt`); if the
  post-spawn hook hasn't completed before the agent reads, the agent fails
  with `No such file or directory` (seen in session 8c06326a).
- Auth flows that trigger inside the devcontainer (Claude sign-in, git device
  code) can leave the agent stuck before it consumes the initial prompt;
  session c53335ab discusses re-issuing the prompt via a second
  `tmux send-keys` round after `/exit` as a workaround.
- Spawn runs *before* the runner PATCHes the job to `in_progress`, so a slow
  or failing devcontainer build looks like a stuck claim from the server's
  perspective (raised in session agent-af943d816f970e718).
- Credential-proxy idea (route in-container `git`/token calls back through
  the host so the container never holds long-lived creds) is discussed in
  session 024c4bd0 but is not fully landed in this lifecycle yet.
