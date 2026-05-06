# Credential Proxy Architecture for Azure AI Foundry

## Overview

When pks-cli launches a devcontainer with Azure AI Foundry credentials, it must make those
credentials available inside the container so that tools like `claude` can authenticate to
Azure Cognitive Services. This document describes the two deployment scenarios, the current
approach, and the proposed Host-side token server with SSH reverse tunnel approach.

---

## Deployment Scenarios

### Scenario A: Remote VM (Win → Azure VM → Docker → Devcontainer)

```
+----------------+         SSH (port 22)        +------------------+
|  Host (Win)    |  --------------------------> |  Azure VM        |
|  pks-cli       |                              |  (Ubuntu)        |
|                |                              |                  |
|  Credentials   |                              |  Docker daemon   |
|  (TenantId,    |                              |  +------------+  |
|   RefreshToken)|                              |  |Devcontainer|  |
+----------------+                              |  |  claude    |  |
                                                |  +------------+  |
                                                +------------------+
```

The Host has a dynamic IP address and no inbound firewall rules. The Azure VM has a static
public IP reachable over SSH. The Host always initiates the connection; the VM cannot reach
back to the Host.

### Scenario B: Local Docker (Win → Docker → Devcontainer)

```
+--------------------------------------------------+
|  Host (Win)                                      |
|  pks-cli                                         |
|  Credentials (TenantId, RefreshToken)            |
|                                                  |
|  Docker daemon                                   |
|  +--------------------------------------------+  |
|  |  Devcontainer                              |  |
|  |  claude  (reaches host via 172.17.0.1)    |  |
|  +--------------------------------------------+  |
+--------------------------------------------------+
```

The Docker bridge IP `172.17.0.1` (or `host.docker.internal` on Docker Desktop) is always
reachable from within any container. No SSH tunnel is needed.

---

## Current Approach: Copy Credentials to VM

### How It Works (Scenario A)

`DevcontainerSpawnCommand.BuildFoundryEnvArgsAsync()` performs the following on every session
start:

1. SSH into the VM and kill any stale process on port 40342.
2. Serialize `{TenantId, RefreshToken}` to JSON, base64-encode it, and write it to
   `~/.pks-cli/foundry-credentials.json` on the VM (chmod 600).
3. Write a self-contained Python HTTP server (the MSI token server) to `/tmp/pks-msi-server.py`
   on the VM and launch it with `nohup python3 ... &`.
4. Pass env vars into `docker exec`:
   - `IDENTITY_ENDPOINT=http://172.17.0.1:40342`
   - `IDENTITY_HEADER=<random-secret>`
   - `CLAUDE_CODE_USE_FOUNDRY=1`
   - `ANTHROPIC_FOUNDRY_RESOURCE=<resource-name>`
   - Model override env vars

The MSI server on the VM receives GET requests from the devcontainer over the Docker bridge,
validates the `X-IDENTITY-HEADER` secret, and exchanges the stored refresh token for an
access token by calling `login.microsoftonline.com`.

### Trade-offs

| Aspect | Detail |
|--------|--------|
| Security | **Credentials leave the Host.** The refresh token is written to disk on the VM (`~/.pks-cli/foundry-credentials.json`). Any process running as the same user on the VM can read it. |
| Attack surface | The MSI server binds to `0.0.0.0` on the VM, so it is reachable by any process on the VM or any container on the Docker bridge. The `X-IDENTITY-HEADER` secret is the only gate. |
| Simplicity | Simple: no persistent connection required. The SSH session that writes credentials can close immediately. |
| Staleness | Refresh tokens are long-lived; if the VM is compromised the token persists on disk. |
| Cleanup | Credentials are not automatically removed when the session ends. |

---

## Proposed Approach: Host-Side Token Server with SSH Reverse Tunnel

### Core Idea

Keep ALL credential material on the Host. Run the MSI token server on the Host as a
local-only process. Use SSH reverse port forwarding so that the VM's port 40342 is
transparently forwarded back through the existing SSH connection to the Host's token server.

### SSH Reverse Tunnel Mechanism

