---
title: "pks proxy reference"
description: "Command reference for pks proxy — serve a local dev server over HTTPS on a real-looking hostname, with a local certificate authority the machine trusts once."
tags: [reference, cli, https, demo]
category: infrastructure
status: beta
author: Poul Kjeldager
component: pks
usage: "pks proxy [host] --to <url> [options]"
examples:
  - command: "pks proxy platform.contextand.com --to http://localhost:5173"
    description: "Serve the Vite dev server on https://platform.contextand.com"
  - command: "pks proxy --route app.example.test=http://localhost:3000 --route api.example.test=http://localhost:5000"
    description: "Several hostnames on one port, each to its own upstream"
  - command: "pks proxy --remove-trust"
    description: "Take the local CA out of the trust store and delete it"
---

`pks proxy` is a local HTTPS reverse proxy for demos and development. It answers on port 443 for the hostnames you give it and forwards each one to a local upstream. The certificates come from a local certificate authority that pks creates on first use and that the machine trusts once. A .NET or Vite developer certificate only covers `localhost`; this is what lets an address such as `https://platform.contextand.com` open with a padlock and no warning.

## Synopsis

```text
pks proxy [host] --to <url> [--route host=url ...] [--port 443] [--no-trust] [--remove-trust]
```

| Option | Default | Purpose |
|---|---|---|
| `[host]` + `--to <url>` | | One hostname and the upstream it forwards to. |
| `--route <host=url>` | | Further hostname and upstream pairs. Repeatable. |
| `--port <port>` | `443` | Local HTTPS port. Binding 443 needs no admin on Windows or macOS; on Linux it needs root, so use e.g. `8443`. |
| `--no-trust` | off | Do not offer to install the CA. The browser will warn. |
| `--remove-trust` | | Remove the CA from the trust store, delete it and exit. The next run makes a new one. |

## What happens on a run

1. **The hosts file.** pks does not edit it. If a hostname does not resolve to this machine, the run prints the line to add, e.g. `127.0.0.1 platform.contextand.com`, which needs an admin editor.
2. **The CA.** Created once, valid for ten years, and stored AES-GCM-encrypted under `~/.pks-cli/proxy-ca/` with the same store as `pks cert`. It may only sign server certificates (path length 0). Its public part is written to `~/.pks-cli/proxy-ca/pks-proxy-ca.cer`.
3. **Trust.** On Windows, pks adds the CA to the current user's *Trusted Root Certification Authorities* store; Windows itself shows a dialog asking you to confirm, and no admin rights are needed. Chrome and Edge read that store, so restart the browser if it was open. On macOS, pks runs `security add-trusted-cert` against the login keychain, which asks for your password. On Linux, pks prints the path and the commands to trust it by hand. Firefox keeps its own store everywhere: import the `.cer` there.
4. **Server certificates.** One per hostname, issued fresh in memory on every start and valid for 30 days. Kestrel picks it by the name the browser asks for (SNI).
5. **Forwarding.** YARP forwards every request, websockets included, so Vite HMR keeps working. The original `Host` header goes upstream, so a dev server with a host allow-list (Vite's `server.allowedHosts`) has to allow the public name.

The proxy listens on loopback only. A request for a hostname with no route gets a 404 that says so.

## Security

Anyone who can read the CA's private key can issue certificates this machine trusts for any name. The key is encrypted with the same KEK as `pks cert` (`~/.pks-cli/.certs-kek`), which is tamper resistance, not protection from another process running as the same user. Run `pks proxy --remove-trust` when the demo is over if that matters on the machine.