# Two-Factor Action Guard

`pks` can hold real cloud credentials (Scaleway/Azure/Foundry) and can start billable
infrastructure — GPU instances especially. When `pks` runs inside a devcontainer that an **AI
agent** also drives, we need a hard guarantee: *the agent cannot start an expensive VM (or take
other sensitive actions) without the human's deliberate consent.*

This document describes the consent mechanism, the threat model it defends, and how it ties into
in-place updates of the baked-in binary.

## Threat model

In the baked devcontainer image (`templates/base-images/pks-fullstack-base/Dockerfile`):

- `pks` is a self-contained binary at `/usr/local/bin/pks` (root-owned).
- A dedicated **`pks` system user** owns the credential cache `/home/pks/.pks-cli` (mode `700`).
- The dev user `node` (and therefore any agent running as `node`) invokes pks via the sudoers rule
  `node ALL=(pks) NOPASSWD: /usr/local/bin/pks`.

So the agent **can** run any `sudo -u pks pks <subcommand>` as the credential-bearing user, but it
**cannot**:

- read files under `/home/pks/.pks-cli` (mode `700`, different user),
- run arbitrary non-pks commands as `pks` (the sudoers entry is scoped to the pks **binary** only),
- gain root.

The defense exploits exactly that asymmetry: gate sensitive **actions** behind a **TOTP second
factor** whose verifying **seed lives behind pks** (`/home/pks/.pks-cli/authenticator.json`, `600`)
and whose **6-digit code lives on the human's phone**. The agent can *run* pks but can't *read* its
files and can't obtain a code — so it can't pass the gate.

## Actions, not commands

Two-factor is attached to **semantic actions**, enforced at the **shared choke-point** where the
action actually executes — not at command parsing. Toggling one action therefore covers every
command that funnels through it (e.g. `vm.start` covers `pks vm start`, the `vm status` menu, and
the *silent* devcontainer/`claude`/`vibecast` remote auto-start).

| Action id | Default | Where it is enforced |
|---|---|---|
| `vm.create` | on | `VmInitCommand` (Azure + Scaleway create sites) |
| `vm.start` | on | `GuardedVmProvider.StartAsync` (decorator) + the two direct-`StartVmAsync` auto-start paths (`DevcontainerSpawnCommand`, `SshConnectCommand`) |
| `vm.stop` | off | `GuardedVmProvider.StopAsync` |
| `vm.destroy` | on | `GuardedVmProvider.DestroyAsync` |
| `vm.autoshutdown.write` | off | `VmAutoshutdownCommand` |
| `cloud.auth.write` | on | `ScalewayInitCommand` (static API-key paste; OAuth-browser providers are already human-gated) |
| `devcontainer.spawn.remote` | on | `DevcontainerSpawnCommand` remote entry (composes `vm.start`) |
| `pks.update` | on | `UpdateCommand` (binary replacement is the most sensitive action) |
| `policy.write` | on* | `ActionsCommand` (changing the policy) |
| `authenticator.write` | on* | `AuthenticatorInitCommand` (re-enroll/disable) |

`*` control-plane actions are enforced only **once a factor is enrolled** (see TOFU below).

Enforcement is centralized in `IActionGuard.RequireAsync(ActionRequest)`
(`src/Infrastructure/Services/Security/`). `GuardedVmProvider` decorates each registry-resolved
`IVmProvider`, so power operations are gated for every caller without per-command wiring. Gated
provider calls run **outside** any `AnsiConsole.Status(...)` spinner (Spectre forbids prompting
inside a live display).

## Enrollment: host-gated, fail-closed, self-locking

The agent can only reach pks through the sudoers escalation `sudo -u pks pks …`, which always sets
`SUDO_USER`. A trusted enrollment from the **Docker host** (`docker exec -it -u pks <ctr> pks
authenticator init`) or at **image-build time** (as root) does **not**. The enrollment flow uses
exactly that asymmetry (`SecurityContext.IsSudoInvoked`):

- **`pks authenticator init` refuses the sudo path.** It runs only from the host/build context, so
  the agent can neither enroll its own authenticator nor capture the generated secret off pks's
  stdout (the secret is shown only on the host terminal, which the in-container agent can't observe).
  It also refuses a redirected stdin/stdout so the secret can't be piped to a file.
- **Re-enroll / disable / change-policy still require a current code** (the self-lock), via the
  `authenticator.write` / `policy.write` actions.
- **Two-factor is opt-in (no breaking change).** With no authenticator enrolled the gate is inert —
  existing workflows run exactly as before. Enrolling (from the host) is what turns protection on.
  Because the agent can't enroll, it can't enable-then-satisfy the gate on its own; the trade-off is
  that a box you intend to protect but haven't enrolled yet is unprotected, as with any opt-in 2FA.
  (To make freshly-spawned containers protected from first boot, enroll at provision time.)

**Setup is one command from the host**, before or after the agent is running:

```
docker exec -it -u pks <container> pks authenticator init
```

Scan the QR/secret on the host terminal; from then on the in-container agent is fully gated.

## Anti-bypass guarantees

- **No seed exfiltration.** The seed is shown only at enrollment. No subcommand prints the seed or
  computes a current code; `ITotpSeedStore` exposes verify/enroll only — never a getter.
- **Single-use, replay-resistant.** A verified code's time-step is recorded (cross-process file
  lock) so a code the agent observes on stdin can't be replayed for a different action or in a
  parallel process within its 30s window.
- **Brute-force resistant.** Five consecutive failures trigger an exponentially-backing-off lockout.
- **Self-protecting control plane.** `policy.write`, `authenticator.write`, and `pks.update` are
  gated like any other action once enrolled.

### Residual risk

Because the agent owns the terminal of the pks process it launches, a *fully active, malicious*
agent could in principle observe a code the human types and try to race it onto a different request.
Single-use + per-time-step burning defeats the autonomous case (the agent can't get a code at all);
the stronger active-attacker case is closed by the planned out-of-band **phone-push** provider,
which the `ISecondFactor` abstraction is built to accommodate (TOTP is provider #1).

## Updating the baked binary (and `pks update --self`)

Replacing the pks binary is the most sensitive action — a swapped binary could disable the whole
gate — so it is gated by `pks.update` and the privileged file swap is kept **off the agent's path**.

- **`pks update --self`** (mirrors `aspire update`): pick a channel (**stable** = nuget.org,
  **daily** = NuGet preview from `main`), show the version diff, confirm, pass the `pks.update`
  factor, then apply the way that fits how pks was installed (`InstallMethodDetector`). For a
  `dotnet tool` install it runs `dotnet tool update -g pks-cli`. In the **baked** devcontainer pks
  runs as the non-root `pks` user and **cannot** replace `/usr/local/bin/pks`, so it **delegates** —
  printing the exact host command.
- **Host swap** (`scripts/host/pks-devswap.sh`): run on the Docker host (outside the agent's reach).
  `workspace` mode builds linux-x64 from the checkout (the dev loop — the secure replacement for
  `dotnet tool install -g`, which would otherwise run as `node` and leak credentials); `release`
  mode installs the latest published binary. It stages the binary, `docker cp`s it in, and invokes
  the only privileged step, `pks-apply-update`, as root.
- **`pks-apply-update`** (baked at `/usr/local/bin/pks-apply-update`): re-checks root, verifies an
  optional sha256, smoke-tests `--version` before and after, and swaps the
  `/usr/local/lib/pks/pks-<ts>` → `/usr/local/bin/pks` symlink atomically, rolling back on failure.
  There is **no** `node → root` sudoers entry for it: the swap is host-initiated by design.
