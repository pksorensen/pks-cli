using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PKS.Commands.Firecracker;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Firecracker.Runner;

/// <summary>
/// Polls the agentics.dk server for alp_runner_spawn jobs and executes them
/// by booting Firecracker microVMs.
/// </summary>
public class FirecrackerRunnerStartCommand : Command<FirecrackerRunnerStartCommand.Settings>
{
    private readonly IFirecrackerRunnerConfigurationService _configService;
    private readonly IFirecrackerService _firecrackerService;
    private readonly FirecrackerNetworkManager _networkManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Track active VMs for cleanup on shutdown
    private readonly List<ActiveVmContext> _activeVms = new();
    private readonly object _activeVmsLock = new();

    private record ActiveVmContext(string VmId, string WorkDir);

    public class Settings : FirecrackerRunnerSettings
    {
        [CommandOption("--server <SERVER>")]
        [Description("Agentics server URL (falls back to AGENTIC_SERVER env, then agentics.dk)")]
        public string? Server { get; set; }

        [CommandOption("--project <OWNER_PROJECT>")]
        [Description("Project in owner/project format. Auto-registers if not already registered.")]
        public string? Project { get; set; }

        [CommandOption("--polling-interval <SECONDS>")]
        [Description("Polling interval in seconds (default: 10)")]
        [DefaultValue(10)]
        public int PollingInterval { get; set; } = 10;

        [CommandOption("--max-concurrent-vms <COUNT>")]
        [Description("Maximum concurrent VMs (default: 5)")]
        [DefaultValue(5)]
        public int MaxConcurrentVms { get; set; } = 5;
    }

    public FirecrackerRunnerStartCommand(
        IFirecrackerRunnerConfigurationService configService,
        IFirecrackerService firecrackerService,
        FirecrackerNetworkManager networkManager,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _firecrackerService = firecrackerService ?? throw new ArgumentNullException(nameof(firecrackerService));
        _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            DisplayBanner();

            // Resolve registration: --project auto-registers if needed, otherwise use first saved
            FirecrackerRunnerRegistration registration;
            if (!string.IsNullOrEmpty(settings.Project))
            {
                registration = await ResolveOrRegisterAsync(settings.Project, settings.Server, settings.Verbose);
            }
            else
            {
                var registrations = await _configService.ListRegistrationsAsync();
                if (registrations.Count == 0)
                {
                    DisplayError("No runner registrations found. Use --project owner/project to auto-register, or run 'pks firecracker runner register <owner/project>' first.");
                    return 1;
                }
                registration = registrations[0];
            }

            if (settings.Verbose)
            {
                DisplayInfo($"Using registration: {registration.Name} ({registration.Id})");
                DisplayInfo($"Owner: {registration.Owner}/{registration.Project}");
                DisplayInfo($"Server: {registration.Server}");
                DisplayInfo($"Polling interval: {settings.PollingInterval}s");
                DisplayInfo($"Max concurrent VMs: {settings.MaxConcurrentVms}");
            }

            // Load Firecracker config for defaults
            var fcConfig = await _configService.LoadAsync();
            if (string.IsNullOrEmpty(fcConfig.Defaults.KernelPath) || string.IsNullOrEmpty(fcConfig.Defaults.BaseRootfsPath))
            {
                _console.MarkupLine("[red]Firecracker not initialized. Run 'pks firecracker init' first.[/]");
                return 1;
            }

            _console.WriteLine();
            DisplayInfo($"Starting Firecracker runner daemon for [cyan]{registration.Owner}/{registration.Project}[/]");
            DisplayInfo("Press Ctrl+C to stop.");
            _console.WriteLine();

            // Set up cancellation (handle both SIGINT via Ctrl+C and SIGTERM via process exit)
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                DisplayInfo("Shutdown requested (SIGINT)...");
                cts.Cancel();
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                if (!cts.IsCancellationRequested)
                {
                    DisplayInfo("Process exit signal received (SIGTERM)...");
                    cts.Cancel();
                    // Block briefly to allow cleanup to run
                    CleanupAllActiveVmsAsync(fcConfig.Defaults).GetAwaiter().GetResult();
                }
            };

