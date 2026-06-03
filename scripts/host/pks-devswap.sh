#!/usr/bin/env bash
# pks-devswap.sh — replace the baked pks binary inside a running devcontainer, from the HOST.
#
# This is the secure alternative to `dotnet tool install -g pks-cli` (which shadows the baked
# binary with a node-owned copy that runs as `node`, leaking pks credentials into the agent's
# reach). Here the swapped binary stays root-owned and keeps running as the isolated `pks` user.
#
# Run this on the Docker host (NOT inside the container — the in-container agent has no path to
# the host's docker daemon, which is exactly why this is the trusted update channel):
#
#   scripts/host/pks-devswap.sh workspace <container>   # build linux-x64 from THIS workspace (dev loop)
#   scripts/host/pks-devswap.sh release   <container>   # install the latest released linux-x64 (npm)
#
# Prerequisites: docker; for `workspace` mode the .NET SDK; for `release` mode node/npm.
set -euo pipefail

MODE="${1:-}"
CTR="${2:-}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PKS_SRC="$(cd "$SCRIPT_DIR/../../src" && pwd)"

[ -n "$MODE" ] && [ -n "$CTR" ] || { echo "usage: $0 <workspace|release> <container>" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/pks"

case "$MODE" in
  workspace)
    command -v dotnet >/dev/null || { echo "error: dotnet SDK not found on host" >&2; exit 1; }
    echo "==> Building pks linux-x64 (self-contained) from $PKS_SRC"
    dotnet publish "$PKS_SRC/pks-cli.csproj" -c Release -r linux-x64 \
      -p:PublishSelfContained=true -p:PublishSingleFile=true -p:EmbedTemplates=true \
      -o "$WORK/publish" --nologo -v q
    cp "$WORK/publish/pks-cli" "$STAGE"
    ;;
  release)
    command -v npm >/dev/null || { echo "error: npm not found on host" >&2; exit 1; }
    echo "==> Fetching latest @pks-cli/cli-linux-x64 from npm"
    ( cd "$WORK" && npm pack @pks-cli/cli-linux-x64 --silent >/dev/null && tar -xzf ./*.tgz )
    cp "$WORK/package/bin/pks" "$STAGE"
    ;;
  *)
    echo "error: unknown mode '$MODE' (use 'workspace' or 'release')" >&2; exit 1;;
esac

chmod +x "$STAGE"
SHA="$(sha256sum "$STAGE" | awk '{print $1}')"
echo "==> Staged binary sha256: $SHA"

echo "==> Copying into container $CTR"
docker cp "$STAGE" "$CTR:/tmp/pks-staged"

echo "==> Applying as root inside $CTR"
if docker exec -u root "$CTR" test -x /usr/local/bin/pks-apply-update; then
  docker exec -u root "$CTR" /usr/local/bin/pks-apply-update /tmp/pks-staged "$SHA"
else
  echo "    (pks-apply-update not present — image predates it; doing a plain install)"
  docker exec -u root "$CTR" install -m 0755 -o root -g root /tmp/pks-staged /usr/local/bin/pks
fi
docker exec -u root "$CTR" rm -f /tmp/pks-staged || true

echo "==> Done. Verify: docker exec -u pks $CTR pks --version"
