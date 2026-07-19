---
title: "pks"
description: "Enkeltoperatørens værktøjsbælte til at køre AI-kodeagenter på tværs af devcontainere, cloud-VM'er, credentials og issue-trackere — ét .NET global tool."
tags: [cli, agents, devcontainers, infrastructure]
category: infrastructure
platform: [linux, macos, windows]
icon: terminal
status: stable
type: cli
author: Poul Kjeldager
component: pks
usage: "pks <command> [options]"
examples:
  - command: "dotnet tool install -g pks-cli"
    description: "Installér pks som .NET global tool"
  - command: "pks claude"
    description: "Start en devcontainer og tilkobl en Claude Code-session"
  - command: "pks vm init"
    description: "Provisionér en cloud-VM og registrér den som SSH-target"
  - command: "pks agentics runner start"
    description: "Kør den selvhostede Agentics job-runner"
  - command: "pks claude limits"
    description: "Rapportér forbrugsgrænser for session og uge som struktureret data"
  - command: "pks brain refresh"
    description: "Genopbyg din personlige brain ud fra Claude-sessionshistorikken"
---

`pks` er en .NET 10-kommandolinjeapplikation, bygget på Spectre.Console.Cli og udgivet som pakken `pks-cli`. Den er bindevævet, når du vil køre AI-kodeagenter et andet sted end på din bærbare.

## Overblik

`pks` er ét binary, der dækker hele den loop, en operatør kører: stilladsér et projekt, giv agenten en runtime, placér den runtime på en maskine, udlevér credentials, fodr den med opgaver, følg med i hvad den gjorde, og saml resultatet op. Kommandofladen er bred med vilje — 57 topniveau-grupper — fordi hvert trin i den loop ellers kræver sin egen leverandør-CLI.

- **Agent-runtimes.** Start Claude Code, Codex eller en leverandørneutral in-process-agent, lokalt eller inde i en container på en fjern host.
- **Maskiner.** Provisionér Azure- og Scaleway-VM'er, boot Firecracker-microVM'er, og styr devcontainere over SSH.
- **Credentials.** Log ind én gang på Azure, Azure AI Foundry, GitHub, Azure DevOps, Jira, Google og Scaleway — og lad alle andre kommandoer genbruge det login.
- **Opgaver og output.** Hent sager, indsend assembly-line-opgaver, forespørg telemetri, og generér tale, billeder og transskriptioner.

## Installation

To ruter giver samme kommandoflade. Rute A kræver .NET 10 SDK; rute B kræver kun Node 18 eller nyere og indeholder et selvstændigt binary pr. platform. Kommandoerne er identiske på Linux, macOS og Windows.

**A — .NET global tool (kanonisk):**

```bash
dotnet tool install -g pks-cli
dotnet tool update -g pks-cli               # stable channel
dotnet tool update -g pks-cli --prerelease  # daily channel
```

**B — npm, uden .NET:**

```bash
npm install -g @pks-cli/cli
```

Platform-binaryen (`@pks-cli/cli-linux-x64`, `@pks-cli/cli-osx-arm64`, `@pks-cli/cli-win-x64` og resten) findes via `optionalDependencies`, så installationslinjen er den samme uanset styresystem.

Bekræft installationen:

```bash
pks --version
```

Du bør se den aktuelle version udskrevet — 6.20.1 i skrivende stund. Efter første installation håndterer `pks update` opgraderinger og registrerer selv, hvilken af de to ruter du brugte.

## Sådan hænger det sammen

Alle kommandoer læser fra én config-rod: `$HOME/.pks-cli` på Linux og macOS, `C:\Users\<user>\.pks-cli` på Windows. Login-kommandoer som `pks azure init`, `pks foundry init` og `pks github init` skriver credentials dertil, og alt andet læser dem tilbage. En anden, repo-lokal mappe, `.pks/`, indeholder projektspecifik tilstand såsom projektidentitet og genererede agent-definitioner. De to er adskilte og begge bærende.

Agent-runtime-kommandoerne bygger oven på maskinkommandoerne i stedet for at duplikere dem. `pks vm init` provisionerer en maskine og registrerer den som et navngivet SSH-target. `pks devcontainer spawn`, `pks claude` og `pks vibecast` adresserer derefter det target ved navn, sender projektets `.devcontainer` derover og tilkobler en interaktiv session inde i den resulterende container. Følsomme trin — remote spawn, VM-strømoperationer, udgående SSH, selvopdatering — passerer gennem en tofaktor-action guard, som du konfigurerer med `pks actions`, efter du har tilmeldt en faktor med `pks authenticator init`.

- **På din egen maskine:** login-tilstand, projektstilladser og lokal analyse af Claude Code-sessionstransskriptioner.
- **På en andens maskine:** de containere, microVM'er og runners, der udfører arbejdet, tilgået over SSH.

## Kommandofamilier

De 57 grupper falder i syv familier. Hver gruppe har sin egen side.

### Kernelivscyklus og CLI-rørføring

Stilladsering af et projekt og administration af `pks` selv.

