# Runner capability configuration + SSH handoff

**Status:** plan, v2. Supersedes the v1 "remote execution targets" draft (which proposed a
`RunnerTarget` store and a tunnelled-Docker alternative; both are dropped — see *Rejected*).

**Decisions already taken by Poul (2026-07-18):**

- A runner without Docker genuinely cannot serve devcontainer work. Advertise honestly. ✔
- **Also** add `needs` to dispatched jobs so the *server* stops handing them out. The
  "no www-site changes" constraint from v1 is lifted. ✔
- Do **not** drive a remote Docker daemon from the laptop (no `DOCKER_HOST`, no socket
  forwarding, no claude-spawn-style remote orchestration). Instead: help the operator
  **launch a runner on a remote machine over SSH**, where everything is local again. ✔
- `agentics runner start` becomes the configuration surface: which capabilities to
  advertise, which chat models to expose, and — when SSH targets exist — the option to
  run the runner there instead. ✔

---

## Problem

`pks agentics runner start` on a Docker-less machine takes jobs it cannot run, claims
them, then fails them. There are **three** independent defects.

### Defect A — the runner advertises capabilities it does not have

`ComputeCapabilitiesAsync` (`src/Commands/Agentics/Runner/AgenticsRunnerStartCommand.cs:682`)
opens at `:684` with:

```csharp
var caps = new List<string> { "alp_operator", "chat-session:v1", DevAgentSessionCapability };
```

Three capabilities, unconditional, never probed against Docker. Computed **once** at `:385`
and never refreshed, so a Docker daemon that starts or stops mid-run changes nothing.

### Defect B — the server has no `needs` to filter on

`findQueuedJobs` (`src/apps/www-site/src/lib/agentics-store.ts:706-731`) filters on
capabilities **only when the job carries needs**:

```ts
const jobNeeds: string[] = j.needs ?? [];
if (jobNeeds.length > 0 && !jobNeeds.every((n) => capabilities.includes(n))) return false;
```

and `dispatchStationJob` (`task-dispatch.ts:419-424`) spreads `needs` only when explicitly
passed — `buildAgentDefinition` never sets it (doc comment at `:269-270`). **Every ordinary
station job is `needs: []`**, and an unconstrained job is claimable by any runner, including
one that reported `capabilities: []`.

So Defect A alone fixes nothing for the transcript's job: honest advertisement changes no
routing decision when nothing is being matched against.

### Defect C — no way to say "run it on the Hetzner box instead"

`ExecuteSpawnModeAsync` calls only `SpawnLocalAsync` (`:975`). The operator has a perfectly
good Docker host registered as an SSH target and no way to point the work at it.

### The failure path being fixed

`DevcontainerSpawnerService.cs:69` (`"Checking Docker availability..."`) → `:73`
`CheckDockerAvailabilityAsync()` (`:533-575`) → `:74-79` `"Docker is not available or not
running"` → `AgenticsRunnerStartCommand.cs:996` → job PATCHed `completed`/`failure`.

### Two transcript premises that are false

- **There are no fixed ports 59111/59112.** `AgenticsProxy.FindFreePort()`
  (`AgenticsProxy.cs:320-327`) and `OtlpProxy.FindFreePort()` (`OtlpProxy.cs:264-271`) both
  bind `TcpListener(IPAddress.Loopback, 0)` — ephemeral, different every run.
- **`DOCKER_HOST` does not work as an escape hatch today.** The DI singleton is built with
  the parameterless `DockerClientConfiguration()` (`src/Program.cs:304-308`) which ignores
  it, while `RunDockerCommandAsync` (`DevcontainerSpawnerService.cs:1787`) and seven raw
  `ProcessStartInfo("docker")` sites honour it — setting it produces a split-brain.

---

## Design

Three separable pieces, in dependency order.

