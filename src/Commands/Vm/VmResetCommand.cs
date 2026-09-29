using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Wipe a VM back to its baseline snapshot (keeps IP, NIC, NSG rules and SSH key)")]
public class VmResetCommand : Command<VmResetCommand.Settings>
{
    private const string ManagementScope = "https://management.azure.com/.default";

    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmDiskService _disks;
    private readonly IActionGuard _guard;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAnsiConsole _console;

    public VmResetCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmDiskService disks,
        IActionGuard guard,
        IHttpClientFactory httpClientFactory,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _disks = disks;
        _guard = guard;
        _httpClientFactory = httpClientFactory;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name (interactive picker if omitted)")]
        public string? VmName { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Do not ask for confirmation")]
        public bool Yes { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(Settings settings)
    {
        var local = await _vmMetadata.ListAsync();
        var vms = await _providers.MergeWithDiscoveryAsync(local);
        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Use [bold]pks vm init[/] to provision one.[/]");
            return 0;
        }
        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to reset:[/]");
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(settings.VmName ?? "")}' not found.[/]");
            return 1;
        }
        if (!string.Equals(record.Provider, "azure", StringComparison.OrdinalIgnoreCase))
        {
            _console.MarkupLine($"[yellow]Reset is not supported yet for {Markup.Escape(record.Provider)} VMs.[/]");
            return 1;
        }

        var token = await _azureAuth.GetAccessTokenAsync(ManagementScope);
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain an Azure management token. Run 'pks azure init' first.[/]");
            return 1;
        }

        var purpose = $"pks vm reset {record.VmName}";
        AzureVmBaseline? baseline;
        AzureVmOsDisk? disk;
        try
        {
            baseline = await VmBaseline.WithPimRetryAsync(
                () => _disks.GetBaselineAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName),
                _console, _httpClientFactory, token, record.SubscriptionId, purpose);
            disk = await _disks.GetOsDiskAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Could not read the VM's disks:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (baseline == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(record.VmName)}' has no baseline snapshot.[/] " +
                $"Create one with [bold]pks vm snapshot {Markup.Escape(record.VmName)}[/] while it is in the state you want to return to.");
            return 1;
        }
        if (disk == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(record.VmName)}' has no managed OS disk.[/]");
            return 1;
        }

        var when = baseline.TimeCreated?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "unknown time";
        var oldDiskFate = AzureVmDiskService.IsDisposableOldDisk(disk, record.VmName)
            ? "[red]deleted[/]"
            : "[yellow]kept (not recognisably this VM's — delete it yourself)[/]";
        _console.Write(new Panel(
            $"""
            [cyan1]VM:[/]          {Markup.Escape(record.VmName)} ([dim]{Markup.Escape(record.ResourceGroup)}[/])
            [cyan1]Back to:[/]     {Markup.Escape(baseline.Name)} (taken {Markup.Escape(when)})
            [cyan1]Current disk:[/] {Markup.Escape(disk.Name)} → {oldDiskFate}

            [red]Everything written to the disk since {Markup.Escape(when)} is lost.[/]
            [dim]Kept: public IP, NIC, NSG rules, SSH key and host keys. The VM is off for a few minutes.[/]
            """)
            .Border(BoxBorder.Rounded).BorderStyle("yellow")
            .Header(" [bold yellow]Reset to baseline[/] "));

        if (!settings.Yes && !_console.Confirm("[red]Reset this VM?[/]", defaultValue: false))
            return 0;

        AzureVmResetResult result;
        try
        {
            await _guard.RequireAsync(new ActionRequest(
                ActionIds.VmReset,
                $"Reset VM '{record.VmName}' to its baseline",
                "This wipes the OS disk back to the baseline snapshot.",
                Resource: $"azure-vm:{record.ResourceGroup}/{record.VmName}"));

            result = await VmBaseline.WithPimRetryAsync(
                () => _disks.ResetToBaselineAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName,
                    msg => _console.MarkupLine($"[dim]{Markup.Escape(msg)}[/]")),
                _console, _httpClientFactory, token, record.SubscriptionId, purpose);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Reset failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        if (!result.OldDiskDeleted)
            _console.MarkupLine($"[yellow]Old disk {Markup.Escape(result.OldDiskName)} kept: {Markup.Escape(result.OldDiskKeptReason ?? "")}[/]");

        var ip = await _vmService.GetVmPublicIpAsync(token, record.SubscriptionId, record.ResourceGroup, record.VmName);
        if (!string.IsNullOrEmpty(ip))
        {
            var sshReady = false;
            await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync("Waiting for SSH (port 22)...", async _ =>
                    sshReady = await _vmService.WaitForSshAsync(ip, record.Port(), TimeSpan.FromMinutes(5)));
            if (!sshReady)
                _console.MarkupLine("[yellow]SSH is not answering yet — the VM may still be booting.[/]");

            if (ip != record.PublicIpAddress)
            {
                record.PublicIpAddress = ip;
                await _vmMetadata.SaveAsync(record);
            }
        }

        _console.MarkupLine($"[green]VM [bold]{Markup.Escape(record.VmName)}[/] is back at its baseline ({Markup.Escape(when)}) on disk {Markup.Escape(result.NewDiskName)}.[/]");
        return 0;
    }
}