OpenSSH's `-R` flag creates a reverse (remote) port forward:

```
ssh -R <remote_port>:localhost:<local_port> user@remote
```

This instructs the SSH server on the remote VM to open a listening socket on `remote_port`.
Any TCP connection to that port on the VM is tunnelled back through the SSH connection and
delivered to `localhost:<local_port>` on the Host.

```
+----------------+  SSH + reverse tunnel  +------------------+
|  Host (Win)    |  -------------------> |  Azure VM        |
|                |                        |                  |
|  Token server  | <-- tunnel data -----  |  :40342 socket   |
|  :40342        |  (same SSH conn)       |  (Docker bridge  |
|  (localhost    |                        |   exposes this   |
|   only)        |                        |   to containers) |
+----------------+                        +------------------+
```

The SSH connection is initiated by the Host (so no inbound firewall rules on the Host are
needed). The reverse tunnel piggybacks on that same authenticated connection. The VM never
needs to know the Host's IP address.

#### Example SSH invocation

```
ssh -o StrictHostKeyChecking=no -p 22 \
    -R 0.0.0.0:40342:localhost:40342 \
    -i /path/to/key \
    user@vm-host \
    "docker exec -it <containerId> claude --dangerously-skip-permissions"
```

The `-R 0.0.0.0:40342:localhost:40342` addition is the only change required to the SSH
invocation. No additional .NET libraries are required; pks-cli already shells out to `ssh`.

### Why This Works Even Though the VM Can't Reach the Host

Normally, a process on the VM cannot initiate a TCP connection to the Host because:
- The Host has a dynamic IP (DHCP / CGNAT)
- The Host firewall blocks inbound connections

The SSH reverse tunnel sidesteps this entirely. The tunnel multiplexes over the existing
outbound SSH connection that the Host established. The VM's kernel thinks the socket on
:40342 is local; the SSH daemon silently forwards traffic back to the Host through the
already-open, authenticated channel.

### Analogy: VS Code Port Forwarding

This is exactly how VS Code's "Forward a Port" feature works:

1. VS Code client (desktop) opens SSH to the remote VM.
2. VS Code Server on the VM listens on the forwarded port.
3. When code in the container connects to that port, data travels back through the SSH
   connection to the VS Code client on the desktop.
4. The desktop then connects to `localhost:<localPort>` as if the service were local.

pks-cli can use the identical mechanism. The SSH connection that launches `claude` or
`vibecast` simultaneously carries the credential tunnel.

### Scenario A with Reverse Tunnel

```
Host (Win)                     Azure VM                    Devcontainer
----------                     --------                    ------------
Token server                   sshd opens :40342           http://172.17.0.1:40342
(127.0.0.1:40342)             forwarded via -R            (Docker bridge to VM host)
     ^                              |
     |                    Docker bridge (172.17.0.1)
     +<------ SSH reverse tunnel (same connection) -------+
```

Step by step:
1. pks-cli starts the MSI token server locally, binding only to `127.0.0.1:40342`.
2. pks-cli adds `-R 0.0.0.0:40342:localhost:40342` to the SSH command (requires `GatewayPorts yes` on VM sshd, or use a relay — see below).
3. OpenSSH on the VM opens `:40342` bound to all interfaces.
4. The devcontainer makes HTTP GET to `http://172.17.0.1:40342` (the Docker bridge IP).
5. The Docker bridge delivers the connection to the VM's `:40342` socket managed by `sshd`.
6. `sshd` forwards it back through the tunnel to the Host's `127.0.0.1:40342`.
7. The Host token server handles the request, refreshes the token locally, and returns it.

No credentials ever leave the Host.

### GatewayPorts Requirement

By default, `sshd` binds remote-forwarded ports to `127.0.0.1` on the VM. For the Docker
bridge to reach the forwarded port, sshd must bind to `0.0.0.0`. This requires either:

**Option A (preferred):** Add `GatewayPorts yes` to `/etc/ssh/sshd_config` on the VM.
This is done once during `pks vm init`. Then `-R 0.0.0.0:40342:localhost:40342` works as-is.

