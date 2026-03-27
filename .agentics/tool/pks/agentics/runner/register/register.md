---
title: "pks agentics runner register"
description: "Register an Agentic Live runner for broadcasting sessions"
tags: ["agentics", "runner", "registration"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "user-plus"
status: "stable"
usage: "pks agentics runner register [options]"
examples:
  - command: "pks agentics runner register"
    description: "Register a runner with default settings"
  - command: "pks agentics runner register --server agentics.dk"
    description: "Register against a specific server"
---

# pks agentics runner register

Registers a new Agentic Live runner that can broadcast coding sessions. The runner is configured to connect to the specified Agentic Live server.

## Options

| Flag | Description |
|------|-------------|
| `--server` | Agentic Live server URL (default: agentics.dk) |
| `--name` | Runner name (defaults to hostname) |
