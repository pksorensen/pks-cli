using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PKS.Infrastructure.Services;

/// <summary>The managed OS disk currently attached to a VM, with what a replacement must copy.</summary>
public sealed record AzureVmOsDisk(
    string Id,
    string Name,
    string Location,
    string Sku,
    IReadOnlyList<string> Zones,
    IReadOnlyDictionary<string, string> Tags,
    JsonNode? SecurityProfile);

/// <summary>A VM's baseline snapshot (<c>{vm}-baseline</c>).</summary>
public sealed record AzureVmBaseline(string Id, string Name, DateTimeOffset? TimeCreated, string? SourceDiskId);

/// <summary>What <see cref="IAzureVmDiskService.ResetToBaselineAsync"/> did.</summary>
public sealed record AzureVmResetResult(string NewDiskName, string OldDiskName, bool OldDiskDeleted, string? OldDiskKeptReason);

/// <summary>
/// OS-disk baselines for pks-managed Azure VMs: snapshot the OS disk once in a known-good state,
/// and later put the VM back to it by swapping in a fresh disk made from that snapshot. The VM,
/// NIC, public IP, NSG rules and SSH key survive, and so do the host keys (they live on the disk,
/// which is copied from the same snapshot every time), so known_hosts stays valid.
/// </summary>
public interface IAzureVmDiskService
{
    Task<AzureVmOsDisk?> GetOsDiskAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    Task<AzureVmBaseline?> GetBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);

    /// <summary>Creates <c>{vm}-baseline</c> from the VM's current OS disk, deleting an existing one first
    /// (an incremental snapshot's source cannot be changed in place).</summary>
    Task<AzureVmBaseline> CreateBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default);

    /// <summary>Wipes the VM back to its baseline: new disk from the snapshot → deallocate → swap OS
    /// disk → start → delete the old disk (only when it is recognisably this VM's).</summary>
    Task<AzureVmResetResult> ResetToBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default);
}

public class AzureVmDiskService : IAzureVmDiskService
{
    private const string Arm = "https://management.azure.com";
    private const string VmApiVersion = "2023-09-01";
    private const string DiskApiVersion = "2024-03-02";

    private readonly HttpClient _http;
    private readonly IAzureVmService _vm;
    private readonly TimeSpan _pollInterval;

    public AzureVmDiskService(HttpClient http, IAzureVmService vm, TimeSpan? pollInterval = null)
    {
        _http = http;
        _vm = vm;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    public static string BaselineName(string vmName) => $"{vmName}-baseline";

    public static string NewDiskName(string vmName, DateTime utcNow) => $"{vmName}-osdisk-{utcNow:yyyyMMddHHmmss}";

    /// <summary>A disk reset may delete: tagged as this VM's by pks, or named the way Azure/pks name its OS disks.</summary>
    public static bool IsDisposableOldDisk(AzureVmOsDisk disk, string vmName)
    {
        if (disk.Tags.TryGetValue("pks-vm-name", out var owner) && string.Equals(owner, vmName, StringComparison.OrdinalIgnoreCase))
            return true;
        return disk.Name.StartsWith($"{vmName}_OsDisk_", StringComparison.OrdinalIgnoreCase)
            || disk.Name.StartsWith($"{vmName}-osdisk-", StringComparison.OrdinalIgnoreCase);
    }

    private static string VmUrl(string sub, string rg, string vm)
        => $"{Arm}/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/virtualMachines/{vm}?api-version={VmApiVersion}";

    private static string SnapshotUrl(string sub, string rg, string name)
        => $"{Arm}/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/snapshots/{name}?api-version={DiskApiVersion}";

    private static string DiskUrl(string sub, string rg, string name)
        => $"{Arm}/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/disks/{name}?api-version={DiskApiVersion}";

    private static string ResourceUrl(string resourceId) => $"{Arm}{resourceId}?api-version={DiskApiVersion}";

    public async Task<AzureVmOsDisk?> GetOsDiskAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var vm = await GetAsync(accessToken, VmUrl(subscriptionId, resourceGroup, vmName), ct);
        var diskId = (string?)vm?["properties"]?["storageProfile"]?["osDisk"]?["managedDisk"]?["id"];
        if (string.IsNullOrEmpty(diskId)) return null;

        var disk = await GetAsync(accessToken, ResourceUrl(diskId), ct)
            ?? throw new InvalidOperationException($"OS disk '{diskId}' not found.");

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in disk["tags"]?.AsObject() ?? new JsonObject())
            if (v != null) tags[k] = v.ToString();

