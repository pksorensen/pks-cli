# `pks t3` — a private T3 Code box

**Status: draft.** The code compiles, the generated bash passes `bash -n`, and the secret-handling
path goes through `SecretSink` so the gate test holds. Nothing here has been run against a real
Azure subscription yet — see [Unverified](#unverified) before trusting it.

```
dnx pks-cli t3 init
```

One command that ends with a URL you can log into with your Microsoft account, running
[T3 Code](https://github.com/pingdotgg/t3code) with Codex pointed at Azure AI Foundry.

## What T3 Code is, and what it isn't

T3 Code is a control plane for coding agents: a local server plus web/desktop/mobile clients that
drive `codex`, `claude`, `cursor-agent`, `grok` or `opencode` on the machine the server runs on. It
does not talk to a model itself and holds no API keys — the agent CLIs bring their own auth.

Two consequences shape this whole command:

1. **"Foundry support" is Codex configuration, not T3 configuration.** T3 spawns `codex`; whatever
   `~/.codex/config.toml` says is what T3 gets. So the wiring is a Foundry provider block plus
   something serving Azure tokens on loopback.
2. **T3 has no OIDC.** Its remote-access story is a one-time pairing token, optionally fronted by
   Tailscale HTTPS. There is no "sign in with Entra" setting to switch on.

## The Entra decision, and what it costs

The ask was "an Entra app for login." Since T3 has no OIDC of its own, the only way to get one is a
gate in front of it: **oauth2-proxy** terminating the Entra OIDC flow, **Caddy** terminating TLS,
and T3 bound to `127.0.0.1` so it is unreachable except through both.

```
browser ──https──> Caddy :443 ──> oauth2-proxy :4180 ──> t3 serve :3773
                     (ACME)          (Entra OIDC)         (loopback only)

                                        codex ──> pks foundry proxy :8788 ──> Azure AI Foundry
                                                  (systemd, token in /etc/pks-t3/foundry.env)
```

**The cost: `https://app.t3.codes` stops working against this box.** That hosted client fetches the
backend *directly* from the browser using a pairing token — a cookie-based OIDC gate on a different
origin cannot satisfy it, and there is no interactive login inside a cross-origin `fetch`. What you
get instead is T3's own web UI, served by the same server, on your own domain, behind Entra.

If you would rather keep `app.t3.codes`, don't use this command's Entra half — run
`t3 serve --tailscale-serve` and pair with the QR code. That is a different security model
(tailnet membership instead of Entra), not a worse one.

## What the command does, in order

| Step | Mechanism | Notes |
|---|---|---|
| 1. VM | chains into `VmInitCommand`, or reuses an SSH target | joins on the SSH target registered after `vm init` returns |
| 2. Domain | prompts | no default is derivable — see below |
| 3. Deployment | prompts, defaulting to `pks foundry`'s selected model | |
| 4. Entra app | `IEntraApplicationService.InitAsync` | adopt-or-create, registers the redirect URI, mints a secret |
| 5. Bootstrap | one `ssh` running generated bash | node 22, `t3`, `@openai/codex`, `pks-cli`, caddy, oauth2-proxy, four systemd units |
| 6. Credentials | `ssh` + **stdin** | client secret, cookie secret, Foundry passthrough token |
| 7. Summary | | prints the redirect URI it already registered |

### Why the domain is a prompt and not a default

Entra rejects a `Web` redirect URI that isn't HTTPS (localhost excepted). HTTPS needs a certificate,
a certificate needs a name that resolves, and nothing about a fresh VM's IP address implies one. So
the command asks — and it asks *before* touching Graph, so a wrong answer costs nothing.

The user still has to create the A record. Caddy fetches the certificate during bootstrap, so if DNS
isn't in place the bootstrap is where you find out.

### Why the secret goes down a pipe

`ISshCommandRunner.RunAsync` puts the remote command on an `ssh` argv. sshd hands that to a login
shell: it lands in shell history, in `ps` for as long as it runs, and in any auditd `execve` rule.
A client secret cannot go there.

So there are two new pieces:

- `ISshCommandRunner.RunWithStdinAsync` — runs a remote command and feeds its stdin from a callback.
  The remote side is a `sudo tee` into a 0600 root-owned file under `/etc/pks-t3/`.
- `SecretSink.WriteTo` / `SecretSink.WriteEnvLine` — the sanctioned way to get a `SecretValue` onto a
  stream. This matters structurally: `SecretResolverGateTests` fails the build if anything under
  `src/Commands/` so much as names `Reveal(`, so the command layer *cannot* hold the plaintext even
  by accident. The class comment on `SecretSink` already listed "hands it to an ssh session" as a
  sanctioned destination; it just had no helper for it.

Two env files, not one, on purpose:

| File | Owner | Holds |
|---|---|---|
| `/etc/pks-t3/oauth2-proxy.env` | `root:root` 0600 | Entra client id + secret, cookie secret |
| `/etc/pks-t3/foundry.env` | `root:<vmuser>` 0640 | Foundry passthrough token |

The Foundry passthrough runs as the VM user (it is what spawns agents). Merging the files would let
that user — and therefore every agent T3 runs — read the Entra client secret.

### Why the Foundry proxy is a systemd unit

`pks codex` starts its passthrough **in-process** and tears it down in a `finally`. That is right for
an interactive `pks codex` and wrong here: T3 spawns `codex` directly, so the passthrough has to
outlive any single invocation. Hence `pks-foundry-proxy.service` on a fixed port and a *static*
provider block in `~/.codex/config.toml`, where `pks codex` writes a per-launch one against a random
port.

## What still needs a human

**DNS.** `<domain>` → the VM's public IP, ports 80/443 open.

**Foundry sign-in on the box.** `AzureFoundryAuthService` does an authorization-code flow against an
`HttpListener` on `http://localhost:<port>` — it prints the URL and tries to open a browser. On a
headless VM, forward the port and open it locally:

```bash
ssh -L 8400:localhost:8400 <user>@<vm>
pks foundry init                     # open the printed URL in your own browser
sudo systemctl enable --now pks-foundry-proxy
```

This is deliberately not automated in the draft. The stored credential is a user refresh token; the
alternative to signing in on the box is copying that token onto an internet-facing machine, which is
a worse trade than one `ssh -L`.

## Unverified

Things a real provisioning run needs to settle, in rough order of how likely they are to bite:

1. **T3 loopback trust.** Behind the proxy, T3 sees every connection from `127.0.0.1`. If it treats
   loopback as trusted it will skip pairing entirely — convenient (Entra becomes the sole gate) but
   it also means anything that reaches port 3773 is in. The bootstrap binds `--host 127.0.0.1` and
   opens no firewall rule for it either way. If loopback trust *doesn't* apply, expect one pairing
   step after the first Entra login. Read `apps/server/src/cloud/http.ts` upstream to settle it.
2. **`t3 serve` flag names.** Taken from the upstream remote-access doc (`--host`, `--ttl`,
   `--tailscale-serve`); `--port` is assumed and 3773 comes from the doc's example URL.
3. **oauth2-proxy 7.8.1 pinned.** The generated config uses v7 key names. A newer release is fine but
   is not automatic, deliberately — an unpinned fetch would silently stop matching the config.
4. **The `vm init` join.** `VmInitCommand` returns an `int`, so the new SSH target is found by
   diffing the target list. Fine while nothing else registers targets concurrently; a real fix is for
   `vm init` to return the target it made.
5. **Foundry passthrough token on the command line.** `pks foundry proxy` only takes `--token`, so
   the token is visible in `ps` on the box. Adding an env-var fallback to that command would close it.
6. **Codex `wire_api = "responses"`** is copied from `CodexCliConfig.BuildProxyProviderBlock`; the
   static block should be generated from that same code rather than restated here.

## Files

| File | |
|---|---|
| `src/Commands/T3/T3InitCommand.cs` | orchestration + prompts + summary |
| `src/Commands/T3/T3BootstrapScript.cs` | the generated remote bash, in three phases |
| `src/Commands/T3/T3Settings.cs` | branch-level options |
| `src/Infrastructure/Services/SshCommandRunner.cs` | `RunWithStdinAsync` |
| `src/Infrastructure/Services/Security/SecretSink.cs` | `WriteTo`, `WriteEnvLine` |
| `src/Program.cs` | the `t3` branch |
| `tests/Commands/T3/T3BootstrapScriptTests.cs` | pins the loopback bind, the file modes, and "no secret in phase 1" |

## One trap worth knowing

`config.AddBranch<TSettings>(...)` binds `TSettings`' options **only when the flag appears before the
subcommand**. `pks t3 --domain x init` binds; `pks t3 init --domain x` parses without error and
arrives with `Domain == null`, so the command prompts for a value it was already handed. Nothing
warns. `--vm` and `--domain` therefore live on `T3InitCommand.Settings`, and `T3Settings` is an empty
marker like `VmSettings` — which is empty for the same reason, whether or not anyone noticed.

## Try it without a VM

```bash
pks t3 init --vm <existing-ssh-target> --domain t3.example.com --skip-bootstrap
```

Registers the Entra app and prints the bash instead of running it.
