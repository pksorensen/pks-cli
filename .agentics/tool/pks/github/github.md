---
title: "pks github"
description: "Manage GitHub integrations including self-hosted runners"
tags: ["github", "ci/cd", "runners"]
category: "infrastructure"
icon: "github"
status: "stable"
usage: "pks github <command>"
examples:
  - command: "pks github runner register my-org/my-repo"
    description: "Register a runner for a repository"
---

# pks github

Manage GitHub integrations for the Agentic Live platform, including self-hosted runner provisioning and lifecycle management.

## Subcommands

- **runner** -- Manage self-hosted GitHub Actions runners
