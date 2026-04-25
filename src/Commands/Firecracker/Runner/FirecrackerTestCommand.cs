using System.ComponentModel;
using System.Diagnostics;
using PKS.Commands.Firecracker;
using PKS.Infrastructure.Services.Firecracker;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Firecracker.Runner;

/// <summary>
/// Boots a Firecracker microVM, runs a series of smoke tests inside it, and reports results.
/// Meant for verifying the Firecracker setup works end-to-end on a server.
/// </summary>
public class FirecrackerTestCommand : Command<FirecrackerTestCommand.Settings>
{
    private readonly IFirecrackerService _firecrackerService;
    private readonly IFirecrackerRunnerConfigurationService _configService;
    private readonly FirecrackerNetworkManager _networkManager;
    private readonly IAnsiConsole _console;

    public class Settings : FirecrackerSettings
    {
        [CommandOption("--vcpus <COUNT>")]
        [Description("Override vCPU count for test VM (default: from config)")]
        public int? Vcpus { get; set; }

        [CommandOption("--mem-mib <SIZE>")]
        [Description("Override memory for test VM (default: from config)")]
        public int? MemMib { get; set; }

        [CommandOption("--keep-vm")]
        [Description("Don't clean up the VM after testing (for debugging)")]
        public bool KeepVm { get; set; }
    }

    public FirecrackerTestCommand(
        IFirecrackerService firecrackerService,
        IFirecrackerRunnerConfigurationService configService,
        FirecrackerNetworkManager networkManager,
        IAnsiConsole console)
    {
        _firecrackerService = firecrackerService ?? throw new ArgumentNullException(nameof(firecrackerService));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // Display banner
        var banner = new Panel("[bold cyan]Firecracker VM Test[/]")
            .BorderStyle(Style.Parse("cyan"))
            .Padding(1, 0);
        _console.Write(banner);
        _console.WriteLine();

        // Check prerequisites
        var (kvmAvailable, firecrackerInstalled, firecrackerVersion) =
            await _firecrackerService.CheckPrerequisitesAsync();

        if (!kvmAvailable)
        {
            _console.MarkupLine("[red]KVM is not available. Firecracker requires KVM support.[/]");
            return 1;
        }

        if (!firecrackerInstalled)
        {
            _console.MarkupLine("[red]Firecracker is not installed. Install it from https://github.com/firecracker-microvm/firecracker/releases[/]");
            return 1;
        }

        _console.MarkupLine($"[green]Prerequisites OK[/] [dim](Firecracker {firecrackerVersion.EscapeMarkup()})[/]");
        _console.WriteLine();

        // Load config and validate kernel/rootfs
        var config = await _configService.LoadAsync();
        var defaults = config.Defaults;

        if (string.IsNullOrEmpty(defaults.KernelPath) || !File.Exists(defaults.KernelPath))
        {
            _console.MarkupLine("[red]Kernel not found. Run 'pks firecracker init' first.[/]");
            return 1;
        }

        if (string.IsNullOrEmpty(defaults.BaseRootfsPath) || !File.Exists(defaults.BaseRootfsPath))
        {
            _console.MarkupLine("[red]Rootfs not found. Run 'pks firecracker init' first.[/]");
            return 1;
        }

        // Run test sequence
        var vmId = $"fc-test-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var results = new List<(string Test, bool Passed, string Detail)>();
        var stopwatch = Stopwatch.StartNew();

        var sshKeyPath = Path.Combine(defaults.WorkDir, "vm-key");
        string vmIp = "";
        string rootfsPath = "";

        // Test: Allocate Network
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Allocating network...", async _ =>
            {
                try
                {
                    var (tapDevice, ip, gatewayIp, macAddress) =
                        await _networkManager.AllocateNetworkAsync(vmId, defaults.WorkDir, defaults.NetworkSubnet);
                    vmIp = ip;
                    results.Add(("Allocate Network", true, $"tap={tapDevice}, vm={ip}, gw={gatewayIp}"));
                }
                catch (Exception ex)
                {
                    results.Add(("Allocate Network", false, ex.Message));
                }
            });

        if (!results.Last().Passed)
        {
            DisplayResults(results, stopwatch);
            return 1;
        }

