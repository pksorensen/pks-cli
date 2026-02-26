# Devcontainer-Based GitHub Actions Runner

Run GitHub Actions workflows inside devcontainers on your own hardware. The host stays trusted — the runner binary executes inside the container with no access to host Docker.

## How It Works

```
Your Host (e.g. Hetzner VPS)
  |
  pks github runner start
  |
  +-- Polls GitHub API every 30s for queued jobs
  |
  +-- Job queued for "devcontainer-runner" label:
  |     1. git clone --depth=1 the repo
  |     2. devcontainer up (builds container from .devcontainer/)
  |     3. Installs GitHub Actions runner binary inside the container
  |     4. Gets a one-time JIT token from GitHub API
  |     5. Starts the runner — it picks up the job and runs it
  |     6. Runner exits -> container destroyed -> clone removed
  |
  +-- Repeat
```

The key insight: your workflow steps run inside the devcontainer, so the CI environment matches what developers use locally. Docker-in-Docker works because the devcontainer controls its own Docker daemon.

## Prerequisites

On the host machine:

- Docker (system Docker, running as root — this is fine because only PKS CLI talks to it)
- [devcontainer CLI](https://github.com/devcontainers/cli) (`npm install -g @devcontainers/cli`)
- PKS CLI (`dotnet tool install -g pks-cli`)
- The [Agentics Live GitHub App](https://github.com/organizations/si14agents/settings/apps/agentics-live) must be installed on the target repo (it gets installed automatically during the auth flow)

## GitHub App

PKS CLI uses the **Agentics Live** GitHub App for authentication:

| | |
|---|---|
| **App** | [Agentics Live](https://github.com/organizations/si14agents/settings/apps/agentics-live) |
| **Organization** | [si14agents](https://github.com/si14agents) |
| **Client ID** | `Iv23liFv43zosMUb8t9y` |
| **Auth flow** | Device code (no browser callback needed) |

**Permissions requested:**
- Repository → Administration: Read & write (for JIT runner tokens)
- Repository → Actions: Read-only (to poll for queued runs)
- Repository → Contents: Read-only (to clone repos)

## Quick Start

### 1. Authenticate

```bash
pks github runner register --repo owner/repo
```

Authentication happens automatically when you register. It uses the GitHub device code flow — you'll see a URL and code to enter in your browser. No separate auth command needed.

### 2. Register Repositories

Register each repo you want this host to serve. Call `register` once per repo:

```bash
pks github runner register --repo myorg/my-project
pks github runner register --repo myorg/another-project
```

Each call verifies you have **admin access** on that repo (required by GitHub's JIT runner token API) and adds it to `~/.pks-cli/runners.json`. The daemon will watch all registered repos.

### 3. Start the Runner Daemon

```bash
pks github runner start
```

The daemon starts polling for queued workflow runs. It shows a live status table:

```
 Runner Daemon
 Status: Running | Polling: 30s | Jobs: 0 active

 Registrations:
 | Repository          | Labels              | Last Poll            |
 |---------------------|---------------------|----------------------|
 | myorg/my-project    | devcontainer-runner | 2026-02-25 10:30:15  |
```

Press `Ctrl+C` for graceful shutdown (finishes active jobs first).

### 4. Configure Your Workflow

In your repo, create a workflow that targets the `devcontainer-runner` label:

```yaml
# .github/workflows/ci.yml
name: CI

on:
  push:
    branches: [main]
  pull_request:

jobs:
  build-and-test:
    runs-on: [self-hosted, devcontainer-runner]
    steps:
      - uses: actions/checkout@v4

      - name: Build
        run: dotnet build

      - name: Test
        run: dotnet test
```

That's it. When this workflow is triggered, the daemon detects the queued run, spins up the devcontainer, and the steps execute inside it.

### 5. Push and Watch

```bash
git push origin main
```

The daemon output shows the job lifecycle:

```
 [10:31:02] Job detected: CI #42 on myorg/my-project (main)
 [10:31:03] Cloning myorg/my-project@main...
 [10:31:05] Building devcontainer...
 [10:31:45] Runner started inside container abc123
 [10:33:12] Job completed successfully
 [10:33:13] Cleaned up container and clone directory
```

## Managing Runners

```bash
# List registered repos
pks github runner list

# Check daemon status and active jobs
pks github runner status

# Stop accepting new jobs (finishes current ones)
pks github runner stop

# Remove a registration
pks github runner unregister --repo myorg/my-project
```

## Custom Labels

Register with custom labels to route specific workflows:

```bash
pks github runner register --repo myorg/my-project --labels "devcontainer-runner,gpu"
```

Then in your workflow:

```yaml
jobs:
  train:
    runs-on: [self-hosted, devcontainer-runner, gpu]
```

## Why This Approach?

| Approach | Problem |
|----------|---------|
| Add runner to Docker group | Runner can see/modify all host containers |
| Docker socket proxy | Runner can still stop/remove host containers |
| Rootless Docker | Docker-in-Docker fails (cgroup permission denied) |
| **Devcontainer runner** | **Runner is isolated inside the container** |

The host only runs trusted code (PKS CLI). The runner and all workflow steps execute inside the devcontainer with no access to host Docker.

## Example: .NET Aspire with Keycloak

A real-world scenario — running Aspire integration tests that need Docker-in-Docker for Keycloak:

```
.devcontainer/
  devcontainer.json    # Includes docker-in-docker feature
```

```json
{
  "image": "mcr.microsoft.com/devcontainers/dotnet:1-9.0",
  "features": {
    "ghcr.io/devcontainers/features/docker-in-docker:2": {}
  }
}
```

```yaml
# .github/workflows/integration.yml
jobs:
  aspire-tests:
    runs-on: [self-hosted, devcontainer-runner]
    steps:
      - uses: actions/checkout@v4
      - run: dotnet test tests/Integration
        # Aspire starts Keycloak via DinD inside the devcontainer
```

This works because the devcontainer has its own Docker daemon (docker-in-docker feature), completely independent of the host.

## Configuration

The runner configuration is stored at `~/.pks-cli/runners.json`:

```json
{
  "registrations": [
    {
      "id": "a1b2c3d4",
      "owner": "myorg",
      "repository": "my-project",
      "labels": "devcontainer-runner",
      "registeredAt": "2026-02-25T10:00:00Z",
      "enabled": true
    }
  ],
  "pollingIntervalSeconds": 30,
  "maxConcurrentJobs": 1
}
```

## Troubleshooting

### Runner doesn't pick up jobs

- Check the workflow uses `runs-on: [self-hosted, devcontainer-runner]`
- Verify registration: `pks github runner list`
- Check auth is valid: `pks github auth` (re-auth if expired)
- Ensure the GitHub App has `Administration: Read & Write` permission

### devcontainer build fails

- Test locally first: `devcontainer up --workspace-folder .`
- Check the repo has a `.devcontainer/devcontainer.json`

### Job stuck or orphaned container

```bash
# Check for leftover containers
docker ps -a | grep pks-runner

# Manual cleanup
docker rm -f <container-id>
```