| Gruppe | Hvad den gør |
|---|---|
| [pks init](/tools/pks/init) | Stilladsér et nyt projekt ud fra en NuGet-devcontainer-template, og start det eventuelt. |
| [pks exec](/tools/pks/exec) | Start ethvert værktøj, der taler `PKS_DISCOVERY`-kontrakten, og injicér en valgt LLM-provider. |
| [pks update](/tools/pks/update) | Opdatér CLI'en på stable- eller daily-kanalen, alt efter registreret installationsmetode. |
| [pks report](/tools/pks/report) | Opret et GitHub-issue med version, miljø og lokal forbrugsstatistik vedhæftet. |
| [pks status](/tools/pks/status) | Vis systemstatus-dashboardet. |
| [pks deploy](/tools/pks/deploy) | Vis demoen af deployment-flowet. |

### AI-agenter, agent-runtimes og MCP

Få en kodeagent op at køre, koble den til en model og forbinde den til Assembly Line Platform.

| Gruppe | Hvad den gør |
|---|---|
| [pks claude](/tools/pks/claude) | Start Claude Code i en devcontainer eller inline, peg den mod ikke-Anthropic-backends, og analysér lokalt forbrug. |
| [pks agent](/tools/pks/agent) | Kør en one-shot, leverandørneutral kodeagent-loop, eller registrér sessionen hos Agent Share. |
| [pks agentics](/tools/pks/agentics) | Log ind på agentics.dk, kør den selvhostede job-runner, og indsend assembly-line-opgaver. |
| [pks codex](/tools/pks/codex) | Kør upstream Codex CLI mod et Azure AI Foundry-deployment uden oversættelse af requests. |
| [pks mcp](/tools/pks/mcp) | Udstil CLI'ens egne kapabiliteter til en Model Context Protocol-klient over stdio eller HTTP. |
| [pks hooks](/tools/pks/hooks) | Registrér `pks` som handler for Claude Codes livscyklus-hooks, inklusive en lint-gate ved stop. |
| [pks brain](/tools/pks/brain) | Byg en personlig videnbase ud fra din Claude Code-sessionshistorik. |
| [pks marketplace](/tools/pks/marketplace) | Registrér og kuratér marketplaces for Claude Code-plugins. |
| [pks share](/tools/pks/share) | Log denne host ind på en Agent Share-server over OIDC. |
| [pks vibecast](/tools/pks/vibecast) | Start en fjern devcontainer og hop ind i en vibecast-session i den. |
| [pks prd](/tools/pks/prd) | Stilladsér, validér og templatisér product requirements documents. |

### Maskiner, microVM'er og fjernudviklingsmiljøer

Der hvor agenterne rent faktisk kører.

| Gruppe | Hvad den gør |
|---|---|
| [pks devcontainer](/tools/pks/devcontainer) | Forfat, validér, start, tilkobl og nedlæg devcontainere, lokalt eller over SSH. |
| [pks vm](/tools/pks/vm) | Provisionér, start, stop, inspicér og nedlæg de Azure- og Scaleway-VM'er, der hoster containerne. |
| [pks schedule](/tools/pks/schedule) | Konfigurér en VM's daglige opstart, daglige nedlukning og idle-shutdown-watchdog. |
| [pks firecracker](/tools/pks/firecracker) | Bootstrap og kør en Firecracker-microVM-job-runner til isoleret eksekvering. |
| [pks ssh](/tools/pks/ssh) | Administrér navngivne SSH-targets og et pks-holdt krypteret nøglelager bag action guard'en. |
| [pks rsync](/tools/pks/rsync) | Registrér rsync-backup-targets såsom et NAS eller en fjern host. |
| [pks tailscale](/tools/pks/tailscale) | Gem en Tailscale-auth-nøgle og join-præferencer til VM-enrollment. |
| [pks scaleway](/tools/pks/scaleway) | Autentificér mod Scaleway med et statisk API-nøglepar. |

### Cloud-identitet, hemmeligheder og signering

Den credential-rygrad, resten af værktøjet trækker på.

| Gruppe | Hvad den gør |
|---|---|
| [pks azure](/tools/pks/azure) | Log ind på Azure, vælg et abonnement, og gennemse forbrug og kreditsaldo i Cost Management. |
| [pks foundry](/tools/pks/foundry) | Autentificér mod Azure AI Foundry, vælg deployments, udsted tokens, og kør den lokale token-proxy. |
| [pks google](/tools/pks/google) | Registrér og validér en Google AI Studio-API-nøgle til billedgenerering. |
| [pks ms-graph](/tools/pks/ms-graph) | Autentificér mod Microsoft Graph via device-code-flowet for adgang til postkasser. |
| [pks authenticator](/tools/pks/authenticator) | Tilmeld og inspicér den lokale tidsbaserede engangskode som anden faktor. |
| [pks actions](/tools/pks/actions) | Vælg hvilke følsomme handlinger der kræver den anden faktor. |
| [pks cert](/tools/pks/cert) | Opret, inspicér, eksportér og fjern pks-holdte kodesignerings-certifikater. |
| [pks sign](/tools/pks/sign) | Signér et Windows-artefakt uovervåget, på en arbejdsstation eller inde i en CI-job-container. |

