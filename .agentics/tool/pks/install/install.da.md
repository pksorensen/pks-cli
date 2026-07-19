---
title: "Installer pks"
description: "Installer pks CLI som .NET global tool eller som npm-pakket self-contained binary på Linux, macOS eller Windows — og verificer, fastlås, opdater og fjern den bagefter."
tags: [how-to, install, cli]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "dotnet tool install -g pks-cli"
---

Få `pks` på maskinen på få minutter: vælg en installationskanal, kør én kommando, og bekræft at binaryen rapporterer en version. De samme kommandoer virker på Linux, macOS og Windows — den eneste forskel mellem de to udgivne kanaler er, hvad de kræver installeret i forvejen.

`pks` er et .NET 10-program, der udgives til nuget.org som pakken `pks-cli` og til npm som `@pks-cli/cli`. Selve kommandofladen er beskrevet i [pks-oversigten](/tools/pks).

## 1. Forudsætninger

Vælg én række. Du behøver ikke begge.

- **.NET 10 SDK** — kræves til `dotnet tool`-kanalen. `pks` targeter `net10.0`, så et ældre SDK kan ikke køre den.
- **Node.js 18 eller nyere** — kræves til npm-kanalen. Den kanal leverer en self-contained binary, så .NET er ikke nødvendigt.

De understøttede platformspakker på npm-kanalen er `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` og `win-arm64`. Andre kombinationer af platform og arkitektur udgives ikke.

## 2. Installer pks

### Mulighed A — .NET global tool (anbefalet)

```bash
dotnet tool install -g pks-cli
```

```powershell
dotnet tool install -g pks-cli
```

Værktøjet pakkes med `PackAsTool`, så den installerede kommando hedder `pks` — ikke `pks-cli`. .NET SDK'et lægger shimmet i `~/.dotnet/tools` på Linux og macOS og i `%USERPROFILE%\.dotnet\tools` på Windows.

### Mulighed B — npm-wrapper

```bash
npm install -g @pks-cli/cli
```

```powershell
npm install -g @pks-cli/cli
```

Pakken `@pks-cli/cli` indeholder kun en Node-launcher. Selve binaryen kommer via en optional dependency, der vælges ud fra din platform — `@pks-cli/cli-linux-x64`, `@pks-cli/cli-osx-arm64`, `@pks-cli/cli-win-x64` og så videre. Et postinstall-tjek skriver `PKS CLI installed successfully`, når den rigtige platformspakke er landet.

### Mulighed C — byg fra kildekode

```bash
git clone https://github.com/pksorensen/pks-cli.git
cd pks-cli
dotnet build pks-cli.sln
```

Kør resultatet uden at installere det:

```bash
cd src
dotnet run -- status
```

Et kildekodebyg rapporterer sin installationsmetode som ukendt, så selvopdatering er ikke tilgængelig — byg igen for at få ændringer med.

Brug publish-scriptet til at producere self-contained single-file-binaries for hver understøttet runtime identifier. Det skriver én mappe pr. platform:

```bash
./scripts/publish-self-contained.sh 6.20.1 ./npm-dist
```

> **Bemærk.** `build-local.sh` er et andet script. Det krydskompilerer de indlejrede Go-ledsagere (`vibecast`, `heypoul`), før det udgiver en Windows-binary, og kræver derfor også Go 1.24 eller nyere på `PATH`.

## 3. Verificer installationen

```bash
pks --version
```

Versionsstrengen læses fra assemblyens informational version og skrives på sin egen linje — for en aktuel stabil installation `6.20.1`.

Bekræft, at shimmet peger på den kanal, du forventer:

```bash
command -v pks
```

```powershell
Get-Command pks
```

En `dotnet tool`-installation resolver under `.dotnet/tools`; en npm-installation resolver under dit globale npm-prefix og peger på en launcher inde i `node_modules/@pks-cli/`.

## 4. Fastlås eller tjek en version

Begge kanaler accepterer en eksplicit version, og det er den understøttede måde at holde en maskine på et kendt build.

```bash
dotnet tool install -g pks-cli --version 6.20.1
```

```bash
npm install -g @pks-cli/cli@6.20.1
```

Sådan læser du, hvad der er installeret — frem for hvad der er udgivet:

```bash
dotnet tool list -g
```

```bash
npm list -g @pks-cli/cli
```

Prereleases udgives til nuget.org fra `main` sideløbende med de stabile udgivelser. Installer en ved at bede eksplicit om det:

```bash
dotnet tool install -g pks-cli --prerelease
```

## 5. Opdatering

