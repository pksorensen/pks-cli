using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for Azure VM provisioning operations
/// </summary>
public interface IAzureVmService
{
    Task<List<AzureResourceGroup>> ListResourceGroupsAsync(string accessToken, string subscriptionId, CancellationToken ct = default);
    Task<AzureResourceGroup> EnsureResourceGroupAsync(string accessToken, string subscriptionId, string name, string location, CancellationToken ct = default);
    Task<AzureVmInfo> CreateVmAsync(AzureVmCreateOptions options, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<string?> GetVmPublicIpAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    Task<bool> WaitForSshAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default);
    /// <summary>Returns the power state (e.g. "running", "deallocated", "stopped") or null if the VM does not exist.</summary>
    Task<string?> GetVmStatusAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    /// <summary>Starts a deallocated or stopped VM. Returns when the start command is accepted (VM may still be booting).</summary>
    Task StartVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    /// <summary>Deallocates a running VM (stops billing for compute). Returns when the deallocate command is accepted.</summary>
    Task DeallocateVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    Task SetScheduledShutdownAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string location, string vmId, string timeUtc, CancellationToken ct = default);
    Task DisableScheduledShutdownAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, CancellationToken ct = default);
    Task SetScheduledStartAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string location, string vmId, string timeUtc, CancellationToken ct = default);
    Task DisableScheduledStartAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, CancellationToken ct = default);
}