### Versionsstyring, opgavestyring og leveringsmål

Hvor arbejdet kommer fra, og hvor det leveres hen.

| Gruppe | Hvad den gør |
|---|---|
| [pks github](/tools/pks/github) | Autentificér mod GitHub og kør den devcontainer-baserede selvhostede Actions-runner. |
| [pks ado](/tools/pks/ado) | Autentificér mod Azure DevOps og kør git-credential-proxyen til containere. |
| [pks jira](/tools/pks/jira) | Gennemse Jira-issue-træer og eksportér udvalgte sager til markdown og JSON. |
| [pks confluence](/tools/pks/confluence) | Synkronisér Confluence-sider til lokal markdown i et git-sporet workspace, og skub redigeringer tilbage. |
| [pks git](/tools/pks/git) | Besvar Gits askpass-prompts med et friskt Azure DevOps-token. |
| [pks registry](/tools/pks/registry) | Gem container-registry-credentials til de job-containere, en runner starter. |
| [pks coolify](/tools/pks/coolify) | Registrér Coolify-instanser, så runneren kan matche repositories til applikationer og injicere deploy-variabler. |
| [pks tools](/tools/pks/tools) | Generér de tool-registry-sider, der publicerer kommandoer til agentics.dk. |

### Lagring, data og observability

Flyt bytes, og læs bagefter hvad der skete.

| Gruppe | Hvad den gør |
|---|---|
| [pks storage](/tools/pks/storage) | List, gennemse og synkronisér filer mod autentificerede share-providers, med en samtykke-gate på skrivninger. |
| [pks fileshare](/tools/pks/fileshare) | Autentificér en fileshare-provider og rapportér dens forbindelsestilstand. |
| [pks appinsights](/tools/pks/appinsights) | Vælg den Application Insights-ressource, telemetri-forespørgsler kører mod. |
| [pks otel](/tools/pks/otel) | Forespørg exceptions, requests, logs og dependency-spans fra den ressource. |
| [pks email](/tools/pks/email) | Eksportér Microsoft Graph-mail til et datomærket træ af markdown-filer med vedhæftninger. |

### Indhold, medier og skrivning

Produktion af artefakter, når arbejdet er gjort.

| Gruppe | Hvad den gør |
|---|---|
| [pks writing](/tools/pks/writing) | Dansk-først terminologi-lint, rubrik-scoring og en portabel skribentprofil. |
| [pks persona](/tools/pks/persona) | Scor indhold mod læser-arketype-personaer på rubrik-drevne metrikker. |
| [pks voice](/tools/pks/voice) | Push-to-talk-diktering drevet af Azure AI Foundry Speech. |
| [pks transcribe](/tools/pks/transcribe) | Transskribér en lyd- eller videofil med en cloud-motor eller en on-device-motor. |
| [pks tts](/tools/pks/tts) | Generér tale ud fra tekst eller SSML, eventuelt med rendering af en lydreaktiv video. |
| [pks image](/tools/pks/image) | Generér eller redigér et billede via en Google AI- eller Azure AI Foundry-model. |
| [pks promptwall](/tools/pks/promptwall) | Renders en prompt fra din Claude Code-sessionshistorik som et delbart kort. |
| [pks model](/tools/pks/model) | Download, opdatér og fjern de on-device-modeller, stemmekommandoerne bruger. |

## Standardindstillinger

| Indstilling | Værdi |
|---|---|
| Config-rod (Linux, macOS) | `$HOME/.pks-cli` |
| Config-rod (Windows) | `C:\Users\<user>\.pks-cli` |
| Globale indstillinger og de fleste tokens | `~/.pks-cli/settings.json` |
| Projektspecifik tilstand | `<repo>/.pks/` |
| Konsol-logniveau | `Warning` |
| Opdateringskanal | vælges ved første `pks update` og gemmes derefter |

> **Bemærk.** Access- og refresh-tokens til GitHub, Azure, Azure DevOps, Foundry, Microsoft Graph, Scaleway, Tailscale, Google og Jira skrives til `settings.json` i klartekst. Kun lagrene for SSH-nøgler, certifikater og Agent Share er krypteret at rest.

Ingen miljøvariabel flytter config-roden. De enkelte funktioner læser deres egne variabler — `AGENTICS_SERVER`, `ANTHROPIC_BASE_URL`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `PKS_DEBUG` med flere — hver dokumenteret på konfigurationssiden.

## Næste skridt

- [Quickstart: install pks and run your first agent](/tools/pks/quickstart) — den korteste vej fra en tom maskine til en kørende agent-session
- [Installing pks](/tools/pks/install) — alle installationsruter, forudsætninger og opgraderingsveje i detaljer
- [Concepts](/tools/pks/concepts) — operatørmodellen, action guard'en, og hvordan targets, containere og runners hænger sammen
- [Configuration and credentials](/tools/pks/configuration) — config-roden, hver gemt fil, og de miljøvariabler hver funktion læser
- [pks CLI reference](/tools/pks/cli-reference) — komplet reference over kommandoer, flag og miljøvariabler