```
        pks agentics runner start --project o/p
                      |
        [1] probe: docker? claude creds? github token? chat providers?
                      |
        [2] configure (interactive first run, persisted after):
              capabilities   [x] alp_operator   [ ] devcontainer-session:v1 (no docker)
                             [x] chat-llm:v1    [x] git:push
              chat models    [x] claude-opus-4-8  [x] gpt-5.5  [ ] claude-sonnet-4-6
                      |
        [3] docker unavailable AND ssh targets exist?
              > run here (chat + git only)
                hetzner   (docker 27.1, ready)
                wingpu    (docker unreachable)
                      |
                      +-- picked a target --> handoff: register + launch over SSH
                      |                       then this process exits
                      |
                      +-- stay local --------> poll with reduced capabilities
                                               server never offers devcontainer jobs
                                               (because [4] gives them needs)

        [4] server: dispatchStationJob now emits needs: ['alp_operator']
```

### Which capability gates station jobs — `alp_operator`

This is the load-bearing choice and the evidence points one way.

| Candidate | Advertised by field runners? | Used server-side today? | Verdict |
|---|---|---|---|
| `alp_operator` | yes, unconditionally — **but only since v6.10.0** (commit `ced345c`) | never checked — doc-only (`agentics-store.ts:46`, `:177`) | **use this** |
| `devcontainer-session:v1` | yes, unconditionally (`:684`) | **does not exist in server code at all**; the only mention is a comment at `agentics-store.ts:175-178` saying the devagent path deliberately does *not* gate on it | no |
| a new string | no | no | no — every field runner would stop taking work |

`alp_operator` is the only string that is already universally advertised *and* semantically
means "I can run an operator + devcontainer." Emitting `needs: ['alp_operator']` is therefore
a no-op for every *reasonably current* runner — they pass the filter unchanged — while giving
the fixed runner a way to opt out by dropping it.

**CORRECTION (implementation, 2026-07-18):** "every runner in the field" was too strong.
`alp_operator` was introduced in `ced345c`, first shipped in **v6.10.0** — a pre-6.10 or
third-party runner advertises nothing and would have been silently cut off from all station
work by this change. Fixed at the source: `runners/jobs/route.ts` now distinguishes an
**absent** `capabilities` field (legacy runner → unconstrained, skip the needs check) from an
explicitly **empty array** (a real declaration → filter normally), and `findQueuedJobs`'
parameter changed from `capabilities: string[] = []` to `capabilities?: string[]`. This also
fixes a latent bug where `updateRunnerLastSeen` wiped a stored runner's capabilities to `[]`
on every legacy poll.

Client-side it is gated on `inProcess || dockerAvailable`: `--inprocess` runs jobs in a git
worktree with no Docker at all (`ExecuteInProcessAsync`, `:3340`), so it keeps the capability.

### Landmine: `opts.needs` currently doubles as "do not virtualize"

`task-dispatch.ts:377`:

```ts
const integrationRule = opts.needs ? null : findIntegrationInboundRule(stage, column.id, opts.fromColumnId);
```

Passing `needs` silently disables swimlane **service virtualization** (the mock-webhook
substitution). If `dispatchStationJob` starts always setting `needs`, every mock integration
in every swimlane stops firing and those stations start dispatching real jobs.

**This must be fixed in the same commit**, by giving the intent its own flag:

```ts
// task-dispatch.ts — replace the piggyback
const skipVirtualization = opts.skipIntegrationRule ?? false;
const integrationRule = skipVirtualization ? null : findIntegrationInboundRule(...);
```

and setting `skipIntegrationRule: true` at the two chat dispatch sites (`:621`
`dispatchChatSessionJob`, `:664` `dispatchChatLlmJob`) that relied on the old behavior.
This is the single highest-risk line in the whole plan.

### Landmine: the stale-job requeue loop

`StaleJobMonitor` (`TaskDetailPageClient.tsx:587-670`) re-fetches a queued job after
`staleJobTimeoutMinutes` (default 5) and calls `requeueJob` if it is still queued.
`requeueJob` (`p/actions.ts:1544-1585`) recreates from `staleJob.agentDefinition` verbatim
— **carrying `needs` over**. With an unsatisfiable `needs` and a task page left open, this
is an indefinite cancel/recreate loop. (`requeueJobAction`,
`runners/actions.ts:34-46`, is worse: it duplicates without cancelling.)

