#!/usr/bin/env bash
# Build pks-cli.exe for local testing with all embedded assets:
#   - vibecast linux-amd64 binary + claude-plugin
#   - pks linux-x64 binary (so pks claude can deploy the proxy to the VM without
#     requiring a released version of pks-cli to be installed on the VM)
# Usage: ./build-local.sh
# Output: bin/win-x64/pks-cli.exe
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# CLI_DIR points at the vibecast Go source. Override with CLI_DIR=... when the
# checkout layout differs (e.g. pks-cli used as a submodule alongside vibecast).
if [ -z "${CLI_DIR:-}" ]; then
    for candidate in "$SCRIPT_DIR/../../src/apps/cli" "$SCRIPT_DIR/../vibecast"; do
        if [ -d "$candidate" ]; then CLI_DIR="$(cd "$candidate" && pwd)"; break; fi
    done
fi
if [ -z "${CLI_DIR:-}" ] || [ ! -d "$CLI_DIR" ]; then
    echo "error: vibecast source dir not found. Set CLI_DIR=/abs/path/to/vibecast." >&2
    exit 1
fi
RESOURCE_DIR="$SCRIPT_DIR/src/Infrastructure/Resources"
PLUGIN_SRC="$CLI_DIR/claude-plugin"

# Ensure `go` is on PATH; some devcontainers install it under $HOME/go/bin.
if ! command -v go >/dev/null 2>&1; then
    for candidate in "$HOME/go/bin" "/usr/local/go/bin"; do
        if [ -x "$candidate/go" ]; then PATH="$candidate:$PATH"; break; fi
    done
fi
if ! command -v go >/dev/null 2>&1; then
    echo "error: 'go' not found on PATH. Install Go 1.24+ (see CLAUDE.md)." >&2
    exit 1
fi

echo "[1/4] Building vibecast linux/amd64..."
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 \
    go build -C "$CLI_DIR" -o "$RESOURCE_DIR/vibecast-linux-amd64" .
echo "      -> $RESOURCE_DIR/vibecast-linux-amd64"

echo "[2/4] Copying claude-plugin directory into Resources..."
rm -rf "$RESOURCE_DIR/claude-plugin"
mkdir -p "$RESOURCE_DIR/claude-plugin/.claude-plugin" \
         "$RESOURCE_DIR/claude-plugin/hooks"
cp "$PLUGIN_SRC/.claude-plugin/plugin.json" "$RESOURCE_DIR/claude-plugin/.claude-plugin/plugin.json"
cp "$PLUGIN_SRC/.mcp.json"                  "$RESOURCE_DIR/claude-plugin/.mcp.json"
cp "$PLUGIN_SRC/hooks/hooks.json"           "$RESOURCE_DIR/claude-plugin/hooks/hooks.json"
echo "      -> $RESOURCE_DIR/claude-plugin/"

echo "[3/5] Publishing pks linux-x64 binary for VM deployment..."
dotnet publish "$SCRIPT_DIR/src/pks-cli.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EmbedVibecast=false \
    -p:EmbedPksLinux=false \
    -o "$SCRIPT_DIR/bin/linux-x64-tmp" \
    --nologo -v q
cp "$SCRIPT_DIR/bin/linux-x64-tmp/pks-cli" "$RESOURCE_DIR/pks-linux-x64"
rm -rf "$SCRIPT_DIR/bin/linux-x64-tmp"
echo "      -> $RESOURCE_DIR/pks-linux-x64"

echo "[4/5] Publishing pks-cli.exe with embedded vibecast + pks linux binary..."
# Clear stale MSBuild cache files that cause spurious errors when switching configs
find "$SCRIPT_DIR/src/obj" -name "*.cache" -delete 2>/dev/null || true

dotnet publish "$SCRIPT_DIR/src/pks-cli.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EmbedVibecast=true \
    -p:EmbedPksLinux=true \
    -o "$SCRIPT_DIR/bin/win-x64" \
    --nologo

echo "[5/5] Done -> $SCRIPT_DIR/bin/win-x64/pks-cli.exe"