        // Test: Prepare Rootfs
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing rootfs...", async _ =>
            {
                try
                {
                    rootfsPath = await _firecrackerService.PrepareRootfsAsync(
                        defaults.BaseRootfsPath, vmId, defaults.WorkDir);
                    results.Add(("Prepare Rootfs", true, Path.GetFileName(rootfsPath)));
                }
                catch (Exception ex)
                {
                    results.Add(("Prepare Rootfs", false, ex.Message));
                }
            });

        if (!results.Last().Passed)
        {
            DisplayResults(results, stopwatch);
            await CleanupAsync(vmId, defaults, settings);
            return 1;
        }

        // Retrieve the network allocation for VM config
        var allocation = await _networkManager.GetAllocationAsync(vmId, defaults.WorkDir);
        if (allocation == null)
        {
            results.Add(("Boot VM", false, "Network allocation lost"));
            DisplayResults(results, stopwatch);
            await CleanupAsync(vmId, defaults, settings);
            return 1;
        }

        var (tap, allocVmIp, allocGwIp, allocMac) = allocation.Value;

        // Test: Boot VM
        var bootStopwatch = Stopwatch.StartNew();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Booting VM...", async _ =>
            {
                try
                {
                    var vmConfig = new FirecrackerVmConfig
                    {
                        VcpuCount = settings.Vcpus ?? defaults.DefaultVcpus,
                        MemSizeMib = settings.MemMib ?? defaults.DefaultMemMib,
                        KernelPath = defaults.KernelPath,
                        RootfsPath = rootfsPath,
                        TapDevice = tap,
                        VmIpAddress = allocVmIp,
                        GatewayIp = allocGwIp,
                        MacAddress = allocMac,
                        SocketPath = Path.Combine(defaults.WorkDir, "vms", vmId, "firecracker.sock")
                    };

                    var vmState = await _firecrackerService.BootVmAsync(vmConfig);
                    bootStopwatch.Stop();
                    results.Add(("Boot VM", true, $"PID={vmState.ProcessId}, boot={bootStopwatch.ElapsedMilliseconds}ms"));
                }
                catch (Exception ex)
                {
                    bootStopwatch.Stop();
                    results.Add(("Boot VM", false, ex.Message));
                }
            });

        if (!results.Last().Passed)
        {
            DisplayResults(results, stopwatch);
            await CleanupAsync(vmId, defaults, settings);
            return 1;
        }

        // Test: SSH Connectivity
        var sshStopwatch = Stopwatch.StartNew();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for SSH connectivity...", async _ =>
            {
                try
                {
                    var ready = false;
                    for (int i = 0; i < 30; i++)
                    {
                        try
                        {
                            var result = await _firecrackerService.ExecuteInVmAsync(
                                vmIp, sshKeyPath, "echo pks-ready", 5);
                            if (result.ExitCode == 0 && result.StandardOutput.Contains("pks-ready"))
                            {
                                ready = true;
                                break;
                            }
                        }
                        catch
                        {
                            // SSH not ready yet, retry
                        }

                        await Task.Delay(1000);
                    }

                    sshStopwatch.Stop();
                    if (ready)
                    {
                        results.Add(("SSH Connectivity", true, $"Ready in {sshStopwatch.ElapsedMilliseconds}ms"));
                    }
                    else
                    {
                        results.Add(("SSH Connectivity", false, $"Timed out after {sshStopwatch.ElapsedMilliseconds}ms"));
                    }
                }
                catch (Exception ex)
                {
                    sshStopwatch.Stop();
                    results.Add(("SSH Connectivity", false, ex.Message));
                }
            });

        if (!results.Last().Passed)
        {
            DisplayResults(results, stopwatch);
            await CleanupAsync(vmId, defaults, settings);
            return 1;
        }

        // Test: Kernel Version
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking kernel version...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(vmIp, sshKeyPath, "uname -r");
                results.Add(("Kernel Version", passed, detail));
            });

        // Test: Network (outbound)
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing outbound network...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(vmIp, sshKeyPath, "ping -c 1 -W 3 8.8.8.8");
                results.Add(("Network (outbound)", passed, detail));
            });

        // Test: DNS Resolution
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Testing DNS resolution...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(
                    vmIp, sshKeyPath,
                    "host google.com || nslookup google.com || echo dns-failed");
                // If output contains "dns-failed" as the only meaningful output, mark as failed
                if (passed && detail.Contains("dns-failed") && !detail.Contains("has address"))
                {
                    results.Add(("DNS Resolution", false, detail));
                }
                else
                {
                    results.Add(("DNS Resolution", passed, detail));
                }
            });

        // Test: Docker Available
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking Docker availability...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(vmIp, sshKeyPath, "docker --version");
                results.Add(("Docker Available", passed, detail));
            });

        // Test: Disk Space
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking disk space...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(vmIp, sshKeyPath, "df -h /");
                results.Add(("Disk Space", passed, detail));
            });

        // Test: Memory Info
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking memory info...", async _ =>
            {
                var (passed, detail) = await RunTestAsync(vmIp, sshKeyPath, "free -m");
                results.Add(("Memory Info", passed, detail));
            });

        stopwatch.Stop();

        // Display results
        _console.WriteLine();
        DisplayResults(results, stopwatch);

        // Cleanup
        if (!settings.KeepVm)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Cleaning up test VM...", async _ =>
                {
                    try { await _firecrackerService.StopVmAsync(vmId, defaults.WorkDir); } catch { }
                    try { await _networkManager.ReleaseNetworkAsync(vmId, defaults.WorkDir); } catch { }
                    try { await _firecrackerService.CleanupVmAsync(vmId, defaults.WorkDir); } catch { }
                });
            _console.MarkupLine("[dim]Test VM cleaned up[/]");
        }
        else
        {
            _console.MarkupLine($"[yellow]VM kept alive:[/] {vmId} at {vmIp}");
            _console.MarkupLine($"[dim]SSH: ssh -i {sshKeyPath} root@{vmIp}[/]");
            _console.MarkupLine($"[dim]Cleanup: pks firecracker cleanup {vmId}[/]");
        }

        var passed_count = results.Count(r => r.Passed);
        return passed_count == results.Count ? 0 : 1;
    }

    /// <summary>
    /// Executes a command inside the VM over SSH and returns a pass/fail result with detail.
    /// </summary>
    private async Task<(bool Passed, string Detail)> RunTestAsync(
        string vmIp, string sshKeyPath, string command, string successPattern = "")
    {
        try
        {
            var result = await _firecrackerService.ExecuteInVmAsync(vmIp, sshKeyPath, command, 30);
            if (result.ExitCode == 0)
            {
                var output = result.StandardOutput.Trim();
                if (output.Length > 80) output = output[..80] + "...";
                return (true, string.IsNullOrEmpty(output) ? "OK" : output);
            }
            return (false, $"Exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Displays the test results table and summary line.
    /// </summary>
    private void DisplayResults(List<(string Test, bool Passed, string Detail)> results, Stopwatch stopwatch)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Test[/]")
            .AddColumn("[bold]Result[/]")
            .AddColumn("[bold]Detail[/]");

        foreach (var (test, passed, detail) in results)
        {
            var status = passed ? "[green]PASS[/]" : "[red]FAIL[/]";
            table.AddRow(test, status, detail.EscapeMarkup());
        }

        _console.Write(table);

        var passedCount = results.Count(r => r.Passed);
        var total = results.Count;
        var elapsed = stopwatch.Elapsed;
        _console.MarkupLine($"\n[bold]{passedCount}/{total} tests passed[/] in {elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Best-effort cleanup of the test VM resources.
    /// </summary>
    private async Task CleanupAsync(string vmId, FirecrackerDefaults defaults, Settings settings)
    {
        if (settings.KeepVm)
            return;

        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Cleaning up test VM...", async _ =>
            {
                try { await _firecrackerService.StopVmAsync(vmId, defaults.WorkDir); } catch { }
                try { await _networkManager.ReleaseNetworkAsync(vmId, defaults.WorkDir); } catch { }
                try { await _firecrackerService.CleanupVmAsync(vmId, defaults.WorkDir); } catch { }
            });
        _console.MarkupLine("[dim]Test VM cleaned up[/]");
    }
}
