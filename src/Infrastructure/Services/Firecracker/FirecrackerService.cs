using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Runner;

namespace PKS.Infrastructure.Services.Firecracker;

/// <summary>
/// Manages the full lifecycle of Firecracker microVMs including prerequisite
/// validation, rootfs preparation, VM boot via the Firecracker REST API over
/// Unix domain sockets, networking, and in-VM command execution via SSH.
/// </summary>
public class FirecrackerService : IFirecrackerService, IDisposable
{
    private const string KernelUrl =
        "https://s3.amazonaws.com/spec.ccfc.min/ci-artifacts/kernels/x86_64/vmlinux-5.10.217";

    private const string KernelFileName = "vmlinux";
    private const string RootfsFileName = "rootfs.ext4";
    private const string SocketFileName = "firecracker.sock";
    private const string PidFileName = "firecracker.pid";
    private const string DockerImageName = "pks-fc-rootfs";
    private const string DockerContainerName = "pks-fc-rootfs-tmp";
    private const string MountPoint = "/tmp/pks-fc-mount";
    private const int RootfsSizeMib = 4096;
    private const int SocketPollMaxAttempts = 30;
    private const int SocketPollDelayMs = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    private readonly IProcessRunner _processRunner;
    private readonly ILogger<FirecrackerService> _logger;
    private readonly ConcurrentDictionary<string, Process> _activeVms = new();

