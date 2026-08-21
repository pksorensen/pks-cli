# `pks t3` — a private T3 Code box

**Status: draft.** The code compiles, the generated bash passes `bash -n`, and the secret-handling
path goes through `SecretSink` so the gate test holds. The credential hand-off has been proved
end-to-end against a real `pks` (delivered blob → migrated → read back), but no full run against a
real Azure subscription has happened yet — see [Unverified](#unverified) before trusting it.

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
| 2. Hostname | DNS label on the VM's public IP + an NSG rule for 80/443 | Azure only; falls back to a prompt — see below |
| 3. Deployment | prompts, defaulting to `pks foundry`'s selected model | |
| 4. Entra app | `IEntraApplicationService.InitAsync` | adopt-or-create, registers the redirect URI, mints a secret |
| 5. Bootstrap | one `ssh` running generated bash | node 22, `t3`, `@openai/codex`, caddy, oauth2-proxy, a service account, four systemd units |
| 6. Credentials | `ssh` + **stdin** | client secret, cookie secret, Foundry passthrough token |
| 7. Foundry | `ssh` + **stdin** | the pks binary, then this machine's Foundry credential |
| 8. Summary | | prints the URL to open |

### Where the hostname comes from

Entra rejects a `Web` redirect URI that isn't HTTPS (localhost excepted). HTTPS needs a certificate,
and a certificate needs a name that resolves — which is why the first draft stopped and asked for
one, and why it then told the operator to go and create an A record and open two ports before
anything would work. That is most of the manual labour the command exists to remove.

On Azure the name is already there for the asking: a `domainNameLabel` on the VM's public IP yields
`<label>.<region>.cloudapp.azure.com` at no cost. So the command claims one, opens 80 and 443 on the
NSG (`pks vm init` opens only 22, so ACME would fail otherwise), and reads the FQDN back off the
resource rather than composing it — the suffix belongs to the region, and regions do not all spell
it the way the docs' example does.

Both ARM calls are written to be re-runnable, and both avoid the same trap in different ways:

- The **public IP** is read, mutated and written back. A reconstructed body would drop the
  properties ARM has since attached to it.
- The **NSG rule** is a PUT of the *child* resource. A PUT of the NSG itself carrying only the new
  rule deletes every rule it does not restate — starting with `AllowSSH`, i.e. locking the command
  out of the box it is halfway through configuring. It also scans the existing rules first, so a
  port already opened by hand adds nothing and a taken priority is skipped.

`--domain` overrides all of this. Anything that is not an Azure VM `pks` provisioned — a Scaleway
box, a hand-registered SSH target, a subscription it cannot read — falls back to the old prompt.

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

One linkage is easy to lose: `env_key = "PKS_CODEX_TOKEN"` means *codex* reads that variable from its
own environment, and codex's parent is `t3.service` — not the passthrough unit. So `t3.service`
sources `/etc/pks-t3/foundry.env` too (`EnvironmentFile=-`, optional, because phase 1 enables the
unit before the file exists), and delivering the token ends in `systemctl try-restart` for both units
so a rotation reaches the running processes rather than only the file. A test pins this: get it wrong
and every unit reports healthy while every model call 401s.

### How pks gets onto the box, and why not from a package

The passthrough is `pks foundry proxy`, so the box needs `pks`. It does **not** come from a package
manager:

- **npm.** The first draft ran `npm install -g … pks-cli`. The package by that name on the npm
  registry is an unrelated project last published in 2022. It installs cleanly, which is the whole
  problem: nothing fails, and `pks` on the box is a stranger's binary or absent.
- **NuGet.** The real package is there and current, but `dotnet tool install` needs a .NET SDK on a
  machine whose only other job is to run one proxy.

So the command pushes its own embedded linux-x64 binary down the same stdin channel the secrets use,
and installs it at `/usr/local/bin/pks`. Only builds made with `-p:EmbedPksLinux=true` carry one;
without it the Foundry half is **skipped and reported**, not half-configured.

### The Foundry credential, and the position this reverses

An earlier version of this document said the credential would not be automated, because "the
alternative to signing in on the box is copying that token onto an internet-facing machine, which is
a worse trade than one `ssh -L`."

That was wrong, and worth saying plainly rather than quietly deleting. Signing in *on the box* puts
exactly the same user refresh token on exactly the same internet-facing machine. The only thing the
manual route changed was that a human watched it happen. It bought no security and cost the command
its reason to exist.

What does change the exposure is **who on the box can read it**. T3's entire purpose is to spawn
coding agents as the VM user; anything that user can read, every agent on that box can read and send
anywhere. So the passthrough gets its own account:

| | |
|---|---|
| Account | `pks-foundry`, system user, `/usr/sbin/nologin` |
| Home | `/var/lib/pks-foundry`, 0700 |
| Credential | `~/.pks-cli/settings.json` → migrated into an AES-GCM store on first read |
| Agents see | `PKS_CODEX_TOKEN` and a loopback port, neither of which is worth anything off the box |

Two details that are easy to get wrong and produce a unit that starts cleanly and does nothing:
systemd does not set `HOME` for a `User=`, and `pks` keeps its store under `$HOME` — hence the
explicit `Environment=HOME=`. And the credential is delivered as *plaintext* in `settings.json`
because that is the only format the receiving `pks` migrates; the delivery script reads it once as
`pks-foundry` immediately afterwards to force that migration rather than leaving it on disk.

This is still a real transfer of a real credential. If that is not a trade you want on a given box,
`--skip-bootstrap` or simply not storing Foundry credentials locally both leave it out, and the
summary tells you what is missing.

## What still needs a human

On an Azure VM provisioned by `pks vm init`, with a Foundry credential stored locally and a build
that embeds the linux binary: nothing. The command ends with a URL.

Off that path it degrades one step at a time, and says which step: a non-Azure box asks for a
hostname, a build without the embedded binary skips the passthrough, no local Foundry credential
prints the `pks foundry init` + re-run pair. Everything is idempotent, so re-running after fixing
one of them is the supported repair.

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
4. **Certificate timing.** Caddy issues for a named site block when it *starts*, not on the first
   request, so the race is against `caddy` coming up before the DNS label resolves rather than
   against the browser. Either way a retry is the answer and the box is not broken — but do not
   debug this on the assumption that loading the page is what triggers issuance.
5. **The `vm init` join.** `VmInitCommand` returns an `int`, so the new SSH target is found by
   diffing the target list. Fine while nothing else registers targets concurrently; a real fix is for
   `vm init` to return the target it made.
6. **Foundry passthrough token on the command line.** `pks foundry proxy` only takes `--token`, so
   the token is visible in `ps` on the box. Adding an env-var fallback to that command would close it.
7. **The first Entra login may need an email-claim knob.** Entra ID tokens frequently carry no
   `email` claim (UPN-only accounts) and no `email_verified`, and oauth2-proxy's `oidc` provider can
   refuse the login on that alone. The two knobs are `oidc_email_claim = "preferred_username"` and
   `insecure_oidc_allow_unverified_email = true`. Neither is set — setting them blind weakens the
   config for tenants that don't need it. Try them, in that order, if the first real login fails.
8. **Codex `wire_api = "responses"`** is copied from `CodexCliConfig.BuildProxyProviderBlock`; the
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
| `src/Infrastructure/Services/AzureVmService.cs` | `EnsurePublicIpDnsLabelAsync`, `EnsureInboundPortsAsync` |
| `src/Infrastructure/Services/AzureFoundryAuthService.cs` | `WriteRemoteSettingsAsync` |
| `tests/Commands/T3/T3BootstrapScriptTests.cs` | pins the loopback bind, the file modes, the env chain, the service account, and "no secret in phase 1" |
| `tests/Services/Azure/AzureDnsLabelTests.cs` | the DNS label grammar and the port-range cover check |

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

Registers the Entra app and prints the bash instead of running it. Passing `--domain` also skips the
ARM calls, so this touches nothing on Azure.
