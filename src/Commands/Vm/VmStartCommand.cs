using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Start a VM, wait until it's reachable, and print connection info")]
public class VmStartCommand : Command<VmStartCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAzureVmService _vmService;
    private readonly IAnsiConsole _console;

    public VmStartCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        ISshTargetConfigurationService sshTargets,
        IAzureVmService vmService,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
        _sshTargets = sshTargets;
        _vmService = vmService;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name (interactive picker if omitted)")]
        public string? VmName { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var local = await _vmMetadata.ListAsync();
        List<AzureVmRecord> vms = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Discovering VMs...", async _ => vms = await _providers.MergeWithDiscoveryAsync(local));

        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return 0;
        }

        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to start:[/]");
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(settings.VmName ?? "")}' not found.[/]");
            return 1;
        }

        var provider = _providers.Resolve(record);
        if (!await provider.IsAuthenticatedAsync())
        {
            _console.MarkupLine($"[red]Not authenticated with {Markup.Escape(provider.DisplayName)}. Run 'pks {provider.ProviderKey} init' first.[/]");
            return 1;
        }

        var ip = await VmConnection.EnsureReachableAsync(record, provider, _vmService, _console);
        if (string.IsNullOrEmpty(ip))
        {
            _console.MarkupLine("[yellow]VM started but no public IP became available in time. Check 'pks vm list'.[/]");
            return 1;
        }

        await VmConnection.RegisterTargetAsync(_sshTargets, record);
        VmConnection.RenderConnectionPanel(_console, VmConnection.ToConnectionInfo(record, provider.DisplayName));
        return 0;
    }
}