        return new AzureVmOsDisk(
            Id: diskId,
            Name: (string?)disk["name"] ?? diskId.Split('/').Last(),
            Location: (string?)disk["location"] ?? "",
            Sku: (string?)disk["sku"]?["name"] ?? "Premium_LRS",
            Zones: disk["zones"]?.AsArray().Select(z => z?.ToString() ?? "").Where(z => z != "").ToList() ?? new List<string>(),
            Tags: tags,
            SecurityProfile: disk["properties"]?["securityProfile"]?.DeepClone());
    }

    public async Task<AzureVmBaseline?> GetBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var snap = await GetAsync(accessToken, SnapshotUrl(subscriptionId, resourceGroup, BaselineName(vmName)), ct);
        if (snap == null) return null;
        return new AzureVmBaseline(
            Id: (string?)snap["id"] ?? "",
            Name: (string?)snap["name"] ?? BaselineName(vmName),
            TimeCreated: DateTimeOffset.TryParse((string?)snap["properties"]?["timeCreated"], out var t) ? t : null,
            SourceDiskId: (string?)snap["properties"]?["creationData"]?["sourceResourceId"]);
    }

    /// <summary>The snapshot body. Public for the request-shape tests.</summary>
    public static JsonObject BaselineBody(AzureVmOsDisk disk, string vmName) => new()
    {
        ["location"] = disk.Location,
        // Incremental snapshots are billed on changed blocks only; Standard_LRS is the cheap tier.
        ["sku"] = new JsonObject { ["name"] = "Standard_LRS" },
        ["tags"] = new JsonObject
        {
            ["pks-managed"] = "true",
            ["pks-vm-name"] = vmName,
            ["pks-role"] = "baseline",
        },
        ["properties"] = new JsonObject
        {
            ["incremental"] = true,
            ["creationData"] = new JsonObject
            {
                ["createOption"] = "Copy",
                ["sourceResourceId"] = disk.Id,
            },
        },
    };

    /// <summary>The replacement-disk body. Public for the request-shape tests.</summary>
    public static JsonObject DiskFromSnapshotBody(AzureVmOsDisk template, string snapshotId, string vmName)
    {
        var tags = new JsonObject();
        foreach (var (k, v) in template.Tags) tags[k] = v;
        tags["pks-managed"] = "true";
        tags["pks-vm-name"] = vmName;

        var properties = new JsonObject
        {
            ["creationData"] = new JsonObject
            {
                ["createOption"] = "Copy",
                ["sourceResourceId"] = snapshotId,
            },
        };
        if (template.SecurityProfile != null)
            properties["securityProfile"] = template.SecurityProfile.DeepClone();

        var body = new JsonObject
        {
            ["location"] = template.Location,
            ["sku"] = new JsonObject { ["name"] = template.Sku },
            ["tags"] = tags,
            ["properties"] = properties,
        };
        if (template.Zones.Count > 0)
            body["zones"] = new JsonArray(template.Zones.Select(z => (JsonNode)JsonValue.Create(z)!).ToArray());
        return body;
    }

    /// <summary>The OS-disk swap PATCH body. Public for the request-shape tests.</summary>
    public static JsonObject SwapOsDiskBody(string diskId, string diskName) => new()
    {
        ["properties"] = new JsonObject
        {
            ["storageProfile"] = new JsonObject
            {
                ["osDisk"] = new JsonObject
                {
                    ["name"] = diskName,
                    ["managedDisk"] = new JsonObject { ["id"] = diskId },
                },
            },
        },
    };

    public async Task<AzureVmBaseline> CreateBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var disk = await GetOsDiskAsync(accessToken, subscriptionId, resourceGroup, vmName, ct)
            ?? throw new InvalidOperationException($"VM '{vmName}' has no managed OS disk.");

        var url = SnapshotUrl(subscriptionId, resourceGroup, BaselineName(vmName));
        if (await GetAsync(accessToken, url, ct) != null)
        {
            onProgress?.Invoke("Deleting the previous baseline...");
            await _vm.DeleteResourceAsync(accessToken, url, ct);
        }

        onProgress?.Invoke($"Snapshotting {disk.Name}...");
        await SendAndWaitAsync(HttpMethod.Put, url, BaselineBody(disk, vmName), url, "baseline snapshot", accessToken, ct);

        return await GetBaselineAsync(accessToken, subscriptionId, resourceGroup, vmName, ct)
            ?? throw new InvalidOperationException("Baseline snapshot was created but cannot be read back.");
    }

    public async Task<AzureVmResetResult> ResetToBaselineAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var baseline = await GetBaselineAsync(accessToken, subscriptionId, resourceGroup, vmName, ct)
            ?? throw new InvalidOperationException($"VM '{vmName}' has no baseline snapshot. Create one with 'pks vm snapshot {vmName}'.");
        var oldDisk = await GetOsDiskAsync(accessToken, subscriptionId, resourceGroup, vmName, ct)
            ?? throw new InvalidOperationException($"VM '{vmName}' has no managed OS disk.");

        // 1. The new disk first, while the VM still runs: the downtime is only the swap.
        var newName = NewDiskName(vmName, DateTime.UtcNow);
        var newUrl = DiskUrl(subscriptionId, resourceGroup, newName);
        onProgress?.Invoke($"Creating {newName} from {baseline.Name}...");
        await SendAndWaitAsync(HttpMethod.Put, newUrl, DiskFromSnapshotBody(oldDisk, baseline.Id, vmName), newUrl, "OS disk", accessToken, ct);
        var newId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/disks/{newName}";

        var swapped = false;
        try
        {
            // 2. Deallocate — an OS disk can only be swapped on a stopped (deallocated) VM.
            onProgress?.Invoke("Deallocating the VM...");
            await _vm.DeallocateVmAsync(accessToken, subscriptionId, resourceGroup, vmName, ct);
            await WaitForPowerStateAsync(accessToken, subscriptionId, resourceGroup, vmName, "deallocated", TimeSpan.FromMinutes(10), ct);

            // 3. Swap.
            onProgress?.Invoke("Swapping the OS disk...");
            var vmUrl = VmUrl(subscriptionId, resourceGroup, vmName);
            await SendAndWaitAsync(HttpMethod.Patch, vmUrl, SwapOsDiskBody(newId, newName), vmUrl, "OS disk swap", accessToken, ct);
            swapped = true;
        }
        catch when (!swapped)
        {
            // Nothing changed on the VM; do not leave an orphaned copy behind. The VM is started
            // again so a failed reset does not also leave it off.
            try { await _vm.DeleteResourceAsync(accessToken, newUrl, ct); } catch { /* best effort */ }
            try { await _vm.StartVmAsync(accessToken, subscriptionId, resourceGroup, vmName, ct); } catch { /* best effort */ }
            throw;
        }

        // 4. Start.
        onProgress?.Invoke("Starting the VM...");
        await _vm.StartVmAsync(accessToken, subscriptionId, resourceGroup, vmName, ct);
        await WaitForPowerStateAsync(accessToken, subscriptionId, resourceGroup, vmName, "running", TimeSpan.FromMinutes(10), ct);

        // 5. The old disk — only when it is recognisably this VM's.
        if (!IsDisposableOldDisk(oldDisk, vmName))
            return new AzureVmResetResult(newName, oldDisk.Name, false,
                "it is neither tagged pks-vm-name nor named like this VM's OS disks");

        onProgress?.Invoke($"Deleting the old disk {oldDisk.Name}...");
        try
        {
            await _vm.DeleteResourceAsync(accessToken, ResourceUrl(oldDisk.Id), ct);
            return new AzureVmResetResult(newName, oldDisk.Name, true, null);
        }
        catch (Exception ex)
        {
            return new AzureVmResetResult(newName, oldDisk.Name, false, $"delete failed: {ex.Message}");
        }
    }

    private async Task WaitForPowerStateAsync(string token, string sub, string rg, string vm, string want, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await _vm.GetVmStatusAsync(token, sub, rg, vm, ct);
            if (string.Equals(state, want, StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(_pollInterval, ct);
        }
        throw new TimeoutException($"VM '{vm}' did not reach '{want}' within {timeout.TotalMinutes:0} minutes.");
    }

    private async Task<JsonNode?> GetAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} reading {url.Split('?')[0]}: {body}", null, resp.StatusCode);
        return JsonNode.Parse(body);
    }

    /// <summary>Sends a long-running ARM write and waits for it: via Azure-AsyncOperation when ARM
    /// gives one, otherwise by polling the resource's provisioningState.</summary>
    private async Task SendAndWaitAsync(HttpMethod method, string url, JsonNode body, string resourceUrl, string label, string token, CancellationToken ct)
    {
        string? asyncOp;
        using (var req = new HttpRequestMessage(method, url))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"ARM {(int)resp.StatusCode} writing {label}: {err}", null, resp.StatusCode);
            }
            asyncOp = resp.Headers.TryGetValues("Azure-AsyncOperation", out var ops) ? ops.FirstOrDefault() : null;
        }

        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            string? status;
            if (asyncOp != null)
            {
                var op = await GetAsync(token, asyncOp, ct);
                status = (string?)op?["status"];
                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{label} failed: {op?["error"]?.ToJsonString() ?? status}");
            }
            else
            {
                var resource = await GetAsync(token, resourceUrl, ct);
                status = (string?)resource?["properties"]?["provisioningState"];
                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{label} provisioning failed.");
            }
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(_pollInterval, ct);
        }
        throw new TimeoutException($"Timed out waiting for {label}.");
    }
}
