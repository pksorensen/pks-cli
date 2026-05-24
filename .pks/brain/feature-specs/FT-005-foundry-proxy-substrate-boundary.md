---
id: FT-005
title: Foundry proxy — security boundary for substrates
domain: ai-providers + agent-infra
status: draft
adrs: []
tests: []
source-files: [src/Commands/Foundry/FoundryProxyCommand.cs, src/Commands/Devcontainer/DevcontainerSpawnCommand.cs]
sessions: [ee82d185-bed1-47e9-bd14-02aabe5671c8, 024c4bd0-17c6-4b26-90c9-6d16198defab, e16fa8b7-7344-4fa8-89d7-fa0913b6b919, af8f146f-a46e-4f3e-8d0e-71e318904b67, agent-a26cd45e2f6ffbf78]
---

## Description
A local proxy that mints scoped MSI tokens so devcontainers, `pks claude`, and spawn-hosted
agents can use Azure AI Foundry without ever seeing the user's full Foundry refresh
token. The proxy speaks the IMDS-style MSI endpoint contract (`IDENTITY_ENDPOINT`,
`IDENTITY_HEADER`) and is reached either over an SSH reverse-tunnel (Host → Azure VM →
Docker bridge) or bound directly to the Docker bridge on a local-only Host. Credentials
live exclusively on the Host where `pks foundry login` ran; the substrate (VM, container,
spawned Claude) gets only short-lived bearer tokens scoped to the configured Foundry
resource and model allow-list. `FoundryProxyCommand` is the standalone proxy entry point;
`DevcontainerSpawnCommand.BuildFoundryEnvArgsAsync` is what wires the env vars into the
container so client SDKs transparently pick the proxy up.

## Intent

> From session 024c4bd0 (2026-05-04), prompt:
> "We have gotten our pks-cli claude to work which spawn a claude. It can use foundry
> tokens and all that. With a marketplace url to install plugins. All good. … I would
> like to test a prototype for pks claude where same as token service for getting foundry
> tokens, or the registry token service we use for pks github runner, to implement such
> we copy over the tokens to the vm running the docker with devcontainer at ~/.pks-cli
> similar we did for foundry. then i want to run pks-cli as a proxy there that can be
> mounted into the devcontainer as a proxy to azure devobs git endpoint somehow…"

> From session 024c4bd0 (2026-05-04), prompt:
> "we basically have done option A with the pks ado init and pks git install thing.
> Problem is that claude or an agent will just figure out to call the token endpoint
> /rpocses to get a token also like git is doing. So if we want to be sure i dont see
> this as a viable options. Am i wrong?"

> From session af8f146f (2026-05-05), prompt:
> "Replace the current 'copy credentials to remote VM' approach with a Host-side MSI
> token server delivered to devcontainers via SSH reverse port forwarding. … Security
> problems: credentials leave the Host, persist on VM disk, token server binds to 0.0.0.0
> on VM."

## Key decisions
- **IMDS-shaped contract.** The proxy mimics the Azure Instance Metadata Service token
  endpoint so existing Foundry/Azure SDKs work unchanged once `IDENTITY_ENDPOINT` /
  `IDENTITY_HEADER` are injected — no SDK fork, no custom client.
- **Host-resident credentials, substrate-resident proxy port.** The refresh token never
  leaves the Host. v1 copied creds to the VM (`~/.pks-cli/foundry-credentials.json`);
  v2 keeps them on the Host and exposes only a token endpoint via SSH `-R` reverse
  tunnel, so a compromised VM disk yields no long-lived secret.
- **Bind on the Docker bridge, not the loopback.** Reaching the proxy from inside a
  container forces either `GatewayPorts yes` on the VM's sshd (so `-R` can bind
  `0.0.0.0:40342`) or a `socat` relay fallback — picked over loopback-only forwarding
  because `172.17.0.1` cannot route to the VM's `127.0.0.1`.
- **Separation between proxy command and spawn-time wiring.** `FoundryProxyCommand`
  owns the long-running listener; `DevcontainerSpawnCommand.BuildFoundryEnvArgsAsync`
  owns the per-spawn injection of env vars and the SSH-tunnel arguments. Same pattern
  later reused for the git-proxy substrate boundary.
- **Model and resource allow-list at the proxy.** The proxy enforces
  `FoundryStoredCredentials.SelectedResourceName` and `EnabledModels` before minting —
  the agent inside the container cannot enumerate or escalate to other Foundry
  resources the user happens to own.

## Gotchas / known issues
- `GatewayPorts yes` is **not** the sshd default; a fresh Azure VM needs an explicit
  `VmInitCommand` step or the reverse tunnel silently lands on loopback and containers
  see a connect timeout to `IDENTITY_ENDPOINT`.
- Earlier "option A" (copy refresh token to VM + run Python MSI server with
  `nohup`) is still referenced in code paths and prompts — any agent in the container
  could call the same `/process` endpoint git was using, which is exactly the threat
  model this FT exists to close.
- The Windows-host → Linux-VM testing loop needs an embedded Linux `pks-cli` build
  shipped over SSH; without it the proxy can't run on the VM side during dev. The
  `build-local.sh` script exists to paper over this until release.
