#!/usr/bin/env bash
set -euo pipefail

# Build Firecracker rootfs image
# Usage: ./build-rootfs.sh [--ssh-key PATH] [--output PATH] [--size SIZE_MB]

SSH_PUB_KEY="${SSH_PUB_KEY:-}"
OUTPUT="${OUTPUT:-rootfs.ext4}"
SIZE_MB="${SIZE_MB:-4096}"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m'

info()  { echo -e "${CYAN}[INFO]${NC} $*"; }
ok()    { echo -e "${GREEN}[OK]${NC} $*"; }
error() { echo -e "${RED}[ERROR]${NC} $*"; exit 1; }

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --ssh-key)   SSH_PUB_KEY=$(cat "$2"); shift 2 ;;
        --output)    OUTPUT="$2"; shift 2 ;;
        --size)      SIZE_MB="$2"; shift 2 ;;
        -h|--help)
            echo "Usage: $0 [--ssh-key PATH] [--output PATH] [--size SIZE_MB]"
            echo ""
            echo "Options:"
            echo "  --ssh-key PATH   Path to SSH public key to embed (default: none)"
            echo "  --output PATH    Output ext4 image path (default: rootfs.ext4)"
            echo "  --size SIZE_MB   Image size in MB (default: 4096)"
            exit 0
            ;;
        *) error "Unknown option: $1" ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONTAINER_NAME="pks-fc-rootfs-tmp"

# Check prerequisites
command -v docker &>/dev/null || error "Docker is required but not installed"

# Step 1: Build Docker image
info "Building rootfs Docker image..."
docker build -t pks-fc-rootfs \
    --build-arg "SSH_PUB_KEY=${SSH_PUB_KEY}" \
    -f "${SCRIPT_DIR}/Dockerfile.rootfs" \
    "${SCRIPT_DIR}"
ok "Docker image built"

# Step 2: Create container
info "Creating temporary container..."
docker rm -f "$CONTAINER_NAME" 2>/dev/null || true
docker create --name "$CONTAINER_NAME" pks-fc-rootfs
ok "Container created"

# Step 3: Create ext4 image
info "Creating ${SIZE_MB}MB ext4 image at ${OUTPUT}..."
dd if=/dev/zero of="$OUTPUT" bs=1M count=0 seek="$SIZE_MB" 2>/dev/null
mkfs.ext4 -F "$OUTPUT" > /dev/null 2>&1
ok "ext4 image created"

# Step 4: Mount and populate
info "Populating rootfs from container..."
MOUNT_DIR=$(mktemp -d)
trap 'sudo umount "$MOUNT_DIR" 2>/dev/null; rmdir "$MOUNT_DIR" 2>/dev/null; docker rm -f "$CONTAINER_NAME" 2>/dev/null' EXIT

sudo mount -o loop "$OUTPUT" "$MOUNT_DIR"
docker export "$CONTAINER_NAME" | sudo tar -x -C "$MOUNT_DIR"
sudo umount "$MOUNT_DIR"
ok "Rootfs populated"

# Cleanup
rmdir "$MOUNT_DIR" 2>/dev/null || true
docker rm -f "$CONTAINER_NAME" > /dev/null 2>&1

echo ""
ok "Rootfs image ready: ${OUTPUT} (${SIZE_MB}MB)"
echo "  Use with: pks firecracker runner start"
