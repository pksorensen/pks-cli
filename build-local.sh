#!/usr/bin/env bash
# Build pks-cli.exe for local testing with all embedded assets:
#   - vibecast linux-amd64 binary + claude-plugin
#   - pks linux-x64 binary (so pks claude can deploy the proxy to the VM without
#     requiring a released version of pks-cli to be installed on the VM)
#   - heypoul windows-amd64 binary (so pks voice works out-of-the-box on Windows)
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

# Resolve heypoul source dir (sibling of pks-cli when used as submodule, or in the monorepo).
HEYPOUL_DIR=""
for candidate in "$SCRIPT_DIR/../../sandbox/heypoul" "$SCRIPT_DIR/../heypoul"; do
    if [ -f "$candidate/main.go" ]; then HEYPOUL_DIR="$(cd "$candidate" && pwd)"; break; fi
done

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

# ── mingw cross-compiler (needed for CGO Windows builds) ──────────────────
# We use llvm-mingw (no sudo required — just a tarball).
LLVM_MINGW_VER="20240619"
LLVM_MINGW_DIR="/tmp/llvm-mingw-${LLVM_MINGW_VER}-ucrt-ubuntu-20.04-x86_64"
LLVM_MINGW_URL="https://github.com/mstorsjo/llvm-mingw/releases/download/${LLVM_MINGW_VER}/llvm-mingw-${LLVM_MINGW_VER}-ucrt-ubuntu-20.04-x86_64.tar.xz"

ensure_mingw() {
    if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
        return 0  # already in PATH
    fi
    if [ -x "$LLVM_MINGW_DIR/bin/x86_64-w64-mingw32-gcc" ]; then
        export PATH="$LLVM_MINGW_DIR/bin:$PATH"
        return 0
    fi
    echo "  Downloading llvm-mingw (cross-compiler for Windows CGO)..."
    curl -sL "$LLVM_MINGW_URL" -o /tmp/llvm-mingw.tar.xz
    tar -xf /tmp/llvm-mingw.tar.xz -C /tmp/
    rm -f /tmp/llvm-mingw.tar.xz
    export PATH="$LLVM_MINGW_DIR/bin:$PATH"
}

echo "[1/5] Building vibecast linux/amd64..."
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 \
    go build -C "$CLI_DIR" -o "$RESOURCE_DIR/vibecast-linux-amd64" .
echo "      -> $RESOURCE_DIR/vibecast-linux-amd64"

echo "[2/5] Copying claude-plugin directory into Resources..."
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
    -p:EmbedHeypoul=false \
    -o "$SCRIPT_DIR/bin/linux-x64-tmp" \
    --nologo -v q
cp "$SCRIPT_DIR/bin/linux-x64-tmp/pks-cli" "$RESOURCE_DIR/pks-linux-x64"
rm -rf "$SCRIPT_DIR/bin/linux-x64-tmp"
echo "      -> $RESOURCE_DIR/pks-linux-x64"

echo "[4/5] Building heypoul windows/amd64..."
if [ -z "$HEYPOUL_DIR" ]; then
    echo "  warning: heypoul source not found — skipping (pks voice will need heypoul.exe in PATH)"
    EMBED_HEYPOUL=false
else
    ensure_mingw
    GOOS=windows GOARCH=amd64 CGO_ENABLED=1 \
        CC=x86_64-w64-mingw32-gcc \
        CXX=x86_64-w64-mingw32-g++ \
        go build -C "$HEYPOUL_DIR" -o "$RESOURCE_DIR/heypoul-win-amd64.exe" . \
        2>&1 | grep -v "warning:\|note:\|miniaudio\|ma_atomic" || true
    echo "      -> $RESOURCE_DIR/heypoul-win-amd64.exe"
    EMBED_HEYPOUL=true
fi

echo "[5/5] Publishing pks-cli.exe with all embedded assets..."
# Clear stale MSBuild cache files that cause spurious errors when switching configs
find "$SCRIPT_DIR/src/obj" -name "*.cache" -delete 2>/dev/null || true

dotnet publish "$SCRIPT_DIR/src/pks-cli.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EmbedVibecast=true \
    -p:EmbedPksLinux=true \
    -p:EmbedHeypoul="${EMBED_HEYPOUL:-false}" \
    -o "$SCRIPT_DIR/bin/win-x64" \
    --nologo

echo ""
echo "Done -> $SCRIPT_DIR/bin/win-x64/pks-cli.exe"
if [ "${EMBED_HEYPOUL:-false}" = "true" ]; then
    echo "       heypoul.exe embedded — pks voice start works on Windows out of the box"
else
    echo "       heypoul NOT embedded — place heypoul.exe in PATH on Windows before running pks voice"
fi