            // Capabilities
            var capabilities = new List<string> { "alp_runner_spawn" };
            _console.MarkupLine($"[dim]Runner capabilities: {string.Join(", ", capabilities)}[/]");

            // Polling loop
            var jobsProcessed = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var job = await PollForJobAsync(registration, capabilities, cts.Token);
                    if (job != null)
                    {
                        _console.MarkupLine($"[green]Job received:[/] {job.Id}");
                        await ExecuteFirecrackerJobAsync(registration, job, fcConfig.Defaults, settings, cts.Token);
                        jobsProcessed++;
                    }
                    else if (settings.Verbose)
                    {
                        _console.MarkupLine($"[dim]{DateTime.UtcNow:HH:mm:ss} No jobs, waiting {settings.PollingInterval}s...[/]");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _console.MarkupLine($"[red]Error:[/] {ex.Message.EscapeMarkup()}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollingInterval), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _console.WriteLine();

            // Clean up all active VMs on shutdown
            await CleanupAllActiveVmsAsync(fcConfig.Defaults);

            DisplaySuccess($"Runner daemon stopped. Jobs processed: {jobsProcessed}");
            return 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Runner daemon failed: {ex.Message}");
            if (settings.Verbose)
                _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Polls the server for available jobs matching the given capabilities.
    /// </summary>
    private async Task<FirecrackerJob?> PollForJobAsync(
        FirecrackerRunnerRegistration registration,
        IReadOnlyList<string> capabilities,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);

        var response = await client.PostAsJsonAsync(
            $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}/runners/jobs",
            new { capabilities }, ct);

        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return null;

        var pollResponse = JsonSerializer.Deserialize<PollResponse>(json, JsonOptions);
        if (pollResponse?.Jobs == null || pollResponse.Jobs.Count == 0)
            return null;

        return pollResponse.Jobs[0];
    }

