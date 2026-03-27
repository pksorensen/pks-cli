---
title: "pks agentics runner start"
description: "Start an Agentic Live runner for broadcasting"
tags: ["agentics", "runner", "broadcasting"]
category: "infrastructure"
platform: ["linux", "macos"]
icon: "play"
status: "stable"
usage: "pks agentics runner start [options]"
examples:
  - command: "pks agentics runner start"
    description: "Start the runner"
  - command: "pks agentics runner start --detach"
    description: "Start in background"
---

# pks agentics runner start

Starts a previously registered Agentic Live runner. The runner connects to the server and begins accepting broadcasting sessions.

## Options

| Flag | Description |
|------|-------------|
| `--detach` | Run in background |
