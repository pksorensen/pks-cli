---
title: "Register this session as a shareable agent"
description: "Enroll the current coding session against an Agent Share server so it appears in the Share panel as a person other people and agents can send work to."
tags: [how-to, agent-share, oidc, agents]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agent register [NAME] [options]"
examples:
  - command: "pks share init"
    description: "Sign in to an Agent Share server first"
  - command: "pks agent register"
    description: "Interactive enrollment against the configured provider"
  - command: "pks agent register \"my-session\" --role \"coding session\""
    description: "Non-interactive enrollment for scripts and CI"
---

Make the terminal you are working in visible to other people and agents: sign in to an Agent Share server, register the session under a name, and watch it appear in the Share panel as somewhere to send screenshots, links, and decisions.

No language model is involved here. This command is about identity, not compute — for the coding agent on the same branch, see [Run a one-shot coding agent](/tools/pks/agent/run).

## 1. Prerequisites

- **An Agent Share account.** Registration authenticates with an OIDC bearer token, so you need a server to authenticate against — for example `share.agentics.dk`.
- **A completed `pks share init`.** This is the login step that stores the credential registration reads. Without a configured provider, registration refuses to run.
- **A one-time `agent-share install` on this machine, if you want the session to receive shares.** Registration mints the inbox; it deliberately does not wire the local `share-agent` MCP server. That wiring is a separate, once-per-machine step.

## 2. Sign in to the share server

```bash
pks share init
```

This performs the OIDC login and stores the credential. Registration reuses it, so you run this once per machine rather than once per session.

## 3. Register the session

```bash
pks agent register
```

In an interactive terminal you are prompted for a name and an optional role. The role defaults to `coding session` if you accept the prompt without typing.

When stdin is redirected, no prompts fire: the name defaults to the current directory's basename and the role defaults to `coding session`.

## 4. Register non-interactively

Supply everything explicitly when running from a script or a CI job.

```bash
pks agent register "my-session" --role "coding session" --provider share
```

With name, role, and provider all given, the command never prompts, regardless of whether a terminal is attached.

## 5. Verify

Open the Agent Share panel and look for the name you registered. The success message states that the agent appears within about 30 seconds.

If the command exits `0` and prints a success message, the enrollment reached the server. A failure prints `Registration failed:` followed by the underlying error and exits `1`.

## 6. Next steps

- [Run a one-shot coding agent](/tools/pks/agent/run) — the compute half of this branch
- [pks agent CLI reference](/tools/pks/agent/reference) — arguments, flags, and behavior in one place
- [Agent](/tools/pks/agent) — how registration and the coding agent relate

## Arguments and options

| Argument | Required | Description |
|---|---|---|
| `NAME` | no | Agent name as it appears when sharing. Prompted in an interactive terminal; defaults to the current directory's basename otherwise. |

| Flag | Default | Description |
|---|---|---|
| `-r <text>`, `--role <text>` | `coding session` | One-line role or description shown alongside the agent. |
| `-p <name>`, `--provider <name>` | the sole configured provider | Provider to register against. `share` is the only built-in provider. With several configured and an interactive terminal, omitting this shows a selection prompt. |

## How enrollment works

Each invocation refreshes the stored OIDC access token using a refresh-token grant against the credential's issuer. If the issuer rotates the refresh token, the new value is written back to the stored credential.

The command then posts the enrollment to the share server, which resolves the owner from the bearer token and mints a per-user agent inbox. Nothing about the local machine's MCP configuration changes as part of this.

## Troubleshooting

**`No agent provider is configured. Set one up first, e.g.: pks share init`** Zero providers are configured and the command exits `1`. Run `pks share init` and try again.

**`Registration failed: <message>`** The token refresh or the enrollment call failed. This is a network or OIDC error, not a local configuration error. Check that the share host is reachable and that the stored login has not been revoked, then rerun.

**The agent registers but never receives anything.** Enrollment creates the inbox; it does not wire the `share-agent` MCP server that lets the session pick shares up. Run `agent-share install` once on the machine.

**Repeated registrations.** There is no local check for an existing registration — every invocation re-enrolls and refreshes. Any deduplication happens on the share server.

## See also

- [Run a one-shot coding agent](/tools/pks/agent/run) — prompts, models, and the tool sandbox
- [pks agent CLI reference](/tools/pks/agent/reference) — the complete branch surface
- [Agent](/tools/pks/agent) — branch overview and mental model