Mitigation, Phase 4: before requeueing, check whether any online runner satisfies the job's
`needs`; if not, surface *"no online runner has: alp_operator"* and stop the countdown
instead of recreating. There is no server-side reaper for queued jobs
(`listRunners`' sweep at `agentics-store.ts:360-391` only touches `in_progress`), so this
client-side guard is the only place it can be caught.

### Runner configuration — new persisted state

`~/.pks-cli/agentics-runners.json` currently holds **no** capability, model, or label field:

```
AgenticsRunnerRegistration (src/Infrastructure/Services/Models/AgenticsRunnerModels.cs:3-13)
  Id, Name, Token, Owner, Project, Server: string
  GitUrl: string?
  RegisteredAt: DateTime
```

Every chat-llm setting, the polling interval, and the git identity are re-derived from flags
or env on every start (`:381-384`). Registration always POSTs `labels: []`
(`AgenticsRunnerStartCommand.cs:629`, `AgenticsRunnerRegisterCommand.cs:103`).

Add an optional profile, defaulted-when-absent so existing files keep loading:

```csharp
public sealed class RunnerProfile
{
    // null = "auto" (probe decides). Explicit list = operator override.
    public List<string>? Capabilities { get; set; }
    // null = expose everything CanResolveAsync accepts. Explicit list = allowlist.
    public List<string>? ChatModels { get; set; }
    public List<string>? Labels { get; set; }
    public string? DefaultChatModel { get; set; }
    // Set when this project's work was handed to an SSH target.
    public string? SshTargetLabel { get; set; }
    public DateTime? ConfiguredAt { get; set; }
}
```

hung off `AgenticsRunnerRegistration.Profile`. `AgenticsRunnerConfigurationService` already
serializes with `WhenWritingNull` (`:18-23`), so a null profile is simply absent on disk and
old files round-trip unchanged. Note its `LoadAsync` swallows `JsonException` and returns a
fresh default (`:64-68`) — a corrupt file silently loses every registration. Add a
`.bak-{timestamp}` rename before returning the default.

### Chat model exposure is an enforcement gap, not a preference

`ExecuteChatLlmJobAsync` at `AgenticsRunnerStartCommand.cs:2960-2961`:

```csharp
var requestedModel = bodyProp.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
var effectiveModelId = string.IsNullOrWhiteSpace(requestedModel) ? chatLlmModelId : requestedModel;
```

`effectiveModelId` goes straight into `_chatProviderFactory.ResolveAsync` (`:3142`) with **no
allowlist check**. The runner-configured `--chat-llm-model` is only a default. So
`RunnerProfile.ChatModels` must gate **both**:

1. `chat.models.request` (`:2968-2981`) — filter `ListAvailableModelsAsync()` by the profile.
2. the completion path (`:2960-2963`) — reject an out-of-allowlist `model` with an explicit
   error frame rather than silently resolving it.

The picker's source already exists: `AgentChatProviderFactory.ListAvailableModelsAsync`
(`AgentChatProviderFactory.cs:83-99`) unions built-in defaults (`gpt-5.5`,
`claude-opus-4-7`, `claude-sonnet-4-6`, `:223-240`) ∪ `agent.models.*` keys from
`~/.pks-cli/settings.json` ∪ Foundry `EnabledModels`, then filters by `CanResolveAsync` —
i.e. it already knows what this machine can actually reach. The prompt idiom to copy is
`pks foundry select`'s `MultiSelectionPrompt` (`FoundrySelectCommand.cs:128-134`).

Note `chat.models.request` returns an empty list when `--chat-llm-backend-url` is set
(`:2972-2974`, deliberate) — the allowlist applies to the provider path only.

### SSH handoff — reuse `ssh-targets.json`, add no new store

`ISshTargetConfigurationService` (`SshTargetConfigurationService.cs:25-33`) already has
`AddTargetAsync` / `ListTargetsAsync` / `FindTargetAsync` / `RemoveTargetAsync` over
`~/.pks-cli/ssh-targets.json`, `FindTargetAsync` resolves host → label → `user@host`
(`:129-156`), and `pks vm init` already registers provisioned VMs as targets labelled by VM
name (`VmInitCommand.cs:471`, `:692`; refreshed on IP change by
`VmConnection.RegisterTargetAsync`, `VmConnection.cs:90-95`). **v1's `RunnerTarget` /
`agentics-targets.json` invention is deleted.** A "target" is just an `SshTarget`.

Three real obstacles:

**(a) `ManagedKeyId` is dropped by `ISshCommandRunner`.** It takes `RemoteHostConfig`
(`DevcontainerModels.cs:909-929` — `Host`/`Username`/`Port`/`KeyPath`), there is no
`SshTarget → RemoteHostConfig` converter, and every call site hand-maps the four fields.
Only `ssh connect`/`run`/`copy` resolve managed keys via `_keyStore.MaterializeAsync` into a
disposable 0600 temp (`SshConnectCommand.cs:116-128`). Poul's own registered target is
exactly this shape (`"keyPath": "", "managedKeyId": "d5eafcb2"`), so **the handoff cannot use
`ISshCommandRunner` until this is fixed.** Phase 3 prerequisite: add
`SshTargetExtensions.ToRemoteHostConfigAsync(ISshKeyStore)` materializing the managed key,
and route the handoff through it.

**(b) There is no shared target picker.** Eight sites reimplement selection and disagree:
`ssh connect` hard-errors at zero targets (`SshConnectCommand.cs:66-70`);
`init`/`containers`/`destroy` silently fall through to local; `devcontainer spawn` prompts
even at zero. None auto-selects a lone target, unlike VMs (`VmSelection.Pick`,
`VmConnection.cs:180-197`). Ship one helper — `SshTargetSelection.PickAsync` — modelled on
`VmSelection.Pick`, using the repo-wide `Label ?? Host` convention
(`SshConnectCommand.cs:86-91` is the cleanest exemplar). Do not add a ninth variant.

**(c) `systemctl --user` is not an established pattern here.** Grep returns **zero** hits for
`systemctl --user` or `loginctl enable-linger` anywhere in the repo. The existing
remote-daemon patterns are `nohup … &` with a `Task.Delay` as the "readiness check"
(`DevcontainerSpawnCommand.cs:1786`, `ClaudeSpawnCommand.cs:323`) and detached `tmux`
(`AgenticsRunnerStartCommand.cs:1617-1620`, `:3486-3805`; `UsageTmuxDriver.cs:33-152` for
the capture idiom). **Phase 3 ships tmux**, which is already a hard runtime dependency of
vibecast and therefore present on any box that runs devcontainer jobs. Systemd-user is a
Phase 6 upgrade for reboot persistence, explicitly reported as absent until then.

Readiness is **not** `tmux new-session` returning 0. The handoff polls
`GET .../runners` until a record with the new name reports `status === 'online'` (≤120 s),
mirroring the socket-wait discipline already used at `:1625-1631`.

**Runner name collision is silent data loss.** `registerRunner` upserts by name and rotates
the token in place (`agentics-store.ts:456-465`); two machines under one name invalidate each
other's tokens in a loop. Hard-refuse a remote name equal to the local `Dns.GetHostName()`
or to an existing server-side runner name.

---

## Phases

Every phase's exit gate includes `dotnet test` green (and `npm run build` for Phase 2).

### Phase 1 — Honest capabilities + pre-claim refusal (`fix:`, pks-cli)

Unblocks the transcript on its own, before any server change ships.

- `IRunnerExecutionCapabilityProbe` wrapping `CheckDockerAvailabilityAsync`
  (`DevcontainerSpawnerService.cs:533-575`) with a 60 s memo and a reason string.
- **Hoist the probe above both blocking preflights** — the GitHub device-code preflight
  (`:262-334`, hard `return 1`) and the `GitCredentialServer` construction/`StartAsync`
  (`:341-350`, which calls `ListenUnixSocket` on a `Path.GetTempPath()` path). Both run
  *before* capability computation at `:385`, so on Windows-without-Docker the process may
  never reach the poll loop at all. Skip both when spawn mode is unavailable.
- Skip the probe entirely when `settings.InProcess`.
- Gate `alp_operator` on `inProcess || dockerAvailable`; gate `chat-session:v1` and
  `devcontainer-session:v1` on `dockerAvailable`. Leave `chat-llm:v1`, `git:push`,
  `git-distribute` alone.
- Move the capability call from the one-shot at `:385` into the poll loop behind the memo,
  so the advertisement tracks reality rather than a startup snapshot.
  **CORRECTION (implementation, 2026-07-18):** the original bullet claimed "a daemon that
  comes back re-enables spawn work without a restart". That is **false and was not built**.
  `GitCredentialServer` is constructed once at startup and the GitHub device-code preflight
  is an interactive blocking flow that cannot run inside a poll iteration — so a runner that
  started without Docker cannot serve spawn work later even if `dockerd` returns.
  Capabilities are therefore tied to `settings.InProcess || credentialServer != null`, i.e.
  the *structural* ability to serve, and the runner stays honestly degraded for its process
  lifetime. Restarting it after starting Docker is required. Making recovery real would mean
  a non-interactive-safe preflight plus lazy credential-server construction — a follow-up.
- **Client-side pre-claim refusal**: immediately before `ExecuteSpawnModeAsync` at the final
  `else` (`:474`), re-check the probe; if unavailable, log one grey line and `continue` —
  never reaching `POST …/runners/generate-jitconfig` (`:879-883`). The poll does **not**
  claim (`jobs/route.ts:39` only reads), so the job stays `queued` for a capable runner.
  **This is permanent, not transitional** (see D2): Phase 2 only gives `needs` to
  `dispatchStationJob`, so the ~13 direct-`createRun` paths and every pre-Phase-2 runner
  rely on this refusal. Do not delete it after the server change ships.
- Degraded startup banner; second hint line at the spawn-failure site (`:996`).

**Files:** ADD `src/Infrastructure/Services/Runner/{IRunnerExecutionCapabilityProbe,RunnerExecutionCapabilityProbe}.cs`;
MODIFY `AgenticsRunnerStartCommand.cs` (`:262-350`, `:385`, `:414-476`, `:682-749`, `:996`),
`src/Program.cs`; ADD `tests/Services/Runner/RunnerCapabilityProbeTests.cs`,
`tests/Commands/Agentics/AgenticsRunnerDegradedStartTests.cs`.

**VERIFY**
1. Unit: probe unavailable + `inProcess=false` → caps exclude all three; `inProcess=true` →
   `alp_operator` retained and the probe never invoked (`Verify(..., Times.Never)`).
2. Unit: `TestConsole` non-interactive + stubbed poll returning one `jobType=null` job →
   assert **no** HTTP call to `*/runners/generate-jitconfig`.
3. Live: `sudo systemctl stop docker`; start the runner; dispatch an ordinary ALP station
   task; `curl -s .../runners | jq '.[]|{name,capabilities}'` lacks the three strings and
   `curl -s .../runs/{runId}/jobs/{jobId} | jq .status` → `"queued"`.
4. **Windows gate:** Docker Desktop stopped → the runner prints the degraded banner and
   reaches `Polling every 10s...` within 30 s.

### Phase 2 — Server-side `needs` (`feat:`, www-site)

- Fix the virtualization piggyback first: add `skipIntegrationRule` to the dispatch opts,
  change `task-dispatch.ts:377`, set it at `:621` and `:664`.
- `dispatchStationJob` emits `needs: ['alp_operator']` by default (overridable by
  `opts.needs`).
- Decide the direct-`createRun` sites (`task-create.ts:72`, `run/actions.ts:116`,
  `review/submit/actions.ts:168`, `mcp/operations.ts:144`, `runs/route.ts:56`, …): they
  bypass `dispatchStationJob` entirely. Simplest correct move is to default `needs` inside
  `createRun` when the definition has none, which covers all of them at once — but
  `runs/route.ts:56` takes a **client-supplied `agentDefinition`** straight from the body,
  so the default must not clobber an explicit `[]`.
- Surface the mismatch: extend `noOnlineRunnerWarning` (`mcp/operations.ts:51-57`) — today
  it only checks `status === 'online'` and is capability-blind — to report *"no online
  runner has: X"*. Mirror the `git:push` precedent at `settings/actions.ts:203-206`, which
  refuses to create the job and returns an actionable error.

**VERIFY** Unit on `findQueuedJobs`: a job with `needs:['alp_operator']` is invisible to a
runner reporting `[]` and visible to one reporting `['alp_operator']`. **Swimlane regression
(mandatory):** run the existing service-virtualization e2e — a cross-lane arrival into an
unbuilt station must still hit the mock webhook, not dispatch a real job. Live: Docker-less
runner + dispatched task → the runner's poll returns zero jobs (not "returns then declines").

### Phase 3 — `runner start` configuration flow (`feat:`, pks-cli)

- `RunnerProfile` on the registration + `.bak-{ts}` rename on corrupt load.
- Interactive first run: capability `MultiSelectionPrompt` (unavailable ones shown disabled
  with the probe's reason), chat-model `MultiSelectionPrompt` over
  `ListAvailableModelsAsync()`, default-model `SelectionPrompt`. Persist; subsequent starts
  are silent unless `--configure` is passed.
- Non-interactive (`_console.Profile.Capabilities.Interactive == false`, the injectable
  check per `ClaudeAnthropicCommand.cs:44-57`) → use the persisted profile, else auto. Never
  block. Flags always win over the profile.
- **Enforce `ChatModels` on both paths** (`:2968-2981` and `:2960-2963`).
- Send real `labels` at registration instead of `Array.Empty<string>()`.

**VERIFY** Unit: profile with `ChatModels=['gpt-5.5']` → `chat.models.request` returns only
that, and a `chat.completion.request` naming `claude-opus-4-7` is rejected without calling
`ResolveAsync`. Round-trip: an old `agentics-runners.json` with no `profile` key loads,
gains one, and reloads. Live: `--configure`, tick two models, restart, confirm silence and
correct advertisement.

### Phase 4 — SSH handoff (`feat:`, pks-cli)

- `SshTargetExtensions.ToRemoteHostConfigAsync(ISshKeyStore)` — **the `ManagedKeyId` fix**;
  route the handoff through it.
- `SshTargetSelection.PickAsync` shared helper (auto-select on one, explicit zero-state).
- Probe the chosen target over SSH: `docker info`, `tmux -V`, `dotnet --version`,
  `command -v dnx`.
- Handoff: mint the registration **locally** (the laptop has the GitHub identity; the remote
  does not) under a distinct name, refuse name collisions, scp the registration JSON 0600,
  launch `tmux new-session -d -s pks-agentics-{owner}-{project}` running
  `dnx pks-cli -- agentics runner start --project … --server …`, then poll
  `GET .../runners` until `status === 'online'` (≤120 s). **Do not** declare success on
  `tmux` exit 0.
- `pks agentics runner status|logs|stop <target>` via `tmux capture-pane -p`
  (`UsageTmuxDriver.cs:152`).
- Client-side requeue guard for the `StaleJobMonitor` loop (see Landmine above).

**VERIFY** Live, the actual transcript scenario: Windows/no Docker → `runner start` offers
`hetzner` → handoff → `curl .../runners | jq` shows two runners both online, the remote one
advertising `alp_operator` → dispatch the ALP task → it runs to `success` on the remote →
`ssh hetzner docker ps` shows the devcontainer, local `docker ps` is empty.

### Phase 5 — Credentials on the remote (`feat:`)

Claude credential volume detection (all three `pks-claude-*` shapes per the doc comment at
`:5151`), `agentics runner claude-login <target>`, opt-in forwarding of the GitHub token and
`foundry-credentials.json` at 0600 behind named consent. Without them the remote simply
advertises less — degraded, not broken.

### Phase 6 — Durability + docs (`feat:`)

`systemctl --user` + `loginctl enable-linger` upgrade path (new ground — see obstacle (c)),
`agentics runner doctor <target>` (clock skew — the 60 s heartbeat is unforgiving —, disk,
docker-usable-by-this-user, credential age) with a non-zero exit for CI. Docs:
`docs/RUNNER-CAPABILITIES-AND-SSH.md`, plus the behavior-change release note (a Docker-less
runner now leaves devcontainer jobs queued instead of failing them).

---

## Rejected

**Driving a remote Docker daemon** (`DOCKER_HOST`, `ssh -L`/`-R`, `tcp://…:2376`).
`DevcontainerSpawnerService.cs:2843-2889` bind-mounts three host dirs
(`/var/run/pks-creds`, `/var/run/pks-agentics`, `/var/run/pks-otlp`); bind sources are
resolved by the **daemon**, so a Windows temp path becomes an empty directory on the remote
and the whole credential/telemetry plane fails **invisibly** mid-job. `Directory.Exists`
at `:2854` is a local probe that would silently pick the wrong branch. And the
`ssh -R /remote.sock:/local.sock` forwarding that would fix it **does not exist in Windows
OpenSSH** — the reporting platform. Confirmed rejected by Poul.

**A new `RunnerTarget` store** (v1's `agentics-targets.json`) — `ssh-targets.json` already
does this.

**VM auto-provisioning as a headline feature** — a provisioned VM already registers itself
as an SSH target, so it reduces to Phase 4 for free. `IVmProvider.ProvisionAsync` +
cost/teardown lifecycle is deferred; it also sits against the marketed position in
`content/products/pks-github-runner.ts:35-40`.

---

## Decisions taken (2026-07-18)

- **D1 — chat-session gating.** `chat-session:v1` **is dropped** when Docker is unavailable.
  Kind A chat runs in a devcontainer, so a Docker-less runner genuinely cannot serve it.
  User-visible via `ChatProvider.tsx:167`; accepted. `chat-llm:v1` is unaffected —
  `ExecuteChatLlmJobAsync` (`:2792`) never touches the spawner, so a Docker-less machine
  still serves chat through the LLM path.
- **D2 — `needs` scope.** Phase 2 changes **`dispatchStationJob` only**. The ~13 direct
  `createRun` callers (`task-create.ts:72`, `run/actions.ts:116`,
  `review/submit/actions.ts:168`, `mcp/operations.ts:144`, `runs/route.ts:56`,
  `distribution-store.ts:534`/`:565`, …) keep `needs: []` and stay claimable by any runner.
  **This is why the Phase 1 client-side pre-claim refusal is permanent, not transitional** —
  it is the only thing protecting those paths. Do not remove it once Phase 2 ships.
  Deliberately avoids the absent-vs-empty ambiguity at `runs/route.ts:56`, where
  `agentDefinition` is client-supplied.
- **D3 — credential forwarding (Phase 5).** Forward at 0600 behind an explicit per-file
  consent prompt. When `ActionGuard.RequireAsync` is inert (no factor enrolled,
  `ActionGuard.cs:48-56`) the prompt **says so plainly** and offers `pks authenticator init`
  inline rather than silently pretending to be gated.

## Open questions

- **Q4 — release ordering.** The handoff installs pks-cli from the public `dnx` feed, so
  until Phase 1 is published the remote runs a runner *without* the fix. Enforce a minimum
  version after install, and gate Phase 4 on a Phase 1 release. Confirm the floor value at
  Phase 1 release time.