**Option B (fallback, no sshd_config change):** After the SSH session establishes, run a
small relay on the VM (one-liner with `socat` or a minimal Python script) that binds to
`0.0.0.0:40342` and forwards to `127.0.0.1:40342` where the tunnel lands. More moving
parts but requires no VM-level configuration.

### Scenario B with Local Docker (No Tunnel Needed)

For local Docker, the Host and the token server are already co-located. The token server
just needs to bind to an address reachable from within Docker:

- **Linux:** bind to `172.17.0.1` (the Docker bridge interface)
- **Windows / macOS with Docker Desktop:** bind to `0.0.0.0`; inside the container use
  `host.docker.internal` which Docker Desktop resolves to the host machine

Env vars passed to `docker exec` remain identical in structure:
- `IDENTITY_ENDPOINT=http://172.17.0.1:40342` (Linux) or
  `IDENTITY_ENDPOINT=http://host.docker.internal:40342` (Windows/macOS)
- `IDENTITY_HEADER=<secret>`

No SSH tunnel required.

---

## Lifecycle Considerations

### Connection duration

The reverse tunnel exists only for the duration of the SSH connection. For `pks claude` and
`pks vibecast` this is natural — the terminal SSH session runs interactively until the user
exits. When the user quits `claude`, the SSH process exits, the tunnel closes, and the token
server is shut down via `await using` / `CancellationToken`.

### Token server process management

The token server must be:
1. Started **before** the SSH connection is established.
2. Bound to `127.0.0.1` (Scenario A) — not reachable from the network, only via the tunnel.
3. Kept alive for the duration of the session.
4. Cleanly shut down when the SSH process exits.

`PksSSHAgent` (`Infrastructure/Services/PksSSHAgent.cs`) is the existing precedent for a
long-lived background infrastructure process managed by pks-cli.

### Port conflicts

If port 40342 is already in use on the Host:
1. Bind to `:0` and let the OS assign an ephemeral port.
2. Pass the actual port to both the `-R` flag and the `IDENTITY_ENDPOINT` env var.

---

## Security Comparison

| Property | Current (credentials on VM) | Proposed (tunnel) |
|----------|-----------------------------|--------------------|
| Credentials written to VM disk | Yes (`foundry-credentials.json`) | No |
| Credentials in VM memory | Yes (Python process) | No |
| Token server network exposure | `0.0.0.0` on VM | `127.0.0.1` on Host only |
| Reachable by other VM users | Yes | No (tunnel is authenticated) |
| Compromise scope if VM is hacked | Refresh token stolen | No credential exposed |
| Cleanup on session end | Manual (file stays on disk) | Automatic (process exits) |

---

## Implementation Reference

### Key Files

| File | Role |
|------|------|
| `src/Infrastructure/Services/FoundryMsiTokenServer.cs` | **New** — Host-side IFoundryMsiTokenServer + FoundryMsiTokenServer |
| `src/Commands/Devcontainer/DevcontainerSpawnCommand.cs` | Refactor `BuildFoundryEnvArgsAsync` (line 1554) — remove VM deploy, start local server, new return type `(string, IFoundryMsiTokenServer?)` |
| `src/Commands/Claude/ClaudeSpawnCommand.cs` | `OnAfterRemoteSpawnAsync` (line 53) — inject token server, add `-R` to `interactiveSshArgs` (line 74), `await using` scope |
| `src/Commands/Vibecast/VibecastCommand.cs` | `OnAfterRemoteSpawnAsync` (line 38) — identical pattern to ClaudeSpawnCommand |
| `src/Commands/Vm/VmInitCommand.cs` | Add `GatewayPorts yes` step after cloud-init poll (around line 437) |
| `src/Program.cs` | Register `IFoundryMsiTokenServer` transient (around line 233 near `IAzureFoundryAuthService`) |
| `src/Infrastructure/Services/PksSSHAgent.cs` | Reference pattern for background process lifecycle (`IAsyncDisposable`, `CancellationTokenSource`, `StartAsync`) |
| `tests/Services/FoundryMsiTokenServerTests.cs` | **New** — unit + integration tests |

### Code Locations: BuildFoundryEnvArgsAsync

