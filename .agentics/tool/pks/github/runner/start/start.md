---
title: "pks github runner start"
description: "Start a registered GitHub Actions runner"
tags: ["github", "runner", "ci/cd"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "play"
status: "stable"
usage: "pks github runner start [options]"
examples:
  - command: "pks github runner start"
    description: "Start the runner in foreground"
  - command: "pks github runner start --detach"
    description: "Start the runner in background"
---

# pks github runner start

Starts a previously registered GitHub Actions runner. The runner begins polling for jobs from the configured repository.

## Options

| Flag | Description |
|------|-------------|
| `--detach` | Run in background |
| `--name` | Runner name to start (if multiple registered) |
