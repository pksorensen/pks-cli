---
id: FT-003
title: Remote compute substrates (SSH, VM, Firecracker, FileShare, Rsync, Storage, Coolify, Registry)
domain: agent-infra
status: draft
adrs: []
tests: []
source-files: [src/Commands/Ssh/SshConnectCommand.cs, src/Commands/Ssh/SshListCommand.cs, src/Commands/Ssh/SshRegisterCommand.cs, src/Commands/Ssh/SshRemoveCommand.cs, src/Commands/Ssh/SshSettings.cs, src/Commands/Vm/VmAutoshutdownCommand.cs, src/Commands/Vm/VmDestroyCommand.cs, src/Commands/Vm/VmInitCommand.cs, src/Commands/Vm/VmListCommand.cs, src/Commands/Vm/VmScheduleCommand.cs, src/Commands/Vm/VmSettings.cs, src/Commands/Vm/VmStatusCommand.cs, src/Commands/Firecracker/FirecrackerSettings.cs, src/Commands/Firecracker/Runner/FirecrackerRunnerInitCommand.cs, src/Commands/Firecracker/Runner/FirecrackerRunnerSettings.cs, src/Commands/Firecracker/Runner/FirecrackerRunnerStartCommand.cs, src/Commands/Firecracker/Runner/FirecrackerTestCommand.cs, src/Commands/FileShare/FileShareInitCommand.cs, src/Commands/FileShare/FileShareSettings.cs, src/Commands/FileShare/FileShareStatusCommand.cs, src/Commands/Rsync/RsyncInitCommand.cs, src/Commands/Rsync/RsyncListCommand.cs, src/Commands/Rsync/RsyncRemoveCommand.cs, src/Commands/Rsync/RsyncSettings.cs, src/Commands/Storage/StorageListCommand.cs, src/Commands/Storage/StorageLsCommand.cs, src/Commands/Storage/StorageSettings.cs, src/Commands/Storage/StorageSyncCommand.cs, src/Commands/Coolify/CoolifyListCommand.cs, src/Commands/Coolify/CoolifyRegisterCommand.cs, src/Commands/Coolify/CoolifySettings.cs, src/Commands/Coolify/CoolifyStatusCommand.cs, src/Commands/Registry/RegistryInitCommand.cs, src/Commands/Registry/RegistryRemoveCommand.cs, src/Commands/Registry/RegistrySettings.cs, src/Commands/Registry/RegistryStatusCommand.cs]
sessions: [13973d8f-8531-4dbe-869d-7a36f7c19e81, af8f146f-a46e-4f3e-8d0e-71e318904b67, a0d68f27-c923-4892-868d-20b90f0ec07f, a160e3cc-2a06-4df0-acc3-8c686583a4fd, a935119c-f8fe-4f78-b452-80c7d786553a]
---

## Description
Manage and connect to the underlying compute fabric — SSH targets, Azure VMs,
Firecracker microVMs, file shares, registries — that the higher-level pks
commands (claude spawn, vibecast, runner, devcontainer) lean on. The substrate
layer normalises "where does this workload run, and how do I reach it" so the
spawn-side commands stay agnostic: an SSH target registered with `pks ssh
register` is the same shape whether it points at a long-lived bastion, an Azure
VM provisioned via `pks vm init`, a Firecracker microVM, or a Coolify-managed
service. Companion verbs (`rsync`, `storage`, `fileshare`) cover the
move-data-around half of the same fabric so claude/runner state can be backed
up or shared across these substrates, and `registry` lets a runner pull
container images from a configured private registry.

## Intent
> From session a935119c (2026-05-03), prompt:
> "Your task is to implement four VM management features in pks-cli, end-to-end
> and TDD-first … **NO `az` CLI shellouts.** Every Azure operation goes through
> `src/Infrastructure/Services/AzureVmService.cs` using its existing HttpClient
> + Bearer-token + ARM REST pattern. **NO `--flag` arguments on the new
> behaviour.** Drive every new option with Spectre interactive prompts."

> From session a0d68f27 (2026-05-04), prompt:
> "could we if we have not done it alraedy.  please remind me do a rsync kinda
> backup.  So i could do pks rsync init which just setups a credencials for
> doign a rsync.  then pks claude backup would know it can use rsync targets,
> maybe ssh targets also appply?  and it backups the full ~/.claude/ folders
> project, sessions ect. Right now we are running pks claude stats in diffrent
> devcontianers, i really would like to back them off to a central location and
> i already have a nas that i backup services into usin rsync."

> From session a160e3cc (2026-05-04), prompt:
> "so when we spawn a devcontainer with pks vibecast or pks claude ect -  we
> mount in the Linux and WSL: /etc/claude-code/  folder and setup settinsg to
> include our marketplace url so its avaible when working with claude on the
> devcontainer."

## Key decisions
- **No `az` CLI, no Azure SDK.** All VM ops go through `AzureVmService` over
  the ARM REST API with a Bearer token — keeps the binary self-contained and
  the auth model identical to the rest of the Azure surface (Foundry, billing).
- **Interactive-only for new verbs.** New VM/Rsync/FileShare commands take no
  `[CommandOption]` flags; everything is `SelectionPrompt`/`TextPrompt`/
  `Confirm`. Legacy `vm init` flags stay for back-compat but no new ones are
  added.
- **Targets, not connections.** Both `ssh` and `rsync` persist named *targets*
  to `~/.pks-cli/` via `ISshTargetConfigurationService` /
  `IRsyncTargetConfigurationService`, so higher-level commands
  (`claude backup`, `vibecast`, devcontainer spawn) just consume a target name
  and don't care how it was authenticated.
- **Substrate verbs live next to runner-config verbs.** `coolify register`,
  `registry init` and `fileshare init` all use the same `Runner` configuration
  service pattern so a runner can be told "here is your deploy target, here is
  your image pull source, here is your shared filesystem" with three
  symmetrical commands.
- **Firecracker is the experimental/local-microVM lane.** Kept under
  `Commands/Firecracker/Runner/` rather than mixed into `Vm/` to signal that
  it is the host-side microVM substrate, not an Azure-managed VM.

## Gotchas / known issues
- Remote devcontainer spawn against a freshly-started Azure VM has been seen
  to fail with a `docker buildx build` error after `VM started` (session
  a160e3cc) — the VM is reachable but the devcontainers feature build still
  exits non-zero; root cause not yet captured in an ADR.
- `vm init` is the only command in this group that retained legacy flags;
  keep new options off it unless explicitly extending that legacy surface.