**Current** (`DevcontainerSpawnCommand.cs` line 1554–1666):
```csharp
// Takes: (string sshArgs, SshTarget target)
// Returns: string (empty or "-e IDENTITY_ENDPOINT=... -e IDENTITY_HEADER=...")
// Does:
//   1. fuser -k to kill stale process (line 1576)
//   2. Copies TenantId+RefreshToken to VM disk via SSH (line 1582)
//   3. Writes Python script to /tmp/pks-msi-server.py via SSH (line 1642)
//   4. nohup python3 ... & (line 1642)
//   5. Sleep 800ms, returns env string (line 1644)
```

**Proposed** (after refactor):
```csharp
// Takes: (SshTarget? target)
// Returns: (string envArgs, IFoundryMsiTokenServer? tokenServer)
// Does:
//   1. Prompt UseFoundry vs UseDirect
//   2. GetStoredCredentialsAsync() from HOST storage
//   3. new FoundryMsiTokenServer() → StartAsync(creds, bindAddress)
//   4. Return (envArgs, tokenServer) — caller owns lifetime
```

### Code Locations: interactiveSshArgs

In `ClaudeSpawnCommand.OnAfterRemoteSpawnAsync` (lines 74–76):
```csharp
var interactiveSshArgs = $"-o StrictHostKeyChecking=no -p {target.Port}";
if (!string.IsNullOrEmpty(target.KeyPath))
    interactiveSshArgs += $" -i \"{target.KeyPath}\"";
// PROPOSED ADDITION:
if (tokenServer != null)
    interactiveSshArgs += $" -R 0.0.0.0:{tokenServer.Port}:localhost:{tokenServer.Port}";
```

Same pattern in `VibecastCommand.OnAfterRemoteSpawnAsync` (lines 59–61).

### VmInitCommand GatewayPorts Step

After cloud-init poll loop (around line 437), before `AddTargetAsync` (line 440):
```csharp
// Idempotent: only appends if not already present
var sshArgsForInit = $"-o StrictHostKeyChecking=no -o BatchMode=yes -p 22 -i \"{keyPath}\"";
await RunSshCommandAsync(sshArgsForInit, new SshTarget { Host = vmInfo.PublicIpAddress, Username = "azureuser", Port = 22 },
    "grep -q 'GatewayPorts yes' /etc/ssh/sshd_config || " +
    "echo 'GatewayPorts yes' | sudo tee -a /etc/ssh/sshd_config && sudo systemctl restart ssh",
    timeoutSeconds: 15);
```

### DI Registration

In `Program.cs` around line 233 (near the Foundry auth registration):
```csharp
// Existing:
services.AddHttpClient<IAzureFoundryAuthService, AzureFoundryAuthService>();
// New — transient so each session gets its own server instance:
services.AddTransient<IFoundryMsiTokenServer, FoundryMsiTokenServer>();
```

`FoundryMsiTokenServer` needs `IFoundryMsiTokenServer` injected into `DevcontainerSpawnCommand`
(or its subclasses). Since `DevcontainerSpawnCommand` constructors already pass `foundryAuthService`
optionally, add `IFoundryMsiTokenServer? foundryMsiTokenServer` as a similar optional param.

### Scenario Detection Logic

```csharp
// In BuildFoundryEnvArgsAsync (refactored):
bool isRemote = target != null && !string.IsNullOrEmpty(target.Host);
string bindAddress;
string identityEndpointHost;

if (isRemote)
{
    // Scenario A: server binds locally, tunnel delivers to Docker bridge on VM
    bindAddress = "127.0.0.1";
    identityEndpointHost = "172.17.0.1"; // Docker bridge on VM — reached via SSH tunnel
}
else
{
    // Scenario B: server binds to Docker bridge on Host
    if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
    {
        bindAddress = "0.0.0.0";           // Docker Desktop resolves host.docker.internal
        identityEndpointHost = "host.docker.internal";
    }
    else
    {
        bindAddress = "172.17.0.1";        // Linux Docker bridge
        identityEndpointHost = "172.17.0.1";
    }
}
```