    public FirecrackerService(IProcessRunner processRunner, ILogger<FirecrackerService> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Prerequisites
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<(bool KvmAvailable, bool FirecrackerInstalled, string FirecrackerVersion)> CheckPrerequisitesAsync(
        CancellationToken ct = default)
    {
        _logger.LogDebug("Checking Firecracker prerequisites");

        var kvmTask = _processRunner.RunAsync("test", "-w /dev/kvm", cancellationToken: ct);
        var fcTask = _processRunner.RunAsync("firecracker", "--version", cancellationToken: ct);

        await Task.WhenAll(kvmTask, fcTask);

        var kvmResult = await kvmTask;
        var fcResult = await fcTask;

        var kvmAvailable = kvmResult.ExitCode == 0;
        var fcInstalled = fcResult.ExitCode == 0;
        var fcVersion = fcInstalled ? fcResult.StandardOutput.Trim() : string.Empty;

        _logger.LogInformation(
            "Prerequisites: KVM={KvmAvailable}, Firecracker={FcInstalled}, Version={FcVersion}",
            kvmAvailable, fcInstalled, fcVersion);

        return (kvmAvailable, fcInstalled, fcVersion);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Kernel
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> DownloadKernelAsync(string targetDir, CancellationToken ct = default)
    {
        var kernelPath = Path.Combine(targetDir, KernelFileName);

        _logger.LogInformation("Downloading guest kernel to {KernelPath}", kernelPath);

        await _processRunner.RunAsync("mkdir", $"-p {targetDir}", cancellationToken: ct);

        var result = await _processRunner.RunAsync(
            "curl", $"-L -o {kernelPath} {KernelUrl}", cancellationToken: ct);

        if (result.ExitCode != 0)
        {
            _logger.LogError("Kernel download failed: {Stderr}", result.StandardError);
            throw new InvalidOperationException($"Failed to download kernel: {result.StandardError}");
        }

        _logger.LogInformation("Kernel downloaded to {KernelPath}", kernelPath);
        return kernelPath;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Rootfs
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> BuildRootfsAsync(
        string dockerfilePath, string outputPath, string sshPubKeyPath, CancellationToken ct = default)
    {
        var contextDir = Path.GetDirectoryName(dockerfilePath)
                         ?? throw new ArgumentException("Invalid Dockerfile path", nameof(dockerfilePath));

        _logger.LogInformation("Building rootfs from {Dockerfile} -> {OutputPath}", dockerfilePath, outputPath);

        // 1. Build Docker image
        var buildResult = await _processRunner.RunAsync(
            "bash",
            $"-c \"docker build -t {DockerImageName} -f {dockerfilePath} " +
            $"--build-arg SSH_PUB_KEY=\\\"$(cat {sshPubKeyPath})\\\" {contextDir}\"",
            cancellationToken: ct);
        ThrowOnFailure(buildResult, "Docker build");

        // 2. Create a temporary container from the image
        var createResult = await _processRunner.RunAsync(
            "docker", $"create --name {DockerContainerName} {DockerImageName}",
            cancellationToken: ct);
        ThrowOnFailure(createResult, "Docker create");

        try
        {
            // 3. Create a sparse ext4 image
            var ddResult = await _processRunner.RunAsync(
                "dd", $"if=/dev/zero of={outputPath} bs=1M count=0 seek={RootfsSizeMib}",
                cancellationToken: ct);
            ThrowOnFailure(ddResult, "dd (create sparse image)");

            // 4. Format as ext4
            var mkfsResult = await _processRunner.RunAsync(
                "mkfs.ext4", outputPath, cancellationToken: ct);
            ThrowOnFailure(mkfsResult, "mkfs.ext4");

            // 5. Mount, export container filesystem, extract into image
            await _processRunner.RunAsync("mkdir", $"-p {MountPoint}", cancellationToken: ct);

            var mountResult = await _processRunner.RunAsync(
                "mount", $"-o loop {outputPath} {MountPoint}", cancellationToken: ct);
            ThrowOnFailure(mountResult, "mount");

            try
            {
                var exportResult = await _processRunner.RunAsync(
                    "bash",
                    $"-c \"docker export {DockerContainerName} | tar -x -C {MountPoint}\"",
                    cancellationToken: ct);
                ThrowOnFailure(exportResult, "docker export + tar");
            }
            finally
            {
                // Always unmount, even if export fails
                var umountResult = await _processRunner.RunAsync(
                    "umount", MountPoint, cancellationToken: ct);
                if (umountResult.ExitCode != 0)
                {
                    _logger.LogWarning("Failed to unmount {MountPoint}: {Stderr}",
                        MountPoint, umountResult.StandardError);
                }
            }
        }
        finally
        {
            // Always clean up the temporary container
            var rmResult = await _processRunner.RunAsync(
                "docker", $"rm {DockerContainerName}", cancellationToken: ct);
            if (rmResult.ExitCode != 0)
            {
                _logger.LogWarning("Failed to remove temporary container: {Stderr}", rmResult.StandardError);
            }
        }

        _logger.LogInformation("Rootfs image built at {OutputPath}", outputPath);
        return outputPath;
    }

    /// <inheritdoc/>
    public async Task<string> PrepareRootfsAsync(
        string baseRootfsPath, string vmId, string workDir, CancellationToken ct = default)
    {
        var vmDir = GetVmDir(workDir, vmId);
        var vmRootfs = Path.Combine(vmDir, RootfsFileName);

        _logger.LogInformation("Preparing rootfs for VM {VmId} at {VmDir}", vmId, vmDir);

        await _processRunner.RunAsync("mkdir", $"-p {vmDir}", cancellationToken: ct);

        var cpResult = await _processRunner.RunAsync(
            "cp", $"--reflink=auto {baseRootfsPath} {vmRootfs}", cancellationToken: ct);
        ThrowOnFailure(cpResult, "cp rootfs");

        _logger.LogInformation("Rootfs prepared at {VmRootfs}", vmRootfs);
        return vmRootfs;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Cleanup
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task CleanupVmAsync(string vmId, string workDir, CancellationToken ct = default)
    {
        var vmDir = GetVmDir(workDir, vmId);

        _logger.LogInformation("Cleaning up VM {VmId} at {VmDir}", vmId, vmDir);

        var result = await _processRunner.RunAsync("rm", $"-rf {vmDir}", cancellationToken: ct);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning("Cleanup of {VmDir} returned exit code {ExitCode}: {Stderr}",
                vmDir, result.ExitCode, result.StandardError);
        }

        _activeVms.TryRemove(vmId, out _);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Boot / Stop / Status
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<FirecrackerVmState> BootVmAsync(FirecrackerVmConfig config, CancellationToken ct = default)
    {
        var vmId = Guid.NewGuid().ToString("N")[..12];
        _logger.LogInformation("Booting Firecracker VM {VmId} with {Vcpus} vCPUs, {Mem} MiB RAM",
            vmId, config.VcpuCount, config.MemSizeMib);

        // 1. Remove stale socket if present
        if (File.Exists(config.SocketPath))
        {
            File.Delete(config.SocketPath);
        }

        // 2. Start the firecracker process (long-running, do not await)
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "firecracker",
                Arguments = $"--api-sock {config.SocketPath}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        _activeVms[vmId] = process;

        _logger.LogDebug("Firecracker process started with PID {Pid}", process.Id);

        // 3. Wait for the API socket to appear
        await WaitForSocketAsync(config.SocketPath, ct);

        // 4. Configure the VM via the Firecracker REST API over Unix socket
        using var httpClient = CreateUnixSocketClient(config.SocketPath);

        // Machine configuration
        await PutJsonAsync(httpClient, "/machine-config", new
        {
            vcpu_count = config.VcpuCount,
            mem_size_mib = config.MemSizeMib
        }, ct);

        // Boot source
        var bootArgs = $"console=ttyS0 reboot=k panic=1 pci=off " +
                       $"ip={config.VmIpAddress}::{config.GatewayIp}:255.255.255.252::eth0:off";

        await PutJsonAsync(httpClient, "/boot-source", new
        {
            kernel_image_path = config.KernelPath,
            boot_args = bootArgs
        }, ct);

        // Root drive
        await PutJsonAsync(httpClient, "/drives/rootfs", new
        {
            drive_id = "rootfs",
            path_on_host = config.RootfsPath,
            is_root_device = true,
            is_read_only = false
        }, ct);

        // Network interface
        await PutJsonAsync(httpClient, "/network-interfaces/eth0", new
        {
            iface_id = "eth0",
            host_dev_name = config.TapDevice,
            guest_mac = config.MacAddress
        }, ct);

        // Start the instance
        await PutJsonAsync(httpClient, "/actions", new
        {
            action_type = "InstanceStart"
        }, ct);

        // 5. Persist PID so StopVmAsync can find it
        var vmDir = Path.GetDirectoryName(config.SocketPath) ?? string.Empty;
        var pidPath = Path.Combine(vmDir, PidFileName);
        await File.WriteAllTextAsync(pidPath, process.Id.ToString(), ct);

        var state = new FirecrackerVmState
        {
            VmId = vmId,
            ProcessId = process.Id,
            TapDevice = config.TapDevice,
            IpAddress = config.VmIpAddress,
            StartedAt = DateTime.UtcNow,
            Status = FirecrackerVmStatus.Running
        };

        _logger.LogInformation("VM {VmId} is running (PID {Pid}, IP {Ip})",
            vmId, process.Id, config.VmIpAddress);

        return state;
    }

    /// <inheritdoc/>
    public async Task StopVmAsync(string vmId, string workDir, CancellationToken ct = default)
    {
        _logger.LogInformation("Stopping VM {VmId}", vmId);

        // Try the in-memory process first
        if (_activeVms.TryRemove(vmId, out var process))
        {
            if (!process.HasExited)
            {
                var killResult = await _processRunner.RunAsync(
                    "kill", process.Id.ToString(), cancellationToken: ct);

                if (killResult.ExitCode != 0)
                {
                    _logger.LogWarning("kill {Pid} failed: {Stderr}", process.Id, killResult.StandardError);
                }

                // Give the process a moment to exit gracefully
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                    await process.WaitForExitAsync(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Firecracker process {Pid} did not exit in time", process.Id);
                }
            }

            process.Dispose();
        }
        else
        {
            // Fall back to PID file
            var pidPath = Path.Combine(GetVmDir(workDir, vmId), PidFileName);
            if (File.Exists(pidPath))
            {
                var pidText = await File.ReadAllTextAsync(pidPath, ct);
                if (int.TryParse(pidText.Trim(), out var pid))
                {
                    var killResult = await _processRunner.RunAsync(
                        "kill", pid.ToString(), cancellationToken: ct);
                    if (killResult.ExitCode != 0)
                    {
                        _logger.LogWarning("kill {Pid} (from pid file) failed: {Stderr}",
                            pid, killResult.StandardError);
                    }
                }
            }
        }

        // Clean up the socket file
        var socketPath = Path.Combine(GetVmDir(workDir, vmId), SocketFileName);
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        _logger.LogInformation("VM {VmId} stopped", vmId);
    }

    /// <inheritdoc/>
    public async Task<bool> IsVmRunningAsync(string vmId, string workDir, CancellationToken ct = default)
    {
        var vmDir = GetVmDir(workDir, vmId);
        var socketPath = Path.Combine(vmDir, SocketFileName);

        // Quick check: does the socket file exist?
        var socketResult = await _processRunner.RunAsync(
            "test", $"-e {socketPath}", cancellationToken: ct);

        if (socketResult.ExitCode != 0)
        {
            return false;
        }

        // Check whether the process is still alive
        if (_activeVms.TryGetValue(vmId, out var process))
        {
            return !process.HasExited;
        }

        // Fall back to PID file
        var pidPath = Path.Combine(vmDir, PidFileName);
        if (!File.Exists(pidPath))
        {
            return false;
        }

        var pidText = await File.ReadAllTextAsync(pidPath, ct);
        if (!int.TryParse(pidText.Trim(), out var pid))
        {
            return false;
        }

        var killCheck = await _processRunner.RunAsync(
            "kill", $"-0 {pid}", cancellationToken: ct);
        return killCheck.ExitCode == 0;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  SSH Execution
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ProcessResult> ExecuteInVmAsync(
        string vmIpAddress, string sshKeyPath, string command,
        int timeoutSeconds = 300, CancellationToken ct = default)
    {
        _logger.LogDebug("Executing command in VM at {Ip}: {Command}", vmIpAddress, command);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var result = await _processRunner.RunAsync(
            "ssh",
            $"-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null " +
            $"-o ConnectTimeout=10 -i {sshKeyPath} root@{vmIpAddress} '{command}'",
            cancellationToken: linked.Token);

        _logger.LogDebug("Command exited with code {ExitCode}", result.ExitCode);
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ──────────────────────────────────────────────────────────────────────

    private static string GetVmDir(string workDir, string vmId) =>
        Path.Combine(workDir, "vms", vmId);

    /// <summary>
    /// Creates an <see cref="HttpClient"/> that communicates over a Unix domain socket.
    /// The Firecracker API is exposed exclusively via a Unix socket, so all HTTP
    /// requests are routed through a <see cref="SocketsHttpHandler"/> with a custom
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>.
    /// </summary>
    private static HttpClient CreateUnixSocketClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(socketPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>
    /// Sends a PUT request with a JSON body to the Firecracker API and throws on failure.
    /// </summary>
    private async Task PutJsonAsync<T>(HttpClient client, string path, T payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogDebug("PUT {Path}: {Json}", path, json);

        var response = await client.PutAsync(path, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Firecracker API PUT {Path} returned {StatusCode}: {Body}",
                path, (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"Firecracker API PUT {path} failed ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Polls until the Firecracker API socket file appears on disk.
    /// </summary>
    private async Task WaitForSocketAsync(string socketPath, CancellationToken ct)
    {
        for (var i = 0; i < SocketPollMaxAttempts; i++)
        {
            if (File.Exists(socketPath))
            {
                _logger.LogDebug("Firecracker socket appeared at {SocketPath}", socketPath);
                return;
            }

            await Task.Delay(SocketPollDelayMs, ct);
        }

        throw new TimeoutException(
            $"Firecracker API socket did not appear at {socketPath} " +
            $"after {SocketPollMaxAttempts * SocketPollDelayMs}ms");
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a process result indicates failure.
    /// </summary>
    private void ThrowOnFailure(ProcessResult result, string stepName)
    {
        if (result.ExitCode != 0)
        {
            _logger.LogError("{Step} failed (exit {Code}): {Stderr}",
                stepName, result.ExitCode, result.StandardError);
            throw new InvalidOperationException(
                $"{stepName} failed (exit {result.ExitCode}): {result.StandardError}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Dispose
    // ──────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        foreach (var kvp in _activeVms)
        {
            try
            {
                if (!kvp.Value.HasExited)
                {
                    kvp.Value.Kill();
                }

                kvp.Value.Dispose();
            }
            catch (InvalidOperationException)
            {
                // Process may have already exited
            }
        }

        _activeVms.Clear();
    }
}
