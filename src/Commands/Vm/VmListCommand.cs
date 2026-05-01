using System.ComponentModel;
using PKS.Infrastructure.Services;
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
    private readonly IAnsiConsole _console;

    public VmListCommand(
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        ISshTargetConfigurationService sshTargets,
        IAnsiConsole console)
    {
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _sshTargets = sshTargets;
        _console = console;
    }

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync().GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync()
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
                    foreach (var vm in vms)
                    {
                        var status = await _vmService.GetVmStatusAsync(token, vm.SubscriptionId, vm.ResourceGroup, vm.VmName);
                        statuses[vm.VmName] = status;
                        if (status == null)
                            missing.Add(vm.VmName);
                    }
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
                    "running"      => "[green]running[/]",
                    "stopped"      => "[yellow]stopped[/]",
                    "deallocated"  => "[dim]deallocated[/]",
                    "starting"     => "[cyan]starting[/]",
                    "stopping"     => "[yellow]stopping[/]",
                    "deallocating" => "[dim]deallocating[/]",
                    _              => $"[dim]{Markup.Escape(s ?? "?")}[/]"
                };
            }
            else
            {
                statusDisplay = "[dim]?[/]";
            }

            table.AddRow(
                $"[cyan]{Markup.Escape(vm.VmName)}[/]",
                statusDisplay,
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

        return 0;
    }
}
