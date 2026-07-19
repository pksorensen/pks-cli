---
title: "pks agentics init"
description: "Log in to agentics.dk with a device-code browser flow, and store the resulting Keycloak tokens for every later pks agentics command."
tags: [how-to, auth, oidc, agentics]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks agentics init"
examples:
  - command: "pks agentics init"
    description: "Log in against the default agentics.dk server"
  - command: "pks agentics init --server agentics.dk"
    description: "Log in against an explicit server host"
  - command: "pks agentics init --no-browser"
    description: "Print the verification URL instead of opening it"
---

Get this machine authenticated against agentics.dk in under a minute: run one command, open the printed URL, enter the user code, and the tokens land on disk. This is the login every other `pks agentics` command falls back to when no more specific credential applies.

## 1. Prerequisites

- **pks installed.** Any install method works — see the [pks overview](/tools/pks).
- **A browser you can reach.** It does not have to be on the same machine. The flow prints a URL and a code, so a headless box works as long as you can type the code somewhere.
- **An account on the target server.** The default is agentics.dk.

## 2. Start the login

```bash
pks agentics init
```

The command requests a device code, prints a panel containing the verification URL and the user code, and tries to open your browser. On Linux it uses `xdg-open`, on macOS `open`, on Windows `cmd start`. A failed launch is swallowed — the panel is shown either way.

For a headless or SSH session, skip the browser attempt:

```bash
pks agentics init --no-browser
```

## 3. Approve in the browser

Open the printed URL, sign in, and enter the user code. Meanwhile the CLI polls the token endpoint. Polling honors the RFC 8628 responses: `authorization_pending` keeps polling, `slow_down` adds five seconds to the interval, and `expired_token` or `access_denied` aborts. The interval is clamped to a five-second minimum and the deadline is the server-provided lifetime, with a sixty-second floor.

## 4. Point at a different server or realm

The Keycloak base URL is derived by convention as `https://keycloak.<server>/realms/<realm>` unless `--server` already contains a full `http://` or `https://` URL.

```bash
pks agentics init --server agentics.dk --realm agentics --client-id pks-cli
```

## 5. Verify

On success the tokens are written to `~/.pks-cli/agentics-auth.json` with mode `0600`, together with the server, realm, and client id used.

```bash
ls -l ~/.pks-cli/agentics-auth.json
```

You should see a file owned by you with permissions `-rw-------`. Deleting it forces a fresh login.

## Options

| Flag | Default | Description |
|---|---|---|
| `--server <SERVER>` | `agentics.dk` | Agentics server host, or a full `http(s)` URL. |
| `--realm <REALM>` | `agentics` | Keycloak realm to authenticate against. |
| `--client-id <ID>` | `pks-cli` | OAuth client id used for the device flow. |
| `--no-browser` | — | Print the verification URL instead of trying to open a browser. |
| `-v`, `--verbose` | — | Enable verbose output. |

## Troubleshooting

- **The device-code request fails immediately.** The server host does not follow the `keycloak.<server>` DNS convention. Pass the full issuer URL to `--server`.
- **The code expires before you approve it.** Re-run the command; the deadline is the server's `expires_in`, with a sixty-second floor.
- **Nothing opens on a remote machine.** Use `--no-browser` and copy the URL to a local browser.
- **A later command still says it has no credentials.** Check that `~/.pks-cli/agentics-auth.json` exists and is readable by the same user that runs the command. `sudo` changes the home directory and therefore the credential path.

## Next steps

- [Run a self-hosted Agentics runner](/tools/pks/agentics/runner) — put this machine to work executing ALP jobs
- [Submit a task to an assembly line](/tools/pks/agentics/task) — the command that uses this login as its last-resort credential
- [pks agentics CLI reference](/tools/pks/agentics/cli-reference) — the full flag and environment-variable surface