`pks update` sammenligner den kørende binary med den nyeste `pks-cli`-version på nuget.org, viser et panel med nuværende versus nyeste og beder om bekræftelse.

```bash
pks update
```

Hvad der sker derefter, afhænger af, hvordan `pks` blev installeret. Kommandoen udleder det fra stien til den kørende eksekverbare fil:

| Installationsmetode | Adfærd for `pks update` |
| --- | --- |
| .NET global tool | Kører `dotnet tool update -g pks-cli` for dig. |
| npm | Skriver kommandoen, du skal køre: `npm install -g @pks-cli/cli@latest`. |
| Binary bagt ind i en devcontainer | Skriver udskiftningsscriptet til host-siden, fordi containerbrugeren ikke kan skrive til `/usr/local/bin`. |
| Standalone binary | Skriver målversionen og forventer, at du selv erstatter filen. |
| Kildekode-checkout | Melder, at builddet kom fra kildekode, og stopper. |

Første gang spørger kommandoen, hvilken kanal den skal følge — `stable` (udgivne versioner) eller `daily` (nyeste prerelease fra `main`). Valget gemmes under konfigurationsnøglen `cli.update.channel`, og daily-kanalen tilføjer `--prerelease` til den underliggende `dotnet tool update`.

Udskiftning af binaryen er beskyttet af andenfaktoren `pks.update`, da en udskiftet binary ellers ville kunne slå beskyttelsen fra. Kontrollen springes over, når der ikke er tilmeldt en faktor — se [pks authenticator](/tools/pks/authenticator).

Det virker også at opdatere uden kommandoen:

```bash
dotnet tool update -g pks-cli
```

```bash
npm install -g @pks-cli/cli@latest
```

## 6. Afinstallation

```bash
dotnet tool uninstall -g pks-cli
```

```bash
npm uninstall -g @pks-cli/cli
```

Afinstallation fjerner kun den eksekverbare fil. Konfiguration og credentials bliver liggende i `~/.pks-cli` på Linux og macOS og i `%USERPROFILE%\.pks-cli` på Windows. Slet den mappe for også at fjerne gemte tokens, registrerede SSH-targets, certifikater og cachet tilstand.

## 7. Fejlfind en mislykket installation

**`pks: command not found` efter en `dotnet tool`-installation.** SDK'ets tools-mappe ligger ikke i `PATH`. Tilføj den, og åbn shellen igen:

```bash
export PATH="$HOME/.dotnet/tools:$PATH"
```

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
```

Gør ændringen permanent i din shell-profil på Linux og macOS, eller via Windows' indstillinger for miljøvariabler.

**Installationen fejler med en framework- eller target-fejl.** Pakken targeter `net10.0`. Kør `dotnet --list-sdks`, og bekræft at et 10.x-SDK er til stede. Hvis der kun er ældre SDK'er installeret, så installer enten .NET 10 eller skift til npm-kanalen, som slet ikke kræver .NET.

**`PKS CLI binary not found for <platform>-<arch>`.** Den platformsspecifikke optional dependency blev ikke installeret — typisk fordi optional dependencies blev sprunget over, eller fordi en proxy blokerede downloaden. Installer den direkte, eller gennemtving en geninstallation:

```bash
npm install @pks-cli/cli-linux-x64
```

```bash
npm install -g @pks-cli/cli --force
```

**`Unsupported platform`.** Launcheren mapper kun de seks udgivne platformspakker, der er nævnt i trin 1. Der findes ingen binary til andet — byg fra kildekode i stedet.

**`pks` kører, men rammer den forkerte kopi.** Begge kanaler installerer en kommando ved navn `pks`. Har du både .NET-værktøjet og npm-wrapperen på samme maskine, afgør rækkefølgen i `PATH`, hvilken der kører, og `pks update` rapporterer så installationsmetoden for den, der vandt. Fjern den kanal, du ikke vil have.

**Der skrives intet, eller outputtet er pyntet, hvor du forventede ren tekst.** Brug `--no-logo` for at undertrykke ASCII-banneret, og `--debug` for at sætte `PKS_DEBUG=1` og få detaljeret output for kørslen.

## Næste skridt

- [pks](/tools/pks) — hele kommandofladen, og hvad hver familie gør
- [pks update](/tools/pks/update) — opdateringskommandoens kanaler, flag og adfærd pr. installationsmetode
- [pks init](/tools/pks/init) — stilladsér et projekt og opret `.pks`-mappen
- [pks status](/tools/pks/status) — bekræft at CLI'en kan se dit projekt og dit miljø
- [pks authenticator](/tools/pks/authenticator) — tilmeld den andenfaktor, der beskytter følsomme handlinger
