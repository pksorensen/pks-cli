---
title: "Print a Foundry access token"
description: "Mint and print a bearer token for the selected Azure AI Foundry resource, for scripts, curl calls, or pasting into a browser tool."
tags: [how-to, azure, auth, scripting]
category: infrastructure
status: stable
author: Poul Kjeldager
component: pks
usage: "pks foundry token [options]"
examples:
  - command: "pks foundry token"
    description: "Print a token for the default cognitive-services scope"
  - command: "pks foundry token --scope https://management.azure.com/.default"
    description: "Request a management-plane token instead"
  - command: "TOKEN=$(pks foundry token)"
    description: "Capture the token non-interactively"
---

Get a bearer token for the Azure AI Foundry resource you selected, so a script or a manual HTTP call can talk to the endpoint directly. The command refreshes the stored credential first, so the token it prints is fresh.

## 1. Prerequisites

- **A completed `pks foundry init`.** Without stored credentials the command exits with a pointer back to [pks foundry init](/tools/pks/foundry/init).
- **A selected resource**, if you intend to use the token against the Foundry endpoint.

## 2. Print a token in a terminal

```bash
pks foundry token
```

The token prints and pks waits for a keypress. Press `c` to copy it to the clipboard. Copying is best effort: it shells out to `xclip`, `xsel`, or `wl-copy` on Linux, `pbcopy` on macOS, and `clip` on Windows, and is silently skipped when none is available.

## 3. Capture a token in a script

```bash
TOKEN=$(pks foundry token)
```

When stdout is redirected or piped, the command writes the raw value and returns immediately, with no keypress wait. That makes it safe inside scripts and command substitution.

## 4. Request a different scope

The default scope is `https://cognitiveservices.azure.com/.default`, the data plane for the Foundry resource. To get a management-plane token instead:

```bash
pks foundry token --scope https://management.azure.com/.default
```

## 5. Use the compressed JSON form

`--json` does not print readable JSON. It builds a payload of the token, endpoint, model, resource, and subscription, gzip-compresses it, and base64-encodes the result. The intended consumer is a browser tool that decodes it with `pako.ungzip`.

```bash
pks foundry token --json
```

Piping this straight into `jq` will not work. Base64-decode and gunzip it first.

## 6. Verify

```bash
pks foundry status
```

Confirm the endpoint and default model the token belongs to before using it against an API.

## Options

| Flag | Default | Description |
|---|---|---|
| `-s`, `--scope <scope>` | `https://cognitiveservices.azure.com/.default` | Azure token scope to request. |
| `--json` | `false` | Emit a gzip-compressed, base64-encoded payload with token, endpoint, and default model. |
| `-v`, `--verbose` | `false` | Enable verbose output. |

## Troubleshooting

**"Not authenticated".** No stored credentials. Run [pks foundry init](/tools/pks/foundry/init).

**Token refresh fails.** The stored refresh token is expired or revoked. Run `pks foundry init --force` to sign in again.

**Pressing `c` does nothing.** No clipboard helper was found on the machine. Redirect the output instead: `pks foundry token > token.txt`.

**`jq` cannot parse `--json` output.** That output is compressed and base64-encoded by design. Use the plain form, `pks foundry token`, when you want a raw token in a shell.

**A 401 from Azure with a valid-looking token.** Check the scope. A cognitive-services token is rejected by management-plane APIs and the reverse.

## Next steps

- [Run the local Foundry proxy](/tools/pks/foundry/proxy) — hand an endpoint to a process without giving it the token
- [Inspect stored Foundry credentials](/tools/pks/foundry/status) — see which endpoint the token targets
- [pks foundry reference](/tools/pks/foundry/reference) — all scopes and flags in one table
