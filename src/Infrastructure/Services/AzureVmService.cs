using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    Task<List<AzureVmSizeInfo>> ListVmSizesAsync(string accessToken, string subscriptionId, string location, CancellationToken ct = default);
    Task<Dictionary<string, decimal>> FetchVmPricesAsync(string location, CancellationToken ct = default);
    Task<AzureResourceGroup> EnsureResourceGroupAsync(string accessToken, string subscriptionId, string name, string location, CancellationToken ct = default);
    Task<AzureVmInfo> CreateVmAsync(AzureVmCreateOptions options, Action<string>? onProgress = null, CancellationToken ct = default);
    Task<string?> GetVmPublicIpAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);

    /// <summary>
    /// Gives the VM's public IP a DNS label and returns the resulting
    /// <c>&lt;label&gt;.&lt;region&gt;.cloudapp.azure.com</c> name.
    ///
    /// Azure hands out this name for free, which is the difference between "create an A record and
    /// come back" and a command that finishes. Idempotent: an IP that already carries an fqdn keeps
    /// it, whatever the label was, because something is already pointing at it.
    /// </summary>
    Task<string?> EnsurePublicIpDnsLabelAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string desiredLabel, CancellationToken ct = default);

    /// <summary>
    /// Opens inbound TCP ports on the VM's NSG, adding a rule only if nothing already allows them.
    /// </summary>
    Task EnsureInboundPortsAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string ruleName, string[] ports, CancellationToken ct = default);
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
    Task<string?> GetOsDiskNameAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    Task DeleteVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default);
    Task DeleteResourceAsync(string accessToken, string fullyQualifiedUrl, CancellationToken ct = default);
    Task DestroyVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default);
}

