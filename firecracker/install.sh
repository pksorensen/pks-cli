#!/usr/bin/env bash
set -euo pipefail

# Firecracker MicroVM Runner - Install Script
# Usage: curl -fsSL https://raw.githubusercontent.com/pksorensen/pks-cli/main/firecracker/install.sh | sudo bash

FC_VERSION="${FIRECRACKER_VERSION:-1.15.1}"
SKIP_PKS_INIT="${SKIP_PKS_INIT:-false}"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC} $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

# Step 1: Platform detection
info "Detecting platform..."
OS=$(uname -s)
ARCH=$(uname -m)
[[ "$OS" != "Linux" ]] && error "Firecracker only runs on Linux. Detected: $OS"
case "$ARCH" in
    x86_64)  FC_ARCH="x86_64" ;;
    aarch64) FC_ARCH="aarch64" ;;
    *)       error "Unsupported architecture: $ARCH. Firecracker requires x86_64 or aarch64." ;;
esac
ok "Platform: Linux $FC_ARCH"

# Step 2: Check root
[[ $EUID -ne 0 ]] && error "This script must be run as root (or via sudo)"

# Step 3: Check/enable KVM
info "Checking KVM support..."
if ! test -e /dev/kvm; then
    warn "/dev/kvm not found, attempting to load KVM modules..."
    modprobe kvm 2>/dev/null || true
    modprobe kvm_intel 2>/dev/null || modprobe kvm_amd 2>/dev/null || true
    sleep 1
fi
if test -w /dev/kvm; then
    ok "KVM is available and writable"
else
    error "KVM is not available. Ensure your CPU supports virtualization and it's enabled in BIOS. Check: ls -la /dev/kvm"
fi

# Step 4: Install/update Firecracker
info "Installing Firecracker v${FC_VERSION}..."
DOWNLOAD_URL="https://github.com/firecracker-microvm/firecracker/releases/download/v${FC_VERSION}/firecracker-v${FC_VERSION}-${FC_ARCH}.tgz"
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

curl -fsSL "$DOWNLOAD_URL" -o "$TMP_DIR/firecracker.tgz" || error "Failed to download Firecracker v${FC_VERSION}"
tar -xzf "$TMP_DIR/firecracker.tgz" -C "$TMP_DIR"

# Find and install binaries (exclude .debug, .json, .yaml files)
FC_BIN=$(find "$TMP_DIR" -name "firecracker-v*-${FC_ARCH}" -type f ! -name "*.debug" | head -1)
JAILER_BIN=$(find "$TMP_DIR" -name "jailer-v*-${FC_ARCH}" -type f ! -name "*.debug" | head -1)

[[ -z "$FC_BIN" ]] && error "Could not find firecracker binary in release archive"
[[ -z "$JAILER_BIN" ]] && error "Could not find jailer binary in release archive"

cp "$FC_BIN" /usr/local/bin/firecracker
cp "$JAILER_BIN" /usr/local/bin/jailer
chmod +x /usr/local/bin/firecracker /usr/local/bin/jailer

FC_INSTALLED_VER=$(firecracker --version 2>&1 | head -1)
ok "Installed: $FC_INSTALLED_VER"

# Step 5: System configuration
info "Configuring system..."

# IP forwarding
if ! sysctl -n net.ipv4.ip_forward | grep -q 1; then
    sysctl -w net.ipv4.ip_forward=1 > /dev/null
    echo "net.ipv4.ip_forward=1" > /etc/sysctl.d/99-firecracker.conf
    ok "Enabled IP forwarding (persistent)"
else
    ok "IP forwarding already enabled"
fi

# NAT/masquerade for VM subnet
MAIN_IF=$(ip route | grep default | awk '{print $5}' | head -1)
if [[ -n "$MAIN_IF" ]]; then
    # Check if rule already exists
    if ! iptables -t nat -C POSTROUTING -o "$MAIN_IF" -s 172.16.0.0/16 -j MASQUERADE 2>/dev/null; then
        iptables -t nat -A POSTROUTING -o "$MAIN_IF" -s 172.16.0.0/16 -j MASQUERADE
        iptables -A FORWARD -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
        ok "NAT configured for 172.16.0.0/16 via $MAIN_IF"
    else
        ok "NAT rules already configured"
    fi
else
    warn "Could not detect default network interface. You may need to configure NAT manually."
fi

# Step 6: Optionally run pks firecracker init
if [[ "$SKIP_PKS_INIT" != "true" ]] && command -v pks &>/dev/null; then
    info "PKS CLI detected. Running 'pks firecracker init'..."
    pks firecracker init || warn "pks firecracker init failed. You can run it manually later."
else
    if [[ "$SKIP_PKS_INIT" == "true" ]]; then
        info "Skipping pks firecracker init (SKIP_PKS_INIT=true)"
    else
        info "PKS CLI not found on PATH. Install it first, then run: pks firecracker init"
    fi
fi

# Summary
echo ""
echo -e "${GREEN}============================================${NC}"
echo -e "${GREEN}  Firecracker Installation Complete!${NC}"
echo -e "${GREEN}============================================${NC}"
echo ""
echo "  Firecracker: $(firecracker --version 2>&1 | head -1)"
echo "  Jailer:      $(jailer --version 2>&1 | head -1)"
echo "  KVM:         $(test -w /dev/kvm && echo 'Available' || echo 'Not available')"
echo "  IP Forward:  $(sysctl -n net.ipv4.ip_forward)"
echo ""
echo "  Next steps:"
echo "    1. Install PKS CLI if not already: dotnet tool install -g pks-cli"
echo "    2. Initialize Firecracker runner:  pks firecracker init"
echo "    3. Start the runner:               pks firecracker runner start --server agentics.dk"
echo ""
