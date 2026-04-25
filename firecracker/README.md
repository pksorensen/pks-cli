# Firecracker MicroVM Runner

A "rent a runner" system for PKS CLI that provisions lightweight Firecracker microVMs as ephemeral CI/CD runners. Each job gets an isolated VM with Docker, SSH, and network access -- spun up in under a second and destroyed after use.

## Prerequisites

- **Linux** host (x86_64 or aarch64)
- **KVM** enabled (`/dev/kvm` must be accessible)
- **Docker** (for building rootfs images)
- **curl**, **iptables**, **iproute2** (typically pre-installed)

## Quick Install

```bash
curl -fsSL https://raw.githubusercontent.com/pksorensen/pks-cli/main/firecracker/install.sh | sudo bash
```

This installs Firecracker + Jailer, enables KVM, configures IP forwarding and NAT, and optionally runs `pks firecracker init` if PKS CLI is on PATH.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `FIRECRACKER_VERSION` | `1.11.0` | Firecracker release version |
| `SKIP_PKS_INIT` | `false` | Skip automatic `pks firecracker init` |

## Manual Installation

```bash
git clone https://github.com/pksorensen/pks-cli.git
cd pks-cli/firecracker
sudo ./install.sh
```

## Building the Rootfs

The rootfs is an ext4 disk image containing Ubuntu 24.04 with Docker, SSH, git, and systemd pre-configured.

```bash
# Basic build
./build-rootfs.sh

# With SSH key and custom size
./build-rootfs.sh --ssh-key ~/.ssh/id_ed25519.pub --output my-rootfs.ext4 --size 8192
```

### Options

| Flag | Default | Description |
|------|---------|-------------|
| `--ssh-key PATH` | none | SSH public key to embed for root access |
| `--output PATH` | `rootfs.ext4` | Output image path |
| `--size SIZE_MB` | `4096` | Image size in megabytes |

## Usage

After installation, use PKS CLI to manage the runner:

```bash
# Initialize Firecracker configuration
pks firecracker init

# Start the runner, connecting to the orchestration server
pks firecracker runner start --server agentics.dk

# Check runner status
pks firecracker runner status
```

## Architecture

### VM Lifecycle

1. The runner process connects to the orchestration server and polls for jobs.
2. When a job arrives, a new Firecracker microVM is launched with a fresh copy of the rootfs.
3. The job executes inside the VM with full Docker support.
4. On completion (or timeout), the VM is destroyed and resources are reclaimed.

### Networking

Each VM gets a TAP device and an IP from the `172.16.0.0/16` subnet. The host provides NAT via iptables masquerading on the default interface, giving VMs outbound internet access.

- **Host gateway**: `172.16.0.1` (per TAP bridge)
- **VM IPs**: Assigned from `172.16.0.2+` per VM instance
- **DNS**: `8.8.8.8` / `8.8.4.4` configured in the rootfs

IP configuration is passed to the VM via kernel boot parameters and applied by `setup-network.sh` on boot.

### Rootfs

The rootfs is built via Docker (`Dockerfile.rootfs`) and exported to an ext4 image. It includes:

- **systemd** as init (PID 1)
- **Docker** daemon (available to the `agent` user)
- **SSH** server for remote access
- **Network setup** service that reads IP config from kernel cmdline
- An `agent` user with passwordless sudo and Docker group membership

### Security

- Each VM runs in its own Firecracker jailer sandbox.
- VMs have no access to the host filesystem.
- The rootfs is a copy-on-write overlay -- changes are discarded on VM teardown.
- The `agent` user has Docker access scoped to the VM only.

## Troubleshooting

### KVM not available

```bash
# Check if your CPU supports virtualization
grep -E 'vmx|svm' /proc/cpuinfo

# Load KVM modules
sudo modprobe kvm
sudo modprobe kvm_intel  # or kvm_amd

# Check permissions
ls -la /dev/kvm
sudo chmod 666 /dev/kvm  # quick fix, or add user to kvm group
```

### TAP device issues

```bash
# Verify IP forwarding
sysctl net.ipv4.ip_forward

# Check NAT rules
sudo iptables -t nat -L POSTROUTING -v

# Manually create a TAP device for testing
sudo ip tuntap add tap0 mode tap
sudo ip addr add 172.16.0.1/24 dev tap0
sudo ip link set tap0 up
```

### VM has no internet

```bash
# Verify NAT is configured on the correct interface
ip route | grep default
sudo iptables -t nat -L POSTROUTING -v

# Re-add NAT rule if missing (replace eth0 with your interface)
sudo iptables -t nat -A POSTROUTING -o eth0 -s 172.16.0.0/16 -j MASQUERADE
```

### Docker not starting inside VM

Docker requires cgroup v2 and sufficient memory. Ensure VM config allocates at least 512MB RAM and the kernel supports cgroups.
