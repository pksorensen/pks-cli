---
title: "pks"
description: "CLI-værktøj til styring af infrastruktur, GitHub runners og Agentic Live-tjenester"
tags: ["cli", "infrastruktur", "styring"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "terminal"
status: "stable"
usage: "pks <kommando> [indstillinger]"
examples:
  - command: "pks github runner register my-org/my-repo"
    description: "Registrer en GitHub runner til et repository"
  - command: "pks agentics runner start"
    description: "Start en Agentic Live runner"
---

# pks

`pks` CLI er det primære infrastrukturstyringsværktøj til Agentic Live-platformen. Det tilbyder underkommandoer til styring af GitHub runners, Agentic Live-tjenester og andre infrastrukturkomponenter.

## Installation

CLI'en er bygget fra Go-kildekode og kan kompileres med:

```bash
go build -o pks ./main.go
```

## Underkommandoer

- **github** -- Administrer GitHub-integrationer (runners, workflows)
- **agentics** -- Administrer Agentic Live-platformstjenester
