# Self-Hosted Runner Setup for Devcontainer CI

This guide covers setting up a self-hosted GitHub Actions runner to use the `devcontainer-run` composite action (`.github/actions/devcontainer-run/`).

## Problem

The official `devcontainers/ci` action and a naive `npm install -g @devcontainers/cli` have two issues on self-hosted runners:

1. **Permission error** — `npm install -g` fails because the runner user can't write to the global npm prefix
2. **PATH error** — Even after install, the `devcontainer` binary isn't found (`ENOENT`)

The `devcontainer-run` action solves issue 1 and 2 by installing to a user-local npm prefix. However, the runner user also needs Docker access, which introduces a security concern.

## Why Not Just Add the Runner to the Docker Group?

Membership in the `docker` group is effectively **root access** to all containers on the host. A CI workflow could:

- Stop or remove any container on the host
- Mount host filesystems
- Access Docker secrets

This is unacceptable when the host runs other workloads.

## Solution: Rootless Docker

Rootless Docker runs a **separate Docker daemon in userspace** under the runner account. It is fully isolated from the system Docker:

| | System Docker | Rootless Docker |
|---|---|---|
| Socket | `/var/run/docker.sock` | `~/.docker/run/docker.sock` |
| Storage | `/var/lib/docker` | `~/.local/share/docker` |
| Process | system `dockerd` (root) | user `dockerd` (runner user) |
| Containers visible | all | only runner's own |

Installing rootless Docker does **not** affect the system Docker daemon or any running containers.

## Setup Steps

All commands below assume the runner user is called `github-runner`. Adjust as needed.

### 1. Install Prerequisites

```bash
apt-get install -y uidmap dbus-user-session
```

### 2. Install Rootless Docker

```bash
su - github-runner -c 'dockerd-rootless-setuptool.sh install'
```

### 3. Start the Daemon

On hosts without systemd, the daemon must be started manually:

```bash
su - github-runner -c '
  export XDG_RUNTIME_DIR=$HOME/.docker/run
  mkdir -p "$XDG_RUNTIME_DIR"
  export PATH=/usr/bin:/sbin:/usr/sbin:$PATH
  nohup dockerd-rootless.sh > ~/.docker/dockerd.log 2>&1 &
  sleep 3
  export DOCKER_HOST=unix://$HOME/.docker/run/docker.sock
  docker ps
'
```

You should see an empty container list (this is the isolated rootless daemon, not the host).

On hosts **with systemd**, the setup tool configures a user service automatically:

```bash
su - github-runner -c 'systemctl --user start docker'
su - github-runner -c 'systemctl --user enable docker'
```

### 4. Persist Environment Variables

Add to the runner user's `~/.bashrc` so the GitHub Actions runner process inherits them:

```bash
su - github-runner -c '
  cat >> ~/.bashrc << '\''VARS'\''
export XDG_RUNTIME_DIR=$HOME/.docker/run
export PATH=/usr/bin:/sbin:/usr/sbin:$PATH
export DOCKER_HOST=unix://$HOME/.docker/run/docker.sock
VARS
'
```

### 5. Ensure Daemon Starts on Boot

Without systemd, add a startup script or cron job:

```bash
# Example: crontab entry for github-runner
crontab -u github-runner -e
```

Add:

```
@reboot XDG_RUNTIME_DIR=$HOME/.docker/run PATH=/usr/bin:/sbin:/usr/sbin:$PATH nohup dockerd-rootless.sh > $HOME/.docker/dockerd.log 2>&1 &
```

### 6. Verify

```bash
su - github-runner -c 'docker ps'
su - github-runner -c 'docker run --rm hello-world'
```

## Using the Action

Once the runner is set up, workflows can use the devcontainer-run action:

```yaml
jobs:
  test:
    runs-on: [self-hosted, linux]
    steps:
      - uses: actions/checkout@v4
      - uses: pksorensen/pks-cli/.github/actions/devcontainer-run@vnext
        with:
          command: dotnet test
```

The action handles devcontainer CLI installation automatically using a user-local npm prefix.

## Troubleshooting

### `Cannot connect to the Docker daemon`

The rootless daemon isn't running. Start it:

```bash
su - github-runner -c '
  export XDG_RUNTIME_DIR=$HOME/.docker/run
  nohup dockerd-rootless.sh > ~/.docker/dockerd.log 2>&1 &
'
```

Check logs at `~/.docker/dockerd.log`.

### `docker ps` shows host containers

The runner is using the system Docker socket instead of rootless. Verify `DOCKER_HOST` is set:

```bash
su - github-runner -c 'echo $DOCKER_HOST'
# Should be: unix:///home/github-runner/.docker/run/docker.sock
```

### devcontainer build fails with storage errors

Rootless Docker storage is at `~/.local/share/docker`. Ensure the runner user has sufficient disk space and permissions on that path.

### Slow image pulls

Rootless Docker maintains its own image cache, separate from the system Docker. The first pull will be slow. Consider pre-pulling base images after setup:

```bash
su - github-runner -c 'docker pull mcr.microsoft.com/devcontainers/dotnet:1-9.0'
```
