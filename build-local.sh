#!/usr/bin/env bash
# Build pks-cli.exe with embedded vibecast linux-amd64 binary and claude-plugin
# directory for local testing.
# Usage: ./build-local.sh
# Output: bin/win-x64/pks-cli.exe
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CLI_DIR="$(cd "$SCRIPT_DIR/../../src/apps/cli" && pwd)"
RESOURCE_DIR="$SCRIPT_DIR/src/Infrastructure/Resources"
PLUGIN_SRC="$CLI_DIR/claude-plugin"

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

echo "[3/4] Publishing pks-cli.exe with embedded vibecast..."
# Clear stale MSBuild cache files that cause spurious errors when switching configs
find "$SCRIPT_DIR/src/obj" -name "*.cache" -delete 2>/dev/null || true

dotnet publish "$SCRIPT_DIR/src/pks-cli.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EmbedVibecast=true \
    -o "$SCRIPT_DIR/bin/win-x64" \
    --nologo

echo "[4/4] Done -> $SCRIPT_DIR/bin/win-x64/pks-cli.exe"