/// <summary>
/// Azure VM provisioning service using Azure ARM REST API
/// </summary>
public class AzureVmService : IAzureVmService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureVmService> _logger;
    private const string ApiVersion = "2023-09-01";
    private const string NetworkApiVersion = "2023-09-01";

    public AzureVmService(HttpClient httpClient, ILogger<AzureVmService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<AzureResourceGroup>> ListResourceGroupsAsync(string accessToken, string subscriptionId, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourcegroups?api-version=2021-04-01";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)response.StatusCode} listing resource groups: {content}", null, response.StatusCode);

        var result = JsonSerializer.Deserialize<AzureResourceGroupListResponse>(content);
        return result?.Value ?? new List<AzureResourceGroup>();
    }

    public async Task<AzureResourceGroup> EnsureResourceGroupAsync(string accessToken, string subscriptionId, string name, string location, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{name}?api-version=2021-04-01";
        var body = JsonSerializer.Serialize(new { location });

        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ARM {(int)response.StatusCode} creating resource group '{name}': {content}", null, response.StatusCode);

        var rg = JsonSerializer.Deserialize<AzureResourceGroup>(content);
        return rg ?? new AzureResourceGroup { Name = name, Location = location };
    }

    private async Task EnsureProvidersRegisteredAsync(string accessToken, string subscriptionId, string[] providers, Action<string>? onProgress, CancellationToken ct)
    {
        foreach (var provider in providers)
        {
            var checkUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/{provider}?api-version=2022-09-01";
            var checkReq = new HttpRequestMessage(HttpMethod.Get, checkUrl);
            checkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var checkResp = await _httpClient.SendAsync(checkReq, ct);
            var checkBody = await checkResp.Content.ReadAsStringAsync(ct);

            string? registrationState = null;
            if (checkResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(checkBody);
                doc.RootElement.TryGetProperty("registrationState", out var stateProp);
                registrationState = stateProp.GetString();
            }

            if (string.Equals(registrationState, "Registered", StringComparison.OrdinalIgnoreCase))
                continue;

            onProgress?.Invoke($"Registering provider {provider}...");
            var regUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/{provider}/register?api-version=2022-09-01";
            var regReq = new HttpRequestMessage(HttpMethod.Post, regUrl);
            regReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            regReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var regResp = await _httpClient.SendAsync(regReq, ct);
            if (!regResp.IsSuccessStatusCode)
            {
                var regBody = await regResp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Failed to register provider {provider}: {regBody}", null, regResp.StatusCode);
            }

            // Poll until Registered (max 3 min)
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var pollReq = new HttpRequestMessage(HttpMethod.Get, checkUrl);
                pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var pollResp = await _httpClient.SendAsync(pollReq, ct);
                var pollBody = await pollResp.Content.ReadAsStringAsync(ct);
                if (pollResp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(pollBody);
                    if (doc.RootElement.TryGetProperty("registrationState", out var s) &&
                        string.Equals(s.GetString(), "Registered", StringComparison.OrdinalIgnoreCase))
                    {
                        onProgress?.Invoke($"Provider {provider} registered.");
                        break;
                    }
                }
            }
        }
    }

    public async Task<AzureVmInfo> CreateVmAsync(AzureVmCreateOptions options, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var sub = options.SubscriptionId;
        var rg = options.ResourceGroupName;
        var vm = options.VmName;
        var loc = options.Location;
        var token = options.AccessToken;

        // Ensure required resource providers are registered before creating any resources
        onProgress?.Invoke("Checking resource provider registrations...");
        await EnsureProvidersRegisteredAsync(token, sub,
            new[] { "Microsoft.Network", "Microsoft.Compute" },
            onProgress, ct);

        string BaseUrl(string provider, string resource) =>
            $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/{provider}/{resource}?api-version={NetworkApiVersion}";

        var debug = Environment.GetEnvironmentVariable("PKS_DEBUG") == "1";

        async Task WaitForProvisioningAsync(string resourceUrl, string resourceLabel)
        {
            var deadline = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                var req = new HttpRequestMessage(HttpMethod.Get, resourceUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await _httpClient.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("provisioningState", out var state))
                {
                    var s = state.GetString();
                    if (debug) Console.Error.WriteLine($"[debug] {resourceLabel} provisioningState: {s}");
                    if (string.Equals(s, "Succeeded", StringComparison.OrdinalIgnoreCase)) return;
                    if (string.Equals(s, "Failed", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{resourceLabel} provisioning failed.");
                }
            }
            throw new TimeoutException($"Timed out waiting for {resourceLabel} to finish provisioning.");
        }

        async Task<string> PutAsync(string url, object bodyObj)
        {
            var requestBody = JsonSerializer.Serialize(bodyObj, new JsonSerializerOptions { WriteIndented = debug });
            if (debug)
            {
                _logger.LogInformation("ARM PUT {Url}", url);
                _logger.LogInformation("Request body: {Body}", requestBody);
                Console.Error.WriteLine($"[debug] PUT {url.Split('?')[0]}");
            }

            var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var resp = await _httpClient.SendAsync(req, ct);
            var responseBody = await resp.Content.ReadAsStringAsync(ct);

            if (debug)
                Console.Error.WriteLine($"[debug] {(int)resp.StatusCode} {resp.StatusCode}: {responseBody}");

            if (!resp.IsSuccessStatusCode)
            {
                var resourceName = url.Split('/').Last().Split('?')[0];
                throw new HttpRequestException(
                    $"ARM {(int)resp.StatusCode} {resp.StatusCode} creating '{resourceName}': {responseBody}",
                    null, resp.StatusCode);
            }

            return responseBody;
        }

        // 1. Create Public IP
        onProgress?.Invoke("Creating public IP address...");
        var ipUrl = BaseUrl("Microsoft.Network/publicIPAddresses", $"{vm}-ip");
        await PutAsync(ipUrl, new
        {
            location = loc,
            sku = new { name = "Standard" },
            properties = new { publicIPAllocationMethod = "Static" }
        });
        onProgress?.Invoke("Waiting for public IP...");
        await WaitForProvisioningAsync(ipUrl, "Public IP");

        // 2. Create NSG with SSH rule
        onProgress?.Invoke("Creating network security group...");
        var nsgUrl = BaseUrl("Microsoft.Network/networkSecurityGroups", $"{vm}-nsg");
        await PutAsync(nsgUrl, new
        {
            location = loc,
            properties = new
            {
                securityRules = new[]
                {
                    new
                    {
                        name = "AllowSSH",
                        properties = new
                        {
                            priority = 1000,
                            protocol = "Tcp",
                            access = "Allow",
                            direction = "Inbound",
                            sourceAddressPrefix = "*",
                            sourcePortRange = "*",
                            destinationAddressPrefix = "*",
                            destinationPortRange = "22"
                        }
                    }
                }
            }
        });
        onProgress?.Invoke("Waiting for network security group...");
        await WaitForProvisioningAsync(nsgUrl, "NSG");

        // 3. Create VNet+Subnet
        onProgress?.Invoke("Creating virtual network...");
        var vnetUrl = BaseUrl("Microsoft.Network/virtualNetworks", $"{vm}-vnet");
        await PutAsync(vnetUrl, new
        {
            location = loc,
            properties = new
            {
                addressSpace = new { addressPrefixes = new[] { "10.0.0.0/16" } },
                subnets = new[]
                {
                    new
                    {
                        name = $"{vm}-subnet",
                        properties = new { addressPrefix = "10.0.0.0/24" }
                    }
                }
            }
        });
        onProgress?.Invoke("Waiting for virtual network...");
        await WaitForProvisioningAsync(vnetUrl, "VNet");

        // 4. Create NIC
        onProgress?.Invoke("Creating network interface...");
        var nicUrl = BaseUrl("Microsoft.Network/networkInterfaces", $"{vm}-nic");
        var subnetId = $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/virtualNetworks/{vm}-vnet/subnets/{vm}-subnet";
        var ipId = $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/publicIPAddresses/{vm}-ip";
        var nsgId = $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/networkSecurityGroups/{vm}-nsg";
        await PutAsync(nicUrl, new
        {
            location = loc,
            properties = new
            {
                networkSecurityGroup = new { id = nsgId },
                ipConfigurations = new[]
                {
                    new
                    {
                        name = "ipconfig1",
                        properties = new
                        {
                            subnet = new { id = subnetId },
                            publicIPAddress = new { id = ipId }
                        }
                    }
                }
            }
        });

        // 5. Build cloud-init
        var cloudInitYaml = BuildCloudInit(options);
        var customData = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudInitYaml));

        // 6. Create VM
        onProgress?.Invoke("Creating virtual machine...");
        var vmUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/virtualMachines/{vm}?api-version={ApiVersion}";
        var nicId = $"/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/networkInterfaces/{vm}-nic";

        await PutAsync(vmUrl, new
        {
            location = loc,
            tags = new Dictionary<string, string>
            {
                ["pks-managed"] = "true",
                ["pks-vm-name"] = vm
            },
            properties = new
            {
                hardwareProfile = new { vmSize = options.VmSize },
                storageProfile = new
                {
                    imageReference = new
                    {
                        publisher = "Canonical",
                        offer = "0001-com-ubuntu-server-jammy",
                        sku = "22_04-lts-gen2",
                        version = "latest"
                    },
                    osDisk = new
                    {
                        createOption = "FromImage",
                        managedDisk = new { storageAccountType = "Premium_LRS" }
                    }
                },
                osProfile = new
                {
                    computerName = vm,
                    adminUsername = options.AdminUsername,
                    customData,
                    linuxConfiguration = new
                    {
                        disablePasswordAuthentication = true,
                        ssh = new
                        {
                            publicKeys = new[]
                            {
                                new
                                {
                                    path = $"/home/{options.AdminUsername}/.ssh/authorized_keys",
                                    keyData = options.SshPublicKey
                                }
                            }
                        }
                    }
                },
                networkProfile = new
                {
                    networkInterfaces = new[]
                    {
                        new { id = nicId }
                    }
                }
            }
        });

        // 7. Poll for provisioning state
        onProgress?.Invoke("Waiting for VM to be ready...");
        var deadline = DateTime.UtcNow.AddMinutes(10);
        string provisioningState = "Creating";
        string? vmId = null;

        while (DateTime.UtcNow < deadline && provisioningState != "Succeeded" && provisioningState != "Failed")
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct);

            var pollReq = new HttpRequestMessage(HttpMethod.Get, vmUrl);
            pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var pollResp = await _httpClient.SendAsync(pollReq, ct);
            var pollContent = await pollResp.Content.ReadAsStringAsync(ct);

            if (pollResp.IsSuccessStatusCode)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(pollContent);
                var root = doc.RootElement;

                if (root.TryGetProperty("id", out var idProp))
                    vmId = idProp.GetString();

                if (root.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("provisioningState", out var stateProp))
                {
                    provisioningState = stateProp.GetString() ?? provisioningState;
                    onProgress?.Invoke($"Provisioning state: {provisioningState}");
                }
            }
        }

        if (provisioningState != "Succeeded")
            throw new InvalidOperationException($"VM provisioning ended with state: {provisioningState}");

        // 8. Get public IP
        onProgress?.Invoke("Getting public IP address...");
        var publicIp = await GetVmPublicIpAsync(token, sub, rg, vm, ct) ?? string.Empty;

        // 9. Set scheduled shutdown if requested
        if (!string.IsNullOrEmpty(options.ScheduledShutdownUtc))
        {
            onProgress?.Invoke($"Setting scheduled shutdown at {options.ScheduledShutdownUtc} UTC...");
            await SetScheduledShutdownAsync(token, sub, rg, vm, loc, vmId ?? string.Empty, options.ScheduledShutdownUtc, ct);
        }

        return new AzureVmInfo
        {
            VmName = vm,
            ResourceGroup = rg,
            Location = loc,
            VmSize = options.VmSize,
            PublicIpAddress = publicIp,
            AdminUsername = options.AdminUsername,
            SshKeyPath = string.Empty,
            Id = vmId ?? string.Empty,
            ProvisioningState = provisioningState
        };
    }

    private static string BuildCloudInit(AzureVmCreateOptions options)
    {
        var parts = new StringBuilder();
        parts.AppendLine("#cloud-config");

        if (options.IdleShutdownMinutes > 0)
        {
            parts.AppendLine("write_files:");
            parts.AppendLine($"  - path: /usr/local/bin/pks-idle-monitor");
            parts.AppendLine($"    permissions: '0755'");
            parts.AppendLine($"    content: |");
            parts.AppendLine($"      #!/bin/bash");
            parts.AppendLine($"      IDLE_THRESHOLD_MINUTES={options.IdleShutdownMinutes}");
            parts.AppendLine($"      CHECK_INTERVAL_SECONDS=300");
            parts.AppendLine($"      IDLE_SINCE=0");
            parts.AppendLine($"      while true; do");
            parts.AppendLine($"        sleep $CHECK_INTERVAL_SECONDS");
            parts.AppendLine($"        SSH_SESSIONS=$(who | wc -l)");
            parts.AppendLine($"        LOAD=$(awk '{{print $2}}' /proc/loadavg)");
            parts.AppendLine($"        CPUS=$(nproc)");
            parts.AppendLine($"        IS_IDLE=$(awk -v s=\"$SSH_SESSIONS\" -v l=\"$LOAD\" -v c=\"$CPUS\" 'BEGIN{{print (s==0 && l/c < 0.10) ? \"yes\" : \"no\"}}')");
            parts.AppendLine($"        if [ \"$IS_IDLE\" = \"yes\" ]; then");
            parts.AppendLine($"          if [ $IDLE_SINCE -eq 0 ]; then IDLE_SINCE=$(date +%s); fi");
            parts.AppendLine($"          IDLE_MINUTES=$(( ($(date +%s) - $IDLE_SINCE) / 60 ))");
            parts.AppendLine($"          if [ $IDLE_MINUTES -ge $IDLE_THRESHOLD_MINUTES ]; then");
            parts.AppendLine($"            logger \"pks-idle-monitor: idle ${{IDLE_MINUTES}}min, shutting down\"");
            parts.AppendLine($"            shutdown -h now");
            parts.AppendLine($"          fi");
            parts.AppendLine($"        else");
            parts.AppendLine($"          IDLE_SINCE=0");
            parts.AppendLine($"        fi");
            parts.AppendLine($"      done");
            parts.AppendLine($"  - path: /etc/systemd/system/pks-idle-monitor.service");
            parts.AppendLine($"    content: |");
            parts.AppendLine($"      [Unit]");
            parts.AppendLine($"      Description=PKS Idle Monitor");
            parts.AppendLine($"      After=network.target");
            parts.AppendLine($"      [Service]");
            parts.AppendLine($"      ExecStart=/usr/local/bin/pks-idle-monitor");
            parts.AppendLine($"      Restart=always");
            parts.AppendLine($"      RestartSec=30");
            parts.AppendLine($"      [Install]");
            parts.AppendLine($"      WantedBy=multi-user.target");
        }

        parts.AppendLine("runcmd:");
        parts.AppendLine("  - curl -fsSL https://get.docker.com | sh");
        parts.AppendLine($"  - usermod -aG docker {options.AdminUsername}");
        parts.AppendLine("  - apt-get remove -y nodejs npm libnode-dev libnode72 || true");
        parts.AppendLine("  - curl -fsSL https://deb.nodesource.com/setup_20.x | bash -");
        parts.AppendLine("  - apt-get install -y nodejs");
        parts.AppendLine("  - npm install -g @devcontainers/cli");
        parts.AppendLine("  - systemctl daemon-reload");
        if (options.IdleShutdownMinutes > 0)
        {
            parts.AppendLine("  - systemctl enable pks-idle-monitor");
            parts.AppendLine("  - systemctl start pks-idle-monitor");
        }

        return parts.ToString();
    }

    public async Task SetScheduledShutdownAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string location, string vmId, string timeUtc, CancellationToken ct = default)
    {
        var timeHhmm = timeUtc.Replace(":", "");
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.DevTestLab/schedules/shutdown-computevm-{vmName}?api-version=2018-09-15";

        var body = JsonSerializer.Serialize(new
        {
            location,
            properties = new
            {
                status = "Enabled",
                taskType = "ComputeVmShutdownTask",
                dailyRecurrence = new { time = timeHhmm },
                timeZoneId = "UTC",
                targetResourceId = vmId
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _httpClient.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ARM {(int)resp.StatusCode} setting scheduled shutdown for '{vmName}': {responseBody}",
                null, resp.StatusCode);
    }

    public async Task DisableScheduledShutdownAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.DevTestLab/schedules/shutdown-computevm-{vmName}?api-version=2018-09-15";

        // We need location and vmId for the PUT body; fetch the existing schedule first
        var getReq = new HttpRequestMessage(HttpMethod.Get, url);
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var getResp = await _httpClient.SendAsync(getReq, ct);
        var getBody = await getResp.Content.ReadAsStringAsync(ct);

        string location = "eastus";
        string targetResourceId = string.Empty;
        string dailyTime = "2200";

        if (getResp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(getBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("location", out var locProp))
                location = locProp.GetString() ?? location;
            if (root.TryGetProperty("properties", out var props))
            {
                if (props.TryGetProperty("targetResourceId", out var rid))
                    targetResourceId = rid.GetString() ?? string.Empty;
                if (props.TryGetProperty("dailyRecurrence", out var daily) &&
                    daily.TryGetProperty("time", out var t))
                    dailyTime = t.GetString() ?? dailyTime;
            }
        }

        var body = JsonSerializer.Serialize(new
        {
            location,
            properties = new
            {
                status = "Disabled",
                taskType = "ComputeVmShutdownTask",
                dailyRecurrence = new { time = dailyTime },
                timeZoneId = "UTC",
                targetResourceId
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _httpClient.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ARM {(int)resp.StatusCode} disabling scheduled shutdown for '{vmName}': {responseBody}",
                null, resp.StatusCode);
    }

    public async Task SetScheduledStartAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string location, string vmId, string timeUtc, CancellationToken ct = default)
    {
        var timeHhmm = timeUtc.Replace(":", "");
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.DevTestLab/schedules/autostart-computevm-{vmName}?api-version=2018-09-15";

        var body = JsonSerializer.Serialize(new
        {
            location,
            properties = new
            {
                status = "Enabled",
                taskType = "ComputeVmStartTask",
                dailyRecurrence = new { time = timeHhmm },
                timeZoneId = "UTC",
                targetResourceId = vmId
            }
        });

        var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _httpClient.SendAsync(req, ct);
        var responseBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ARM {(int)resp.StatusCode} setting scheduled start for '{vmName}': {responseBody}",
                null, resp.StatusCode);
    }

    public async Task DisableScheduledStartAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.DevTestLab/schedules/autostart-computevm-{vmName}?api-version=2018-09-15";
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpClient.SendAsync(req, ct);
        // 404 is fine — schedule didn't exist
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"ARM {(int)resp.StatusCode} disabling scheduled start for '{vmName}': {responseBody}",
                null, resp.StatusCode);
        }
    }

    public async Task<string?> GetVmPublicIpAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        try
        {
            var ipUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/publicIPAddresses/{vmName}-ip?api-version={NetworkApiVersion}";
            var request = new HttpRequestMessage(HttpMethod.Get, ipUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var response = await _httpClient.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return null;

            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("properties", out var props) &&
                props.TryGetProperty("ipAddress", out var ipProp))
            {
                return ipProp.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get public IP for VM {VmName}", vmName);
            return null;
        }
    }

    public async Task<string?> GetVmStatusAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}/instanceView?api-version={ApiVersion}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // VM deleted

            if (!response.IsSuccessStatusCode)
                return "unknown";

            var content = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("statuses", out var statuses))
            {
                foreach (var status in statuses.EnumerateArray())
                {
                    if (status.TryGetProperty("code", out var code))
                    {
                        var codeStr = code.GetString() ?? string.Empty;
                        if (codeStr.StartsWith("PowerState/", StringComparison.OrdinalIgnoreCase))
                            return codeStr["PowerState/".Length..];
                    }
                }
            }
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    public async Task StartVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}/start?api-version={ApiVersion}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request, ct);
        // 200 or 202 are both success for async ARM operations
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"ARM {(int)response.StatusCode} starting VM: {body}");
        }
    }

    public async Task DeallocateVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}/deallocate?api-version={ApiVersion}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json");
        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"ARM {(int)response.StatusCode} deallocating VM: {body}");
        }
    }

    public async Task<bool> WaitForSshAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await tcp.ConnectAsync(host, port, connectCts.Token);
                return true;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }

        return false;
    }
}
