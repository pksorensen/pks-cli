---
title: "Kom godt i gang: installer pks og kør dine første kommandoer"
description: "Installer pks CLI, bekræft at den kører, og få så tre rigtige resultater lokalt — omkostningsanalyse for Claude Code, en søgbar sessions-brain og et gennemløb med writing-lint."
tags: [quickstart, cli, installation]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "dotnet tool install -g pks-cli && pks claude usage"
---

Få pks installeret og til at producere rigtigt output på under ti minutter: installer binæren, bekræft versionen, og kør så tre selvstændige kommandoer, der hverken kræver en cloud-konto eller en API-nøgle. Hvert scenarie herunder læser data, der allerede ligger på din maskine, og skriver sine resultater lokalt.

## 1. Forudsætninger

Vælg én installationsvej. Vejene adskiller sig kun i, hvad de kræver på forhånd — `pks`-kommandoen er den samme bagefter.

- **.NET 10 SDK** — nødvendigt for .NET global tool-vejen. pks målretter `net10.0` og pakkes med `PackAsTool`.
- **Node.js 18 eller nyere** — nødvendigt for npm-vejen, som leverer selvstændige binærer og slet ikke kræver .NET.
- **En Claude Code-historik** — trin 4 og 5 læser `~/.claude/projects/**/*.jsonl`. Uden mindst én tidligere Claude Code-session er der ikke noget at analysere.
- **git** — trin 5 skriver artefakter pr. projekt til et repositorys `.pks/`-mappe.

### Mulighed A — .NET global tool (anbefalet)

```bash
dotnet tool install -g pks-cli
```

### Mulighed B — npm

```bash
npm install -g @pks-cli/cli
```

npm-pakken finder en platformsspecifik binær via `optionalDependencies`, så den samme kommando virker på Linux, macOS og Windows (x64 og arm64).

## 2. Bekræft installationen

```bash
pks --version
```

Du bør se den installerede version skrevet ud, for eksempel `6.20.1`. Hvis din shell melder, at `pks` ikke findes, så åbn en ny terminal, så mappen med værktøjet bliver samlet op af `PATH`.

pks skriver et ASCII-banner ud før de fleste kommandoer. Slå det fra med `--no-logo`, og tilføj `--debug` til en vilkårlig kommando for detaljeret output.

```bash
pks --no-logo --version
```

## 3. Se hvad Claude Code koster dig

Det er det hurtigste reelle resultat, pks giver dig, og det forlader aldrig din maskine. Den parser dine Claude Code-sessionstransskriptioner, fjerner dubletter blandt fakturerede requests, prissætter dem og tegner resultatet.

```bash
pks claude usage
```

Du får en timebaseret omkostningsgraf for de seneste 24 timer, en daglig omkostningsgraf og en omkostningsoversigt med de fem dyreste modeller. Parsede filer caches i `~/.pks-cli/usage-cache/manifest.json`, så anden kørsel er markant hurtigere.

Vil du have performance-billedet i stedet for omkostningsbilledet:

```bash
pks claude stats
```

Den tegner et aktivitets-heatmap, sessionsstreaks, samlede token-antal og en graf over svartid pr. output-token, opdelt i et nyligt vindue mod den forudgående periode — den ærlige måde at tjekke, om Claude Code er blevet langsommere.

> **Bemærk.** Begge kommandoer er read-only og offline. Ingen af dem sender dine transskriptioner nogen steder hen.

## 4. Byg en søgbar brain fra din sessionshistorik

Brain forvandler de samme transskriptioner til en søgbar videnbase. Ingest er deterministisk og gratis — der kaldes ingen model, så der faktureres ikke noget.

```bash
cd /path/to/your/repo
pks brain init
pks brain ingest
```

`init` opretter den globale rod i `~/.pks-cli/brain/` og, inde i et git-repository, en `.pks/brain/`-mappe, som den også tilføjer til den nærmeste `.gitignore`. `ingest` gennemgår hver Claude-sessionsfil og skriver fire append-only firehose-filer: prompts, tool calls, filoperationer og fejl. Gentagne kørsler genbehandler kun sessioner, hvis fil er ændret.

Søg nu i alt, du nogensinde har spurgt om:

```bash
pks brain search "keycloak"
pks brain status
```

`search` skriver en tabel ud med kilde, tidsstempel, session og matchende uddrag. `status` rapporterer, hvad din brain ved: projekter, sessioner, prompts, tool calls, filoperationer og tidspunktet for seneste ingest.

De senere faser — `pks brain extract`, `synth`, `wiki`, `adr` — kalder en LLM og koster penge. Hver af dem lægger først en plan og viser et estimat, før der bruges noget.

## 5. Lint en markdown-fil

Skriveværktøjskæden kører et deterministisk terminologigennemløb uden LLM. Initialisér det én gang, og lint så hvad som helst.

```bash
pks writing init
pks writing lint README.md
```

`init` lægger en profilskabelon i `~/.pks-cli/writing/`. Springer du det over, fejler `lint` med en fejl om en tom anglicisme-liste. `lint` skriver en `WRITING-REPORT.json` og en `.md`-sidecar ved siden af hver fil med fundene, sletter forældede sidecars for filer, der nu er rene, og afslutter altid med 0 — den bryder aldrig et build.

Tjek, hvad linteren trækker på:

```bash
pks writing profile show
```

Den skriver den resolverede profil ud sammen med antal og stier for anglicismer, allowlist-termer, den aktive kanal og referenceeksempler.

## 6. Hvor dine data ligger

To mapper er relevante, og de er to forskellige ting.

| Indstilling | Værdi |
|---|---|
| Global konfiguration og state | `~/.pks-cli/` |
| State pr. projekt | `<repo>/.pks/` |

Globale indstillinger ligger i `~/.pks-cli/settings.json`. Credentials for hver integration gemmes pr. funktion i samme mappe. På Windows er roden `%USERPROFILE%\.pks-cli`, ikke `%APPDATA%`.

## 7. Næste skridt

- [pks](/tools/pks) — hele kommandofladen, og hvor hver familie hører hjemme
- [pks brain](/tools/pks/brain) — pipelinen i fem faser fra rå sessioner til wiki-sider og ADR'er
- [pks writing](/tools/pks/writing) — scoringsløkken, naturlighedsgennemgangen og den portable skriveprofil
- [pks devcontainer](/tools/pks/devcontainer) — start en devcontainer lokalt eller på et remote SSH-mål
- [pks github](/tools/pks/github) — log ind på GitHub og kør den selvhostede Actions runner
- [pks agent](/tools/pks/agent) — one-shot-løkken for kodeagenten og registrering i Agent Share
