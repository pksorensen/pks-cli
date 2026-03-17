---
title: "pks github runner"
description: "Manage self-hosted GitHub Actions runners"
tags: ["github", "runner", "ci/cd"]
category: "infrastructure"
icon: "play"
status: "stable"
usage: "pks github runner <command>"
examples:
  - command: "pks github runner register my-org/my-repo"
    description: "Register a new runner"
  - command: "pks github runner start"
    description: "Start a registered runner"
---

# pks github runner

Provision and manage self-hosted GitHub Actions runners. Runners can be registered for specific repositories or organizations and started/stopped as needed.

## Subcommands

- **register** -- Register a new self-hosted runner
- **start** -- Start a registered runner
