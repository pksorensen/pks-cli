namespace PKS.Infrastructure.Services.Firecracker;

using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;

/// <summary>
/// Manages the full lifecycle of Firecracker microVMs: prerequisite checks,
/// rootfs image preparation, VM boot/stop via the Firecracker REST API over
/// Unix sockets, and command execution inside VMs via SSH.
/// </summary>
public interface IFirecrackerService
{
    /// <summary>
    /// Checks whether KVM is available and Firecracker is installed,
    /// returning the Firecracker version string when present.
    /// </summary>
    Task<(bool KvmAvailable, bool FirecrackerInstalled, string FirecrackerVersion)> CheckPrerequisitesAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads a compatible guest kernel to the specified directory
    /// and returns the full path to the kernel image.
    /// </summary>
    Task<string> DownloadKernelAsync(string targetDir, CancellationToken ct = default);

    /// <summary>
    /// Builds an ext4 rootfs image from a Dockerfile, injecting the given SSH
    /// public key for later access. Returns the path to the created image.
    /// </summary>
    Task<string> BuildRootfsAsync(string dockerfilePath, string outputPath, string sshPubKeyPath, CancellationToken ct = default);

    /// <summary>
    /// Creates a copy-on-write copy of a base rootfs image for a specific VM,
    /// returning the path to the per-VM rootfs.
    /// </summary>
    Task<string> PrepareRootfsAsync(string baseRootfsPath, string vmId, string workDir, CancellationToken ct = default);

    /// <summary>
    /// Removes all on-disk artifacts for the specified VM (rootfs copy, socket, etc.).
    /// </summary>
    Task CleanupVmAsync(string vmId, string workDir, CancellationToken ct = default);

    /// <summary>
    /// Boots a Firecracker microVM with the given configuration and returns
    /// its runtime state (PID, IP address, TAP device, etc.).
    /// </summary>
    Task<FirecrackerVmState> BootVmAsync(FirecrackerVmConfig config, CancellationToken ct = default);

    /// <summary>
    /// Gracefully stops a running Firecracker VM and cleans up its socket file.
    /// </summary>
    Task StopVmAsync(string vmId, string workDir, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the Firecracker process for the given VM is still alive.
    /// </summary>
    Task<bool> IsVmRunningAsync(string vmId, string workDir, CancellationToken ct = default);

    /// <summary>
    /// Executes a shell command inside the VM over SSH and returns the result.
    /// </summary>
    Task<ProcessResult> ExecuteInVmAsync(string vmIpAddress, string sshKeyPath, string command, int timeoutSeconds = 300, CancellationToken ct = default);
}
