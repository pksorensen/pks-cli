using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Runner;

namespace PKS.Infrastructure.Services.Firecracker;

/// <summary>
/// Manages TAP device creation/cleanup and IP allocation for Firecracker VMs.
/// Each VM receives a unique /30 subnet carved from a configurable larger subnet,
/// with persistent state tracking to survive process restarts.
/// </summary>
public class FirecrackerNetworkManager
{
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<FirecrackerNetworkManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public FirecrackerNetworkManager(IProcessRunner processRunner, ILogger<FirecrackerNetworkManager> logger)
    {
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <summary>
    /// Allocates a network configuration for a Firecracker VM, creating the TAP device
    /// and configuring IP forwarding.
    /// </summary>
    /// <param name="vmId">Unique identifier for the VM</param>
    /// <param name="workDir">Working directory where network state is persisted</param>
    /// <param name="subnet">Base subnet to carve /30 blocks from (default: 172.16.0.0/16)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tuple of (TapDevice, VmIp, GatewayIp, MacAddress)</returns>
    public async Task<(string TapDevice, string VmIp, string GatewayIp, string MacAddress)> AllocateNetworkAsync(
        string vmId,
        string workDir,
        string subnet = "172.16.0.0/16",
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(workDir);

            // Check if this VM already has an allocation
            if (state.Allocations.TryGetValue(vmId, out var existing))
            {
                _logger.LogWarning("VM {VmId} already has a network allocation on slot {Slot}, returning existing", vmId, existing.Slot);
                return (existing.TapDevice, existing.VmIp, existing.GatewayIp, existing.MacAddress);
            }

            // Pick next available slot
            int slot;
            if (state.FreedSlots.Count > 0)
            {
                slot = state.FreedSlots[0];
                state.FreedSlots.RemoveAt(0);
                _logger.LogDebug("Reusing freed slot {Slot} for VM {VmId}", slot, vmId);
            }
            else
            {
                slot = state.NextSlot;
                state.NextSlot++;
                _logger.LogDebug("Allocated new slot {Slot} for VM {VmId}", slot, vmId);
            }

            var (tapDevice, vmIp, gatewayIp, macAddress) = ComputeAddresses(slot);

            // Create TAP device
            var createResult = await _processRunner.RunAsync("ip", $"tuntap add {tapDevice} mode tap", null, ct);
            if (createResult.ExitCode != 0)
            {
                _logger.LogError("Failed to create TAP device {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    tapDevice, createResult.ExitCode, createResult.StandardError);
                throw new InvalidOperationException($"Failed to create TAP device {tapDevice}: {createResult.StandardError}");
            }

            // Configure TAP device with gateway IP
            var addrResult = await _processRunner.RunAsync("ip", $"addr add {gatewayIp}/30 dev {tapDevice}", null, ct);
            if (addrResult.ExitCode != 0)
            {
                _logger.LogError("Failed to configure IP on {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    tapDevice, addrResult.ExitCode, addrResult.StandardError);
                // Attempt cleanup of the TAP device we just created
                await BestEffortRunAsync("ip", $"link del {tapDevice}", ct);
                throw new InvalidOperationException($"Failed to configure IP on {tapDevice}: {addrResult.StandardError}");
            }

            // Bring up the TAP device
            var upResult = await _processRunner.RunAsync("ip", $"link set {tapDevice} up", null, ct);
            if (upResult.ExitCode != 0)
            {
                _logger.LogError("Failed to bring up {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    tapDevice, upResult.ExitCode, upResult.StandardError);
                await BestEffortRunAsync("ip", $"link del {tapDevice}", ct);
                throw new InvalidOperationException($"Failed to bring up {tapDevice}: {upResult.StandardError}");
            }

            // Set up NAT forwarding
            var fwdResult = await _processRunner.RunAsync("iptables", $"-A FORWARD -i {tapDevice} -j ACCEPT", null, ct);
            if (fwdResult.ExitCode != 0)
            {
                _logger.LogError("Failed to add iptables FORWARD rule for {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    tapDevice, fwdResult.ExitCode, fwdResult.StandardError);
                await BestEffortRunAsync("ip", $"link del {tapDevice}", ct);
                throw new InvalidOperationException($"Failed to add iptables FORWARD rule for {tapDevice}: {fwdResult.StandardError}");
            }

            // Save allocation to state
            var allocation = new NetworkAllocation
            {
                VmId = vmId,
                Slot = slot,
                TapDevice = tapDevice,
                VmIp = vmIp,
                GatewayIp = gatewayIp,
                MacAddress = macAddress
            };
            state.Allocations[vmId] = allocation;

            await SaveStateAsync(state, workDir);

            _logger.LogInformation(
                "Allocated network for VM {VmId}: tap={TapDevice}, vmIp={VmIp}, gw={GatewayIp}, mac={MacAddress}",
                vmId, tapDevice, vmIp, gatewayIp, macAddress);

            return (tapDevice, vmIp, gatewayIp, macAddress);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Releases the network allocation for a VM, removing the TAP device and iptables rules.
    /// Cleanup is best-effort: failures are logged but do not throw.
    /// </summary>
    /// <param name="vmId">Unique identifier for the VM</param>
    /// <param name="workDir">Working directory where network state is persisted</param>
    /// <param name="ct">Cancellation token</param>
    public async Task ReleaseNetworkAsync(string vmId, string workDir, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var state = await LoadStateAsync(workDir);

            if (!state.Allocations.TryGetValue(vmId, out var allocation))
            {
                _logger.LogWarning("No network allocation found for VM {VmId}, nothing to release", vmId);
                return;
            }

            // Remove iptables FORWARD rule (best-effort)
            var fwdResult = await BestEffortRunAsync("iptables", $"-D FORWARD -i {allocation.TapDevice} -j ACCEPT", ct);
            if (fwdResult.ExitCode != 0)
            {
                _logger.LogWarning("Failed to remove iptables FORWARD rule for {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    allocation.TapDevice, fwdResult.ExitCode, fwdResult.StandardError);
            }

            // Delete TAP device (best-effort)
            var delResult = await BestEffortRunAsync("ip", $"link del {allocation.TapDevice}", ct);
            if (delResult.ExitCode != 0)
            {
                _logger.LogWarning("Failed to delete TAP device {TapDevice}: exit code {ExitCode}, stderr: {Stderr}",
                    allocation.TapDevice, delResult.ExitCode, delResult.StandardError);
            }

            // Return slot to the free pool
            state.FreedSlots.Add(allocation.Slot);
            state.Allocations.Remove(vmId);

            await SaveStateAsync(state, workDir);

            _logger.LogInformation("Released network for VM {VmId} (slot {Slot}, tap {TapDevice})",
                vmId, allocation.Slot, allocation.TapDevice);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Retrieves the current network allocation for a VM, if one exists.
    /// </summary>
    /// <param name="vmId">Unique identifier for the VM</param>
    /// <param name="workDir">Working directory where network state is persisted</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The allocation details, or null if the VM has no allocation</returns>
    public async Task<(string TapDevice, string VmIp, string GatewayIp, string MacAddress)?> GetAllocationAsync(
        string vmId,
        string workDir,
        CancellationToken ct = default)
    {
        var state = await LoadStateAsync(workDir);

        if (!state.Allocations.TryGetValue(vmId, out var allocation))
        {
            return null;
        }

        return (allocation.TapDevice, allocation.VmIp, allocation.GatewayIp, allocation.MacAddress);
    }

    /// <summary>
    /// Computes TAP device name, VM IP, gateway IP, and MAC address from a slot number.
    /// Each slot produces a /30 subnet carved from 172.16.0.0/16.
    /// </summary>
    /// <param name="slot">The slot number (0-based)</param>
    /// <returns>Tuple of (tapDevice, vmIp, gatewayIp, macAddress)</returns>
    internal static (string TapDevice, string VmIp, string GatewayIp, string MacAddress) ComputeAddresses(int slot)
    {
        var tapDevice = $"tap-fc-{slot}";

        // Each /30 block consumes 4 IPs. With 64 /30 blocks per /24:
        // octet3 = slot / 64
        // offset within that /24 = (slot % 64) * 4
        var octet3 = slot / 64;
        var baseOffset = (slot % 64) * 4;

        var gatewayIp = $"172.16.{octet3}.{baseOffset + 1}";
        var vmIp = $"172.16.{octet3}.{baseOffset + 2}";

        // MAC: AA:FC:00:00:HH:LL where HH:LL encodes the slot as a 16-bit value
        var macAddress = $"AA:FC:00:00:{slot / 256:X2}:{slot % 256:X2}";

        return (tapDevice, vmIp, gatewayIp, macAddress);
    }

    private async Task<NetworkState> LoadStateAsync(string workDir)
    {
        var stateFile = Path.Combine(workDir, "network-state.json");

        if (!File.Exists(stateFile))
        {
            _logger.LogDebug("Network state file not found at {Path}, returning empty state", stateFile);
            return new NetworkState();
        }

        try
        {
            var json = await File.ReadAllTextAsync(stateFile);
            var state = JsonSerializer.Deserialize<NetworkState>(json, JsonOptions);
            _logger.LogDebug("Loaded network state with {Count} allocations from {Path}",
                state?.Allocations.Count ?? 0, stateFile);
            return state ?? new NetworkState();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize network state from {Path}, returning empty state", stateFile);
            return new NetworkState();
        }
    }

    private async Task SaveStateAsync(NetworkState state, string workDir)
    {
        var stateFile = Path.Combine(workDir, "network-state.json");

        var directory = Path.GetDirectoryName(stateFile);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(stateFile, json);

        _logger.LogDebug("Saved network state with {Count} allocations to {Path}",
            state.Allocations.Count, stateFile);
    }

    /// <summary>
    /// Runs a process command without throwing on failure. Used for best-effort cleanup.
    /// </summary>
    private async Task<ProcessResult> BestEffortRunAsync(string command, string arguments, CancellationToken ct)
    {
        try
        {
            return await _processRunner.RunAsync(command, arguments, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort command failed: {Command} {Arguments}", command, arguments);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private class NetworkState
    {
        public Dictionary<string, NetworkAllocation> Allocations { get; set; } = new();
        public int NextSlot { get; set; }
        public List<int> FreedSlots { get; set; } = new();
    }

    private class NetworkAllocation
    {
        public string VmId { get; set; } = "";
        public int Slot { get; set; }
        public string TapDevice { get; set; } = "";
        public string VmIp { get; set; } = "";
        public string GatewayIp { get; set; } = "";
        public string MacAddress { get; set; } = "";
    }
}
