using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Snapshot a VM's OS disk as its baseline — the state 'pks vm reset' returns to")]
public class VmSnapshotCommand : Command<VmSnapshotCommand.Settings>
{
    private const string ManagementScope = "https://management.azure.com/.default";

    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmDiskService _disks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;

    public VmSnapshotCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmDiskService disks,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _disks = disks;
        _httpClientFactory = httpClientFactory;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name (interactive picker if omitted)")]
        public string? VmName { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Replace an existing baseline without asking")]
        public bool Yes { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(Settings settings)
    {
        var record = await PickAsync(settings.VmName, "[cyan]Pick a VM to snapshot:[/]");
        if (record == null) return 1;
        if (!string.Equals(record.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine($"[yellow]Baselines are not supported yet for {Markup.Escape(record.Provider)} VMs.[/]");
            return 1;
        }

        var token = await _azureAuth.GetAccessTokenAsync(ManagementScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain an Azure management token. Run 'pks azure init' first.[/]");
            return 1;
        }

        try
        {
            var existing = await VmBaseline.WithPimRetryAsync(
                () => _disks.GetBaselineAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName),
                _console, _httpClientFactory, token, record.SubscriptionId, $"pks vm snapshot {record.VmName}");

            if (existing != null && !settings.Yes)
            {
                var when = existing.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown time";
                if (!_console.Confirm($"[yellow]Replace the baseline from {Markup.Escape(when)}?[/] 'pks vm reset' will then return to the disk as it is now.", defaultValue: false))
                    return 0;
            }

            var status = await _vmService.GetVmStatusAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
            if (status == "running")
            {
                var keyPath = string.IsNullOrEmpty(record.SshKeyPath) ? VmConnection.KeyPathFor(record.VmName) : record.SshKeyPath;
                if (!await VmBaseline.TrySyncAsync(record.PublicIpAddress, record.AdminUsername, keyPath))
                    _console.MarkupLine("[dim]Could not run 'sync' over SSH — the snapshot is crash-consistent only.[/]");
            }

            var baseline = await VmBaseline.WithPimRetryAsync(
                () => _disks.CreateBaselineAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName,
                    msg => _console.MarkupLine($"[dim]{Markup.Escape(msg)}[/]")),
                _console, _httpClientFactory, token, record.SubscriptionId, $"pks vm snapshot {record.VmName}");

            _console.MarkupLine($"[green]Baseline [bold]{Markup.Escape(baseline.Name)}[/] saved.[/] Return to it with 'pks vm reset {Markup.Escape(record.VmName)}'.");
            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Snapshot failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private async Task<AzureVmRecord?> PickAsync(string? name, string title)
    {
        var local = await _vmMetadata.ListAsync();
        var vms = await _providers.MergeWithDiscoveryAsync(local);
        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return null;
        }
        var record = VmSelection.Pick(_console, vms, name, title);
        if (record == null)
            _console.MarkupLine($"[red]VM '{Markup.Escape(name ?? "")}' not found.[/]");
        return record;
    }
}
