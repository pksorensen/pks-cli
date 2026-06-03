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
    private readonly VmProviderRegistry _providers;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly ISshExecutor _sshExecutor;
    private readonly IAzureVmService _vmService;
    private readonly IAnsiConsole _console;

    public VmListCommand(
        IAzureVmMetadataService vmMetadata,
        VmProviderRegistry providers,
        ISshTargetConfigurationService sshTargets,
        ISshExecutor sshExecutor,
        IAzureVmService vmService,
        IAnsiConsole console)
    {
        _vmMetadata = vmMetadata;
        _providers = providers;
        _sshTargets = sshTargets;
        _sshExecutor = sshExecutor;
        _vmService = vmService;
        _console = console;
    }

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync()
    {
        var local = await _vmMetadata.ListAsync();
        var localNames = new HashSet<string>(local.Select(v => v.VmName), StringComparer.OrdinalIgnoreCase);

        List<AzureVmRecord> vms = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Discovering VMs...", async _ =>
            {
                vms = await _providers.MergeWithDiscoveryAsync(local);
            });

        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return 0;
        }

        // Check live status for each VM via its provider
        var statuses = new Dictionary<string, string?>();
        var missing = new List<AzureVmRecord>();

        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking VM status...", async _ =>
            {
                var tasks = vms.Select(async vm =>
                {
                    try
                    {
                        var provider = _providers.Resolve(vm);
                        var status = await provider.GetStatusAsync(vm);
                        return (vm, status);
                    }
                    catch { return (vm, (string?)null); }
                });
                foreach (var (vm, status) in await Task.WhenAll(tasks))
                {
                    statuses[Key(vm)] = status;
                    // Only auto-remove records that were locally tracked and have vanished.
                    if (status == null && localNames.Contains(vm.VmName))
                        missing.Add(vm);
                }
            });

        // Fetch disk usage for running VMs in parallel
        var diskPcts = new Dictionary<string, int?>();
        var runningVms = vms.Where(v => statuses.TryGetValue(Key(v), out var s) && s == VmPowerState.Running
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
                            Username = vm.AdminUsername,
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
                                return (Key(vm), (int?)pct);
                        }
                        catch { }
                        return (Key(vm), (int?)null);
                    });
                    foreach (var (key, pct) in await Task.WhenAll(diskTasks))
                        diskPcts[key] = pct;
                });
        }

        // Remove VMs that no longer exist (locally-tracked only)
        if (missing.Count > 0)
        {
            _console.MarkupLine($"[yellow]Removing {missing.Count} deleted VM(s) from local state: {string.Join(", ", missing.Select(v => Markup.Escape(v.VmName)))}[/]");
            foreach (var vm in missing)
            {
                await _vmMetadata.RemoveAsync(vm.VmName);
                var sshTarget = await _sshTargets.FindTargetAsync(vm.VmName);
                if (sshTarget != null)
                    await _sshTargets.RemoveTargetAsync(sshTarget.Id);
                vms.RemoveAll(v => Key(v) == Key(vm));
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
            .AddColumn("[bold]Provider[/]")
            .AddColumn("[bold]Status[/]")
            .AddColumn("[bold]Disk %[/]")
            .AddColumn("[bold]Public IP[/]")
            .AddColumn("[bold]Type[/]")
            .AddColumn("[bold]Location[/]")
            .AddColumn("[bold]Idle Shutdown[/]")
            .AddColumn("[bold]Created[/]");

        foreach (var vm in vms)
        {
            var idleDisplay = vm.IdleShutdownMinutes > 0
                ? $"{vm.IdleShutdownMinutes}m"
                : "[dim]off[/]";

            string statusDisplay;
            if (statuses.TryGetValue(Key(vm), out var s))
            {
                statusDisplay = s switch
                {
                    VmPowerState.Running => "[green]running[/]",
                    VmPowerState.Stopped => "[yellow]stopped[/]",
                    VmPowerState.Starting => "[cyan]starting[/]",
                    VmPowerState.Stopping => "[yellow]stopping[/]",
                    null => "[dim]?[/]",
                    _ => $"[dim]{Markup.Escape(s)}[/]"
                };
            }
            else
            {
                statusDisplay = "[dim]?[/]";
            }

            string diskDisplay;
            if (diskPcts.TryGetValue(Key(vm), out var pct) && pct.HasValue)
            {
                var color = pct.Value switch { > 90 => "red", > 70 => "yellow", _ => "green" };
                diskDisplay = $"[{color}]{pct.Value}%[/]";
            }
            else
            {
                diskDisplay = "[dim]—[/]";
            }

            var providerName = _providers.Resolve(vm).DisplayName;

            table.AddRow(
                $"[cyan]{Markup.Escape(vm.VmName)}[/]",
                Markup.Escape(providerName),
                statusDisplay,
                diskDisplay,
                Markup.Escape(string.IsNullOrEmpty(vm.PublicIpAddress) ? "—" : vm.PublicIpAddress),
                Markup.Escape(vm.VmSize),
                Markup.Escape(vm.Location),
                idleDisplay,
                vm.CreatedAt.ToString("yyyy-MM-dd HH:mm") + " UTC"
            );
        }

        _console.Write(table);
        _console.WriteLine();
        _console.MarkupLine($"[dim]{vms.Count} VM(s). SSH key dir: ~/.pks-cli/keys/[/]");
        _console.MarkupLine("[dim]Connect: pks ssh connect <name>[/]");

        // Drill-down: offer to inspect a specific VM
        var inspectChoices = vms.Select(v => v.VmName).ToList();
        inspectChoices.Add("No, quit");

        _console.MarkupLine(string.Empty);
        var inspect = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Inspect a VM?[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices(inspectChoices));

        if (inspect != "No, quit")
        {
            var selected = vms.First(v => v.VmName == inspect);
            var statusCmd = new VmStatusCommand(_providers, _vmMetadata, _sshExecutor, _sshTargets, _vmService, _console);
            return await statusCmd.ShowVmStatusAsync(selected);
        }

        return 0;
    }

    private static string Key(AzureVmRecord r)
    {
        var provider = string.IsNullOrEmpty(r.Provider) ? "azure" : r.Provider;
        var id = !string.IsNullOrEmpty(r.ServerId) ? r.ServerId : r.VmName;
        return $"{provider}:{id}";
    }
}
