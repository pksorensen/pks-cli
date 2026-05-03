using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("List VMs provisioned with pks vm init")]
public class VmListCommand : Command<VmListCommand.Settings>
{
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly ISshExecutor _sshExecutor;
    private readonly IAnsiConsole _console;

    public VmListCommand(
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        ISshTargetConfigurationService sshTargets,
        ISshExecutor sshExecutor,
        IAnsiConsole console)
    {
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _sshTargets = sshTargets;
        _sshExecutor = sshExecutor;
        _console = console;
    }

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync()
    {
        var vms = await _vmMetadata.ListAsync();

        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return 0;
        }

        // Try to get Azure token for live status checks
        string? token = null;
        if (await _azureAuth.IsAuthenticatedAsync())
            token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");

        // Check live status for each VM and collect which ones no longer exist
        var statuses = new Dictionary<string, string?>();
        var missing = new List<string>();

        if (token != null)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Checking VM status in Azure...", async _ =>
                {
                    var tasks = vms.Select(async vm =>
                    {
                        var status = await _vmService.GetVmStatusAsync(token, vm.SubscriptionId, vm.ResourceGroup, vm.VmName);
                        return (vm.VmName, status);
                    });
                    foreach (var (name, status) in await Task.WhenAll(tasks))
                    {
                        statuses[name] = status;
                        if (status == null)
                            missing.Add(name);
                    }
                });
        }

        // Fetch disk usage for running VMs in parallel
        var diskPcts = new Dictionary<string, int?>();
        var runningVms = vms.Where(v => statuses.TryGetValue(v.VmName, out var s) && s == "running"
                                         && !string.IsNullOrEmpty(v.PublicIpAddress)).ToList();
        if (runningVms.Count > 0)
        {
            await _console.Status()
                .SpinnerStyle(Style.Parse("cyan"))
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching disk usage...", async _ =>
                {
                    var diskTasks = runningVms.Select(async vm =>
                    {
                        var target = new SshTarget
                        {
                            Host = vm.PublicIpAddress,
                            Port = 22,
                            Username = "azureuser",
                            KeyPath = vm.SshKeyPath
                        };
                        try
                        {
                            var result = await _sshExecutor.RunAsync(
                                target,
                                "df --output=pcent / | tail -1 | tr -d ' %'",
                                TimeSpan.FromSeconds(10));
                            if (!result.TimedOut && result.ExitCode == 0
                                && int.TryParse(result.Stdout.Trim(), out var pct))
                                return (vm.VmName, (int?)pct);
                        }
                        catch { }
                        return (vm.VmName, (int?)null);
                    });
                    foreach (var (name, pct) in await Task.WhenAll(diskTasks))
                        diskPcts[name] = pct;
                });
        }

        // Remove VMs that no longer exist in Azure
        if (missing.Count > 0)
        {
            _console.MarkupLine($"[yellow]Removing {missing.Count} deleted VM(s) from local state: {string.Join(", ", missing.Select(Markup.Escape))}[/]");
            foreach (var name in missing)
            {
                await _vmMetadata.RemoveAsync(name);
                // Also remove the SSH target registered for this VM
                var sshTarget = await _sshTargets.FindTargetAsync(name);
                if (sshTarget != null)
                    await _sshTargets.RemoveTargetAsync(sshTarget.Id);
                vms.RemoveAll(v => v.VmName == name);
            }

            if (vms.Count == 0)
            {
                _console.MarkupLine("[yellow]No tracked VMs remaining.[/]");
                return 0;
            }
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]PKS Virtual Machines[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Disk %[/]")
            .AddColumn("[bold]Public IP[/]")
            .AddColumn("[bold]Size[/]")
            .AddColumn("[bold]Location[/]")
            .AddColumn("[bold]Resource Group[/]")
            .AddColumn("[bold]Idle Shutdown[/]")
            .AddColumn("[bold]Scheduled[/]")
            .AddColumn("[bold]Created[/]");

        foreach (var vm in vms)
        {
            var idleDisplay = vm.IdleShutdownMinutes > 0
                ? $"{vm.IdleShutdownMinutes}m"
                : "[dim]off[/]";
            var scheduledDisplay = vm.ScheduledShutdownUtc != null
                ? $"{Markup.Escape(vm.ScheduledShutdownUtc)} UTC"
                : "[dim]none[/]";

            string statusDisplay;
            if (token == null)
            {
                statusDisplay = "[dim]?[/]";
            }
            else if (statuses.TryGetValue(vm.VmName, out var s))
            {
                statusDisplay = s switch
                {
                    "running" => "[green]running[/]",
                    "stopped" => "[yellow]stopped[/]",
                    "deallocated" => "[dim]deallocated[/]",
                    "starting" => "[cyan]starting[/]",
                    "stopping" => "[yellow]stopping[/]",
                    "deallocating" => "[dim]deallocating[/]",
                    _ => $"[dim]{Markup.Escape(s ?? "?")}[/]"
                };
            }
            else
            {
                statusDisplay = "[dim]?[/]";
            }

            string diskDisplay;
            if (diskPcts.TryGetValue(vm.VmName, out var pct) && pct.HasValue)
            {
                var color = pct.Value switch { > 90 => "red", > 70 => "yellow", _ => "green" };
                diskDisplay = $"[{color}]{pct.Value}%[/]";
            }
            else
            {
                diskDisplay = "[dim]—[/]";
            }

            table.AddRow(
                $"[cyan]{Markup.Escape(vm.VmName)}[/]",
                statusDisplay,
                diskDisplay,
                Markup.Escape(vm.PublicIpAddress),
                Markup.Escape(vm.VmSize),
                Markup.Escape(vm.Location),
                Markup.Escape(vm.ResourceGroup),
                idleDisplay,
                scheduledDisplay,
                vm.CreatedAt.ToString("yyyy-MM-dd HH:mm") + " UTC"
            );
        }

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine($"[dim]{vms.Count} VM(s). SSH key dir: ~/.pks-cli/keys/[/]");
        _console.MarkupLine("[dim]Connect: pks ssh connect <name>[/]");
        _console.MarkupLine("[dim]Change shutdown: pks vm autoshutdown <name> --idle 30[/]");

        // Drill-down: offer to inspect a specific VM
        var inspectChoices = vms.Select(v => v.VmName).ToList();
        inspectChoices.Add("No, quit");

        _console.MarkupLine(string.Empty);
        var inspect = _console.Prompt(
            new TextPrompt<string>("[cyan]Inspect a VM?[/]")
                .AddChoices(inspectChoices)
                .DefaultValue("No, quit"));

        if (inspect != "No, quit")
        {
            var selected = vms.First(v => v.VmName == inspect);
            var statusCmd = new VmStatusCommand(_azureAuth, _vmService, _vmMetadata, _sshExecutor, _sshTargets, _console);
            return await statusCmd.ShowVmStatusAsync(selected);
        }

        return 0;
    }
}
