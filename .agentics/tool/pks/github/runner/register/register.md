---
title: "pks github runner register"
description: "Register a self-hosted GitHub Actions runner for a repository"
tags: ["github", "runner", "ci/cd", "registration"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "user-plus"
status: "stable"
usage: "pks github runner register <owner/repo>"
examples:
  - command: "pks github runner register my-org/my-repo"
    description: "Register runner for my-org/my-repo"
  - command: "pks github runner register my-org/my-repo --labels gpu,large"
    description: "Register with custom labels"
---

# pks github runner register

Registers a new self-hosted GitHub Actions runner for the specified repository. The runner is configured with the repository's runner token and can be customized with labels.

## Prerequisites

- GitHub personal access token with `admin:org` scope (for org runners) or `repo` scope
- The target repository must exist

## Options

| Flag | Description |
|------|-------------|
| `--labels` | Comma-separated list of custom labels |
| `--name` | Runner name (defaults to hostname) |
| `--work` | Working directory for the runner |
