---
title: "pks"
description: "CLI tool for managing infrastructure, GitHub runners, and Agentic Live services"
tags: ["cli", "infrastructure", "management"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "terminal"
status: "stable"
type: "cli"
author: "Poul Kjeldager"
component: "pks"
usage: "pks <command> [options]"
examples:
  - command: "pks github runner register my-org/my-repo"
    description: "Register a GitHub runner for a repository"
  - command: "pks agentics runner start"
    description: "Start an Agentic Live runner"
---

# pks

The `pks` CLI is the primary infrastructure management tool for the Agentic Live platform. It provides subcommands for managing GitHub runners, Agentic Live services, and other infrastructure components.

## Installation

The CLI is built from Go source and can be compiled with:

```bash
go build -o pks ./main.go
```

## Subcommands

- **github** -- Manage GitHub integrations (runners, workflows)
- **agentics** -- Manage Agentic Live platform services
