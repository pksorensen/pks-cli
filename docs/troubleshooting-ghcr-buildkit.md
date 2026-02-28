# Troubleshooting: ghcr.io 403 Forbidden in Docker BuildKit

## Symptom

When opening a devcontainer in VS Code (or running `devcontainer up`), the build fails with:

```
ERROR: failed to solve: ghcr.io/pksorensen/pks-fullstack-base:vnext:
failed to resolve source metadata for ghcr.io/pksorensen/pks-fullstack-base:vnext:
failed to authorize: failed to fetch oauth token: unexpected status from GET request to
https://ghcr.io/token?scope=...: 403 Forbidden
```

The image is **public** and `docker pull` works fine — only `docker buildx build` fails.

## Cause

Docker BuildKit has its own credential/auth handling separate from the Docker CLI. Stale or misconfigured credentials for ghcr.io in BuildKit's state cause it to send bad auth tokens, which ghcr.io rejects with 403 even for public images.

## Quick Fix

Pull the image manually, then retry the devcontainer build:

```bash
docker pull ghcr.io/pksorensen/pks-fullstack-base:vnext
```

## Permanent Fix

Reset BuildKit state to clear any stale auth:

```bash
# Remove stale ghcr.io credentials
docker logout ghcr.io

# Reset the buildx builder
docker buildx rm default 2>/dev/null
docker buildx create --use --name default
```

If that doesn't work, nuclear option:

```bash
rm -rf ~/.docker/buildx
docker buildx create --use
```

## Verify

Check `~/.docker/config.json` — if `ghcr.io` appears under `"auths"`, remove that entry. BuildKit uses this file and stale entries cause the 403.