/// <summary>
/// Azure VM provisioning service using Azure ARM REST API
/// </summary>
public class AzureVmService : IAzureVmService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AzureVmService> _logger;
    private readonly TimeSpan _pollInterval;
    private const string ApiVersion = "2023-09-01";
    private const string NetworkApiVersion = "2023-09-01";
    private const string DiskApiVersion = "2024-03-02";

    public AzureVmService(HttpClient httpClient, ILogger<AzureVmService> logger, TimeSpan? pollInterval = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
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
                await Task.Delay(_pollInterval, ct);
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
                        managedDisk = new { storageAccountType = "Premium_LRS" },
                        diskSizeGB = options.OsDiskSizeGb
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
            await Task.Delay(_pollInterval, ct);

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

    public async Task<string?> EnsurePublicIpDnsLabelAsync(string accessToken, string subscriptionId,
        string resourceGroup, string vmName, string desiredLabel, CancellationToken ct = default)
    {
        var ipUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
                    $"/providers/Microsoft.Network/publicIPAddresses/{vmName}-ip?api-version={NetworkApiVersion}";

        // GET → mutate → PUT, not a hand-built body. By now the IP is attached to a NIC and has an
        // allocated address; a PUT that reconstructs only the properties we care about drops the
        // ones ARM handed back, which is a 400 on a good day and a detached IP on a bad one.
        var current = await ReadJsonAsync(accessToken, ipUrl, ct);
        if (current is null)
        {
            _logger.LogWarning("Public IP {VmName}-ip not found; cannot assign a DNS label", vmName);
            return null;
        }

        var existing = current["properties"]?["dnsSettings"]?["fqdn"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;   // already named — and something may already point at it

        var label = SanitizeDnsLabel(desiredLabel);

        // The label is a global name inside the region, so it collides with strangers, not just
        // with us. Azure says DnsRecordInUse; the answer is another label, not a failure.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var candidate = attempt == 0 ? label : $"{label}-{Guid.NewGuid().ToString("N")[..5]}";
            var body = current.DeepClone()!.AsObject();
            var props = body["properties"]?.AsObject() ?? throw new InvalidOperationException("Public IP has no properties.");
            props["dnsSettings"] = new JsonObject { ["domainNameLabel"] = candidate };

            var (ok, response) = await TryPutAsync(accessToken, ipUrl, body.ToJsonString(), ct);
            if (ok)
            {
                // Read the fqdn back rather than composing it: the suffix is the region's, and
                // regions do not all spell it the way the docs' example does.
                var fqdn = JsonNode.Parse(response)?["properties"]?["dnsSettings"]?["fqdn"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(fqdn)) return fqdn;
                var refreshed = await ReadJsonAsync(accessToken, ipUrl, ct);
                return refreshed?["properties"]?["dnsSettings"]?["fqdn"]?.GetValue<string>();
            }

            if (!response.Contains("DnsRecordInUse", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException($"ARM refused the DNS label '{candidate}': {response}");

            _logger.LogInformation("DNS label {Label} is taken; retrying with a suffix", candidate);
        }

        return null;
    }

    public async Task EnsureInboundPortsAsync(string accessToken, string subscriptionId, string resourceGroup,
        string vmName, string ruleName, string[] ports, CancellationToken ct = default)
    {
        var nsgBase = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}" +
                      $"/providers/Microsoft.Network/networkSecurityGroups/{vmName}-nsg";
        var nsgUrl = $"{nsgBase}?api-version={NetworkApiVersion}";

        var nsg = await ReadJsonAsync(accessToken, nsgUrl, ct)
            ?? throw new InvalidOperationException($"Network security group '{vmName}-nsg' not found.");

        var rules = nsg["properties"]?["securityRules"]?.AsArray() ?? new JsonArray();

        // Read the existing rules for two things: whether these ports are already allowed (someone
        // may have opened them by hand, under any name), and which priorities are spoken for. Two
        // inbound rules cannot share a priority, so a fixed number would fail on exactly the boxes
        // that had been fixed manually first.
        var taken = new HashSet<int>();
        var alreadyOpen = true;
        var remaining = new HashSet<string>(ports);

        foreach (var rule in rules)
        {
            var p = rule?["properties"];
            if (p is null) continue;
            if (p["priority"] is { } pr && pr.GetValue<int>() is var prio) taken.Add(prio);

            var inbound = string.Equals(p["direction"]?.GetValue<string>(), "Inbound", StringComparison.OrdinalIgnoreCase);
            var allow = string.Equals(p["access"]?.GetValue<string>(), "Allow", StringComparison.OrdinalIgnoreCase);
            if (!inbound || !allow) continue;

            var protocol = p["protocol"]?.GetValue<string>() ?? "*";
            if (!protocol.Equals("Tcp", StringComparison.OrdinalIgnoreCase) && protocol != "*") continue;

            foreach (var declared in DeclaredPorts(p))
                remaining.RemoveWhere(want => PortRangeCovers(declared, want));
        }

        alreadyOpen = remaining.Count == 0;
        if (alreadyOpen)
        {
            _logger.LogInformation("NSG {VmName}-nsg already allows {Ports}", vmName, string.Join(",", ports));
            return;
        }

        var priority = 1010;
        while (taken.Contains(priority)) priority += 10;

        // A child-resource PUT, not a PUT of the whole NSG. Replacing the parent with a body built
        // here would delete every rule it does not restate — starting with AllowSSH, i.e. locking
        // this command out of the box it is halfway through configuring.
        var ruleUrl = $"{nsgBase}/securityRules/{ruleName}?api-version={NetworkApiVersion}";
        var payload = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["priority"] = priority,
                ["protocol"] = "Tcp",
                ["access"] = "Allow",
                ["direction"] = "Inbound",
                ["sourceAddressPrefix"] = "*",
                ["sourcePortRange"] = "*",
                ["destinationAddressPrefix"] = "*",
                ["destinationPortRanges"] = new JsonArray(ports.Select(x => (JsonNode)x!).ToArray()),
            }
        };

        var (ok, response) = await TryPutAsync(accessToken, ruleUrl, payload.ToJsonString(), ct);
        if (!ok)
            throw new HttpRequestException($"ARM refused security rule '{ruleName}': {response}");

        await WaitForResourceAsync(accessToken, ruleUrl, ruleName, ct);
    }

    private static IEnumerable<string> DeclaredPorts(JsonNode p)
    {
        if (p["destinationPortRange"]?.GetValue<string>() is { Length: > 0 } single) yield return single;
        if (p["destinationPortRanges"]?.AsArray() is { } many)
            foreach (var r in many)
                if (r?.GetValue<string>() is { Length: > 0 } v) yield return v;
    }

    /// <summary>True when an NSG port declaration ("*", "443", "80-90") covers <paramref name="want"/>.</summary>
    internal static bool PortRangeCovers(string declared, string want)
    {
        if (declared == "*") return true;
        if (!int.TryParse(want, out var w)) return false;
        var dash = declared.IndexOf('-');
        if (dash < 0) return int.TryParse(declared, out var one) && one == w;
        return int.TryParse(declared[..dash], out var lo)
            && int.TryParse(declared[(dash + 1)..], out var hi)
            && w >= lo && w <= hi;
    }

    /// <summary>
    /// Azure's grammar: lowercase alphanumerics and hyphens, must start with a letter or digit and
    /// end with one, 3–63 characters. A VM name that violates it produces a 400 whose message does
    /// not mention the VM name, so normalise here rather than explain there.
    /// </summary>
    internal static string SanitizeDnsLabel(string raw)
    {
        var chars = raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray();
        var label = new string(chars).Trim('-');
        while (label.Contains("--")) label = label.Replace("--", "-");
        if (label.Length == 0 || !char.IsLetter(label[0])) label = "t3-" + label;
        label = label.Trim('-');
        if (label.Length > 63) label = label[..63].TrimEnd('-');
        while (label.Length < 3) label += "0";
        return label;
    }

    private async Task<JsonNode?> ReadJsonAsync(string accessToken, string url, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return resp.IsSuccessStatusCode ? JsonNode.Parse(body) : null;
    }

    private async Task<(bool Ok, string Body)> TryPutAsync(string accessToken, string url, string json, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _httpClient.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return (resp.IsSuccessStatusCode, body);
    }

    private async Task WaitForResourceAsync(string accessToken, string url, string label, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            var node = await ReadJsonAsync(accessToken, url, ct);
            var state = node?["properties"]?["provisioningState"]?.GetValue<string>();
            if (string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{label} provisioning failed.");
            await Task.Delay(_pollInterval, ct);
        }
        throw new TimeoutException($"Timed out waiting for {label}.");
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

    public async Task<List<AzureVmSizeInfo>> ListVmSizesAsync(string accessToken, string subscriptionId, string location, CancellationToken ct = default)
    {
        // Use the Resource SKUs API so we can filter to only SKUs with no restrictions
        // (the /vmSizes endpoint returns all theoretically supported sizes, not actually deployable ones)
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Compute/skus?api-version=2021-07-01&$filter=location eq '{location}'";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return new List<AzureVmSizeInfo>();

        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("value", out var value)) return new List<AzureVmSizeInfo>();

        var sizes = new List<AzureVmSizeInfo>();
        foreach (var sku in value.EnumerateArray())
        {
            if (!sku.TryGetProperty("resourceType", out var rt) ||
                rt.GetString() != "virtualMachines") continue;

            if (!sku.TryGetProperty("name", out var nameProp)) continue;
            var name = nameProp.GetString() ?? "";

            // Skip SKUs with any location restriction
            if (sku.TryGetProperty("restrictions", out var restrictions) &&
                restrictions.GetArrayLength() > 0) continue;

            // Extract capabilities: vCPUs, MemoryGB, PremiumIO
            int cores = 0;
            int memMb = 0;
            bool premiumIo = false;
            if (sku.TryGetProperty("capabilities", out var caps))
            {
                foreach (var cap in caps.EnumerateArray())
                {
                    if (!cap.TryGetProperty("name", out var capName) ||
                        !cap.TryGetProperty("value", out var capValue)) continue;
                    var capStr = capName.GetString();
                    if (capStr == "vCPUs" && int.TryParse(capValue.GetString(), out var c)) cores = c;
                    if (capStr == "MemoryGB" && double.TryParse(capValue.GetString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var mem))
                        memMb = (int)(mem * 1024);
                    if (capStr == "PremiumIO") premiumIo = capValue.GetString() == "True";
                }
            }

            if (cores == 0) continue; // skip non-compute SKUs
            if (!premiumIo) continue;  // we use Premium_LRS; skip SKUs that don't support it

            sizes.Add(new AzureVmSizeInfo { Name = name, NumberOfCores = cores, MemoryInMB = memMb });
        }

        return sizes
            .OrderBy(s => s.NumberOfCores)
            .ThenBy(s => s.MemoryInMB)
            .ToList();
    }

    public async Task<Dictionary<string, decimal>> FetchVmPricesAsync(string location, CancellationToken ct = default)
    {
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var filter = Uri.EscapeDataString(
                $"serviceName eq 'Virtual Machines' and armRegionName eq '{location}' and priceType eq 'Consumption'");
            string? url = $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&$filter={filter}";
            var pages = 0;

            while (url != null && pages < 5)
            {
                var resp = await _httpClient.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(resp);

                if (doc.RootElement.TryGetProperty("Items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var armSku = item.TryGetProperty("armSkuName", out var s) ? s.GetString() : null;
                        var productName = item.TryGetProperty("productName", out var p) ? p.GetString() : null;
                        var skuName = item.TryGetProperty("skuName", out var sk) ? sk.GetString() : null;
                        var price = item.TryGetProperty("retailPrice", out var pr) ? pr.GetDecimal() : 0m;

                        if (string.IsNullOrEmpty(armSku) || price <= 0) continue;
                        if (productName?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true) continue;
                        if (skuName?.Contains("Spot", StringComparison.OrdinalIgnoreCase) == true) continue;
                        if (skuName?.Contains("Low Priority", StringComparison.OrdinalIgnoreCase) == true) continue;

                        if (!prices.ContainsKey(armSku) || prices[armSku] > price)
                            prices[armSku] = price;
                    }
                }

                url = doc.RootElement.TryGetProperty("NextPageLink", out var next)
                      && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
                pages++;
            }
        }
        catch { /* pricing is best-effort; caller shows sizes without price */ }

        return prices;
    }

    public async Task<string?> GetOsDiskNameAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}?api-version={ApiVersion}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpClient.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("storageProfile", out var storage) &&
            storage.TryGetProperty("osDisk", out var osDisk) &&
            osDisk.TryGetProperty("name", out var nameProp))
            return nameProp.GetString();
        return null;
    }

    public async Task DeleteVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, CancellationToken ct = default)
    {
        var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Compute/virtualMachines/{vmName}?api-version={ApiVersion}";
        await DeleteAndPollAsync(accessToken, url, "VM", ct);
    }

    public async Task DeleteResourceAsync(string accessToken, string fullyQualifiedUrl, CancellationToken ct = default)
    {
        await DeleteAndPollAsync(accessToken, fullyQualifiedUrl, "resource", ct);
    }

    private async Task DeleteAndPollAsync(string accessToken, string url, string label, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _httpClient.SendAsync(req, ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent ||
            resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;

        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var errBody = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"ARM {(int)resp.StatusCode} deleting {label}: {errBody}");
        }

        var asyncOpUrl = resp.Headers.TryGetValues("Azure-AsyncOperation", out var asyncOp)
            ? asyncOp.FirstOrDefault()
            : resp.Headers.TryGetValues("Location", out var loc) ? loc.FirstOrDefault() : null;

        if (asyncOpUrl == null) return;

        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(_pollInterval, ct);
            var pollReq = new HttpRequestMessage(HttpMethod.Get, asyncOpUrl);
            pollReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var pollResp = await _httpClient.SendAsync(pollReq, ct);
            var pollBody = await pollResp.Content.ReadAsStringAsync(ct);

            if (!pollResp.IsSuccessStatusCode) continue;

            string? status = null;
            try
            {
                using var doc = JsonDocument.Parse(pollBody);
                if (doc.RootElement.TryGetProperty("status", out var s))
                    status = s.GetString();
            }
            catch { }

            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Delete of {label} failed: {pollBody}");
        }
        throw new TimeoutException($"Timed out waiting for {label} delete to complete.");
    }

    public async Task DestroyVmAsync(string accessToken, string subscriptionId, string resourceGroup, string vmName, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        string sub = subscriptionId, rg = resourceGroup;

        onProgress?.Invoke("Checking VM power state...");
        var powerState = await GetVmStatusAsync(accessToken, sub, rg, vmName, ct);
        if (powerState == "running")
        {
            onProgress?.Invoke("Deallocating VM...");
            await DeallocateVmAsync(accessToken, sub, rg, vmName, ct);
            var deallocDeadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deallocDeadline)
            {
                await Task.Delay(_pollInterval, ct);
                var s = await GetVmStatusAsync(accessToken, sub, rg, vmName, ct);
                if (s == "deallocated" || s == null) break;
            }
        }

        onProgress?.Invoke("Getting OS disk name...");
        var osDiskName = await GetOsDiskNameAsync(accessToken, sub, rg, vmName, ct);

        onProgress?.Invoke("Deleting VM...");
        await DeleteVmAsync(accessToken, sub, rg, vmName, ct);

        onProgress?.Invoke("Deleting NIC...");
        var nicUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/networkInterfaces/{vmName}-nic?api-version={NetworkApiVersion}";
        await DeleteResourceAsync(accessToken, nicUrl, ct);

        onProgress?.Invoke("Deleting public IP...");
        var ipUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/publicIPAddresses/{vmName}-ip?api-version={NetworkApiVersion}";
        await DeleteResourceAsync(accessToken, ipUrl, ct);

        if (!string.IsNullOrEmpty(osDiskName))
        {
            onProgress?.Invoke("Deleting OS disk...");
            var diskUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Compute/disks/{osDiskName}?api-version={DiskApiVersion}";
            await DeleteResourceAsync(accessToken, diskUrl, ct);
        }

        onProgress?.Invoke("Deleting NSG...");
        try
        {
            var nsgUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/networkSecurityGroups/{vmName}-nsg?api-version={NetworkApiVersion}";
            await DeleteResourceAsync(accessToken, nsgUrl, ct);
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Warning: Could not delete NSG (may still be in use): {ex.Message}");
        }

        onProgress?.Invoke("Deleting VNet...");
        try
        {
            var vnetUrl = $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.Network/virtualNetworks/{vmName}-vnet?api-version={NetworkApiVersion}";
            await DeleteResourceAsync(accessToken, vnetUrl, ct);
        }
        catch (Exception ex)
        {
            onProgress?.Invoke($"Warning: Could not delete VNet: {ex.Message}");
        }

        onProgress?.Invoke($"VM '{vmName}' destroyed successfully.");
    }
}