    /// <summary>
    /// Finds an existing local registration for the given owner/project, or auto-registers
    /// against the server and saves it. This lets 'start --project owner/proj' be self-contained.
    /// </summary>
    private async Task<FirecrackerRunnerRegistration> ResolveOrRegisterAsync(
        string ownerProject, string? serverOverride, bool verbose)
    {
        var parts = ownerProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new InvalidOperationException($"--project must be in owner/project format, got: '{ownerProject}'");

        var owner = parts[0];
        var project = parts[1];

        // Check if we already have a saved registration for this project
        var registrations = await _configService.ListRegistrationsAsync();
        var existing = registrations.FirstOrDefault(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Project, project, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            // If --server was explicitly provided and differs from the stored URL, update it
            if (!string.IsNullOrEmpty(serverOverride))
            {
                var normalizedServer = serverOverride.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                       serverOverride.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? serverOverride.TrimEnd('/')
                    : $"http://{serverOverride}";

                if (!string.Equals(existing.Server, normalizedServer, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Server = normalizedServer;
                    await _configService.AddRegistrationAsync(existing);
                    DisplayInfo($"Updated server URL for {owner}/{project}: {normalizedServer}");
                }
            }

            if (verbose)
                DisplayInfo($"Found existing registration for {owner}/{project} (id: {existing.Id})");
            return existing;
        }

        // No saved registration — auto-register
        DisplayInfo($"No saved registration for [cyan]{owner}/{project}[/], registering now...");

        var serverHost = serverOverride
            ?? Environment.GetEnvironmentVariable("AGENTICS_SERVER")
            ?? Environment.GetEnvironmentVariable("AGENTIC_SERVER")
            ?? "agentics.dk";

        string serverUrl;
        if (serverHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            serverHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = serverHost.TrimEnd('/');
        }
        else
        {
            var scheme = serverHost.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                         serverHost.StartsWith("127.0.0.1")
                ? "http"
                : "https";
            serverUrl = $"{scheme}://{serverHost}";
        }

        var runnerName = System.Net.Dns.GetHostName();

        using var httpClient = _httpClientFactory.CreateClient();
        var requestBody = new { name = runnerName, labels = Array.Empty<string>() };
        var httpResponse = await httpClient.PostAsJsonAsync(
            $"{serverUrl}/api/owners/{owner}/projects/{project}/runners",
            requestBody);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Auto-registration failed ({(int)httpResponse.StatusCode}): {errorBody}");
        }

        var respJson = await httpResponse.Content.ReadAsStringAsync();
        var resp = JsonSerializer.Deserialize<RegisterRunnerResponse>(respJson, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse registration response");

        var registration = new FirecrackerRunnerRegistration
        {
            Id = resp.Id ?? Guid.NewGuid().ToString(),
            Name = resp.Name ?? runnerName,
            Token = resp.Token ?? "",
            Owner = owner,
            Project = project,
            Server = serverUrl,
            RegisteredAt = DateTime.UtcNow
        };

        await _configService.AddRegistrationAsync(registration);
        DisplayInfo($"[green]Registered runner '{registration.Name}' for {owner}/{project}[/]");
        return registration;
    }

    /// <summary>
    /// Executes a Firecracker job: claims it, boots a microVM, runs the command, and reports results.
    /// </summary>
    private async Task ExecuteFirecrackerJobAsync(
        FirecrackerRunnerRegistration registration,
        FirecrackerJob job,
        FirecrackerDefaults defaults,
        Settings settings,
        CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
        var baseUrl = $"{registration.Server}/api/owners/{registration.Owner}/projects/{registration.Project}";

        var vmId = $"fc-{job.Id[..8]}";

        // Track this VM for shutdown cleanup
        lock (_activeVmsLock)
        {
            _activeVms.Add(new ActiveVmContext(vmId, defaults.WorkDir));
        }

        try
        {
            // 1. Claim job
            var claimResp = await client.PostAsJsonAsync($"{baseUrl}/runners/generate-jitconfig",
                new { jobId = job.Id, name = registration.Name ?? "fc-runner" }, ct);
            if (!claimResp.IsSuccessStatusCode)
            {
                _console.MarkupLine($"[yellow]Could not claim job {job.Id}, skipping.[/]");
                return;
            }
            var claimJson = await claimResp.Content.ReadAsStringAsync(ct);
            var claimData = JsonSerializer.Deserialize<JsonElement>(claimJson, JsonOptions);
            var runId = claimData.GetProperty("runId").GetString()!;

            // 2. Allocate network
            var (tapDevice, vmIp, gatewayIp, macAddress) = await _networkManager.AllocateNetworkAsync(
                vmId, defaults.WorkDir, defaults.NetworkSubnet, ct);

            // 3. Prepare rootfs
            var rootfsPath = await _firecrackerService.PrepareRootfsAsync(
                defaults.BaseRootfsPath, vmId, defaults.WorkDir, ct);

            // 4. Build VM config
            var vmConfig = new FirecrackerVmConfig
            {
                VcpuCount = job.VcpuCount > 0 ? job.VcpuCount : defaults.DefaultVcpus,
                MemSizeMib = job.MemMib > 0 ? job.MemMib : defaults.DefaultMemMib,
                KernelPath = defaults.KernelPath,
                RootfsPath = rootfsPath,
                TapDevice = tapDevice,
                VmIpAddress = vmIp,
                GatewayIp = gatewayIp,
                MacAddress = macAddress,
                SocketPath = Path.Combine(defaults.WorkDir, "vms", vmId, "firecracker.sock")
            };

            // 5. Boot VM
            _console.MarkupLine($"[cyan]Booting VM {vmId} ({vmConfig.VcpuCount} vCPUs, {vmConfig.MemSizeMib}MiB)...[/]");
            var vmState = await _firecrackerService.BootVmAsync(vmConfig, ct);

            // 6. PATCH in_progress
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "in_progress", null, ct);

            // 7. Wait for SSH ready
            _console.MarkupLine($"[dim]Waiting for SSH on {vmIp}...[/]");
            var sshKeyPath = Path.Combine(defaults.WorkDir, "vm-key");
            var ready = false;
            for (int i = 0; i < 30; i++)
            {
                var check = await _firecrackerService.ExecuteInVmAsync(vmIp, sshKeyPath, "echo ready", 5, ct);
                if (check.ExitCode == 0 && check.StandardOutput.Contains("ready"))
                {
                    ready = true;
                    break;
                }
                await Task.Delay(1000, ct);
            }

            if (!ready)
            {
                _console.MarkupLine("[red]VM failed to become SSH-ready[/]");
                await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", "failure", ct);
                return;
            }

            // 8. Execute command
            _console.MarkupLine($"[cyan]Executing command in VM...[/]");
            var result = await _firecrackerService.ExecuteInVmAsync(vmIp, sshKeyPath, job.Command, 3600, ct);

            // 9. Report result
            var conclusion = result.ExitCode == 0 ? "success" : "failure";
            await PatchJobStatusAsync(client, baseUrl, runId, job.Id, "completed", conclusion, ct);

            _console.MarkupLine(conclusion == "success"
                ? $"[green]Job {job.Id} completed successfully[/]"
                : $"[red]Job {job.Id} failed (exit code {result.ExitCode})[/]");
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Job {job.Id} error: {ex.Message.EscapeMarkup()}[/]");
            // Try to report failure (best-effort)
            try
            {
                using var errClient = _httpClientFactory.CreateClient();
                errClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registration.Token);
            }
            catch { }
        }
        finally
        {
            // Always cleanup
            try { await _firecrackerService.StopVmAsync(vmId, defaults.WorkDir, ct); } catch { }
            try { await _networkManager.ReleaseNetworkAsync(vmId, defaults.WorkDir, ct); } catch { }
            try { await _firecrackerService.CleanupVmAsync(vmId, defaults.WorkDir, ct); } catch { }

            // Remove from active tracking
            lock (_activeVmsLock)
            {
                _activeVms.RemoveAll(v => v.VmId == vmId);
            }
        }
    }

    /// <summary>
    /// Updates the status of a job run on the server.
    /// </summary>
    private async Task PatchJobStatusAsync(HttpClient client, string baseUrl, string runId, string jobId,
        string status, string? conclusion, CancellationToken ct)
    {
        var msg = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/runs/{runId}/jobs/{jobId}");
        msg.Content = JsonContent.Create(new { status, conclusion });
        await client.SendAsync(msg, ct);
    }

    /// <summary>
    /// Stops and cleans up all active VMs (used during shutdown).
    /// </summary>
    private async Task CleanupAllActiveVmsAsync(FirecrackerDefaults defaults)
    {
        List<ActiveVmContext> vmsToClean;
        lock (_activeVmsLock)
        {
            vmsToClean = new List<ActiveVmContext>(_activeVms);
            _activeVms.Clear();
        }

        if (vmsToClean.Count == 0)
            return;

        _console.MarkupLine($"[yellow]Cleaning up {vmsToClean.Count} active VM(s)...[/]");

        foreach (var vm in vmsToClean)
        {
            try
            {
                await _firecrackerService.StopVmAsync(vm.VmId, vm.WorkDir);
            }
            catch { }
            try
            {
                await _networkManager.ReleaseNetworkAsync(vm.VmId, vm.WorkDir);
            }
            catch { }
            try
            {
                await _firecrackerService.CleanupVmAsync(vm.VmId, vm.WorkDir);
            }
            catch { }
        }

        _console.MarkupLine("[green]VM cleanup complete.[/]");
    }

    private void DisplayBanner()
    {
        var panel = new Panel("[bold cyan]Firecracker MicroVM Runner[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(panel);
        _console.WriteLine();
    }

    private void DisplayInfo(string message) =>
        _console.MarkupLine($"[cyan]>[/] {message}");

    private void DisplayError(string message) =>
        _console.MarkupLine($"[red]Error:[/] {message.EscapeMarkup()}");

    private void DisplaySuccess(string message) =>
        _console.MarkupLine($"[green]{message}[/]");

    /// <summary>Response wrapper for the /runners/jobs poll endpoint.</summary>
    private class PollResponse
    {
        public List<FirecrackerJob> Jobs { get; set; } = new();
    }

    /// <summary>Minimal job model returned by the server's /runners/jobs endpoint.</summary>
    private class FirecrackerJob
    {
        public string Id { get; set; } = "";
        public string Command { get; set; } = "";
        public int MemMib { get; set; }
        public int VcpuCount { get; set; }
        public string? JobType { get; set; }
    }

    /// <summary>Response from the runner auto-registration endpoint.</summary>
    private class RegisterRunnerResponse
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Token { get; set; }
    }
}
