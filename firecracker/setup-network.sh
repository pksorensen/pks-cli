#!/usr/bin/env bash
# Network setup script for Firecracker VMs
# This script runs on boot and configures networking based on kernel boot parameters.
# The kernel boot args pass IP config in the format: ip=<vm-ip>::<gateway>:<mask>::eth0:off

set -euo pipefail

# Parse IP configuration from /proc/cmdline
# Format: ip=<client-ip>:<server-ip>:<gw-ip>:<netmask>:<hostname>:<device>:<autoconf>
CMDLINE=$(cat /proc/cmdline)

if [[ "$CMDLINE" =~ ip=([0-9.]+)::([0-9.]+):([0-9.]+) ]]; then
    VM_IP="${BASH_REMATCH[1]}"
    GATEWAY="${BASH_REMATCH[2]}"
    NETMASK="${BASH_REMATCH[3]}"

    # Configure eth0
    ip addr add "${VM_IP}/${NETMASK}" dev eth0 2>/dev/null || true
    ip link set eth0 up
    ip route add default via "${GATEWAY}" 2>/dev/null || true

    echo "Network configured: ${VM_IP} via ${GATEWAY}"
else
    echo "WARNING: Could not parse IP configuration from kernel cmdline"
    echo "cmdline: ${CMDLINE}"
fi

# Ensure DNS is configured
if [[ ! -s /etc/resolv.conf ]] || ! grep -q nameserver /etc/resolv.conf; then
    echo "nameserver 8.8.8.8" > /etc/resolv.conf
    echo "nameserver 8.8.4.4" >> /etc/resolv.conf
fi
