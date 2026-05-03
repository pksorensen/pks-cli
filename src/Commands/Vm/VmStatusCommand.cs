using System.ComponentModel;
using System.Text;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Show VM status with disk/memory/docker stats and an action menu")]
public class VmStatusCommand : Command<VmStatusCommand.Settings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshExecutor _sshExecutor;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAnsiConsole _console;

    public VmStatusCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmMetadataService vmMetadata,
        ISshExecutor sshExecutor,
        ISshTargetConfigurationService sshTargets,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _vmMetadata = vmMetadata;
        _sshExecutor = sshExecutor;
        _sshTargets = sshTargets;
        _console = console;
    }

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(Settings settings)
    {
        var vms = await _vmMetadata.ListAsync();
        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return 0;
        }

        AzureVmRecord record;
        if (vms.Count == 1)
        {
            record = vms[0];
        }
        else
        {
            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Pick a VM:[/]")
                    .AddChoices(vms.Select(v => v.VmName)));
            record = vms.First(v => v.VmName == choice);
        }

        return await ShowVmStatusAsync(record);
    }

    public async Task<int> ShowVmStatusAsync(AzureVmRecord record)
    {
        string? token = null;
        if (await _azureAuth.IsAuthenticatedAsync())
            token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");

        string? powerState = null;
        if (token != null)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Checking VM status...", async _ =>
                {
                    powerState = await _vmService.GetVmStatusAsync(
                        token, record.SubscriptionId, record.ResourceGroup, record.VmName);
                });
        }

        var powerDisplay = powerState switch
        {
            "running" => "[green]running[/]",
            "deallocated" => "[dim]deallocated[/]",
            "stopped" => "[yellow]stopped[/]",
            "starting" => "[cyan]starting[/]",
            "stopping" => "[yellow]stopping[/]",
            null => "[dim]unknown[/]",
            _ => $"[dim]{Markup.Escape(powerState)}[/]"
        };

        var idleDisplay = record.IdleShutdownMinutes > 0 ? $"{record.IdleShutdownMinutes} min" : "disabled";
        var scheduledDisplay = record.ScheduledShutdownUtc != null
            ? $"{Markup.Escape(record.ScheduledShutdownUtc)} UTC"
            : "none";
        var diskDisplay = record.OsDiskSizeGb > 0 ? $"{record.OsDiskSizeGb} GB" : "unknown";

        _console.Write(new Panel(
            $"[cyan1]VM Name:[/]       {Markup.Escape(record.VmName)}\n" +
            $"[cyan1]Resource Group:[/] {Markup.Escape(record.ResourceGroup)}\n" +
            $"[cyan1]Public IP:[/]     {Markup.Escape(record.PublicIpAddress)}\n" +
            $"[cyan1]VM Size:[/]       {Markup.Escape(record.VmSize)}\n" +
            $"[cyan1]Location:[/]      {Markup.Escape(record.Location)}\n" +
            $"[cyan1]OS Disk:[/]       {diskDisplay}\n" +
            $"[cyan1]Idle Shutdown:[/] {idleDisplay}\n" +
            $"[cyan1]Scheduled:[/]     {scheduledDisplay}\n" +
            $"[cyan1]Power State:[/]   {powerDisplay}")
            .Border(BoxBorder.Rounded)
            .BorderStyle("cyan")
            .Header($" [bold cyan]{Markup.Escape(record.VmName)}[/] "));

        int? diskPct = null;
        if (powerState == "running" && !string.IsNullOrEmpty(record.PublicIpAddress))
        {
            SshResult? sshResult = null;
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching VM stats...", async _ =>
                {
                    var target = new SshTarget
                    {
                        Host = record.PublicIpAddress,
                        Port = 22,
                        Username = "azureuser",
                        KeyPath = record.SshKeyPath
                    };
                    const string statsCmd =
                        "echo '__DISK__'; df -h /; echo '__MEM__'; free -m; " +
                        "echo '__DOCKER__'; docker info --format 'Containers: {{.Containers}} ({{.ContainersRunning}} running) | Images: {{.Images}} | Server: {{.ServerVersion}}' 2>&1 | head -1; " +
                        "echo '__SPACE__'; df --output=pcent / | tail -1 | tr -d ' %'; " +
                        "echo '__UPTIME__'; uptime -p";
                    sshResult = await _sshExecutor.RunAsync(target, statsCmd, TimeSpan.FromSeconds(30));
                });

            if (sshResult != null && !sshResult.TimedOut && sshResult.ExitCode == 0)
                diskPct = ParseAndRenderStats(sshResult.Stdout);
        }

        // Build action menu based on current state
        var actions = new List<string>();
        if (powerState == "running") actions.Add("Reconnect (ssh)");
        if (diskPct.HasValue && diskPct.Value > 70)
            actions.Add("Free disk space (docker system prune -af --volumes)");
        if (powerState is "deallocated" or "stopped") actions.Add("Start VM");
        if (powerState == "running") actions.Add("Stop / Deallocate VM");
        actions.Add("Destroy VM (delete all resources)");
        actions.Add("Quit");

        var action = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Action:[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(actions));

        return await HandleActionAsync(action, record, token);
    }

    private int? ParseAndRenderStats(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentSection = null;
        var sb = new StringBuilder();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("__") && trimmed.EndsWith("__") && trimmed.Length > 4)
            {
                if (currentSection != null) sections[currentSection] = sb.ToString().Trim();
                currentSection = trimmed.Trim('_');
                sb.Clear();
            }
            else if (currentSection != null)
            {
                sb.AppendLine(line);
            }
        }
        if (currentSection != null) sections[currentSection] = sb.ToString().Trim();

        int? diskPct = null;
        if (sections.TryGetValue("SPACE", out var spaceStr) && int.TryParse(spaceStr.Trim(), out var pct))
            diskPct = pct;

        var diskColor = diskPct switch { > 90 => "red", > 70 => "yellow", _ => "green" };
        var diskUsedDisplay = sections.TryGetValue("DISK", out var diskRaw) ? ParseDfLine(diskRaw) : "?";
        var memDisplay = sections.TryGetValue("MEM", out var memRaw) ? ParseFreeOutput(memRaw) : "?";
        var dockerDisplay = sections.TryGetValue("DOCKER", out var dockerRaw) ? dockerRaw.Trim() : "[red]not running[/]";
        var uptimeDisplay = sections.TryGetValue("UPTIME", out var uptimeRaw) ? uptimeRaw.Trim() : "?";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Value[/]");

        table.AddRow("Disk /", $"[{diskColor}]{Markup.Escape(diskUsedDisplay)} ({diskPct?.ToString() ?? "?"}%)[/]");
        table.AddRow("Memory", Markup.Escape(memDisplay));
        table.AddRow("Docker", Markup.Escape(dockerDisplay));
        table.AddRow("Uptime", Markup.Escape(uptimeDisplay));

        _console.Write(table);
        return diskPct;
    }

    private static string ParseDfLine(string dfOutput)
    {
        foreach (var line in dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && parts[0] != "Filesystem")
                return $"{parts[2]} used / {parts[1]}";
        }
        return "?";
    }

    private static string ParseFreeOutput(string freeOutput)
    {
        foreach (var line in freeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.TrimStart().StartsWith("Mem:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var total = long.TryParse(parts[1], out var t) ? $"{t / 1024.0:F1} GB" : parts[1];
                    var used = long.TryParse(parts[2], out var u) ? $"{u / 1024.0:F1} GB" : parts[2];
                    return $"{used} / {total}";
                }
            }
        }
        return "?";
    }

    private async Task<int> HandleActionAsync(string action, AzureVmRecord record, string? token)
    {
        if (action == "Quit") return 0;

        if (action == "Reconnect (ssh)")
        {
            var sshTarget = await _sshTargets.FindTargetAsync(record.VmName);
            if (sshTarget == null)
            {
                _console.MarkupLine($"[yellow]No SSH target found for '{Markup.Escape(record.VmName)}'. Connect manually.[/]");
                return 1;
            }
            var args = $"-o StrictHostKeyChecking=no -p {sshTarget.Port}";
            if (!string.IsNullOrEmpty(sshTarget.KeyPath)) args += $" -i \"{sshTarget.KeyPath}\"";
            args += $" {sshTarget.Username}@{sshTarget.Host}";
            var psi = new System.Diagnostics.ProcessStartInfo("ssh") { Arguments = args, UseShellExecute = false };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode ?? 0;
        }

        if (action.StartsWith("Free disk space"))
        {
            var target = new SshTarget
            {
                Host = record.PublicIpAddress,
                Port = 22,
                Username = "azureuser",
                KeyPath = record.SshKeyPath
            };
            SshResult? result = null;
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Running docker system prune...", async _ =>
                {
                    result = await _sshExecutor.RunAsync(
                        target, "docker system prune -af --volumes", TimeSpan.FromMinutes(3));
                });
            if (result?.ExitCode == 0)
                _console.MarkupLine("[green]Disk space freed.[/]");
            else
                _console.MarkupLine($"[red]Prune failed:[/] {Markup.Escape(result?.Stderr ?? "unknown error")}");
            return 0;
        }

        if (action == "Start VM" && token != null)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Starting VM...", async _ =>
                {
                    await _vmService.StartVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
                });
            _console.MarkupLine("[green]VM start command sent.[/]");
            return 0;
        }

        if (action == "Stop / Deallocate VM" && token != null)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Deallocating VM...", async _ =>
                {
                    await _vmService.DeallocateVmAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
                });
            _console.MarkupLine("[green]VM deallocate command sent.[/]");
            return 0;
        }

        if (action.StartsWith("Destroy VM") && token != null)
        {
            _console.MarkupLine($"[yellow]This will permanently delete VM [bold]{Markup.Escape(record.VmName)}[/] and all its resources.[/]");
            var confirmed = _console.Confirm("[red]Are you sure?[/]", defaultValue: false);
            if (!confirmed) return 0;

            Exception? destroyError = null;
            await _console.Status()
                .SpinnerStyle(Style.Parse("red"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Destroying VM and resources...", async ctx =>
                {
                    try
                    {
                        await _vmService.DestroyVmAsync(
                            token, record.SubscriptionId, record.ResourceGroup, record.VmName,
                            msg => ctx.Status(msg));
                    }
                    catch (Exception ex) { destroyError = ex; }
                });

            if (destroyError != null)
            {
                _console.MarkupLine($"[red]Destroy failed:[/] {Markup.Escape(destroyError.Message)}");
                return 1;
            }

            try { await _sshTargets.RemoveTargetAsync(record.VmName); } catch { }
            await _vmMetadata.RemoveAsync(record.VmName);
            _console.MarkupLine($"[green]VM [bold]{Markup.Escape(record.VmName)}[/] destroyed.[/]");
            return 0;
        }

        return 0;
    }
}
