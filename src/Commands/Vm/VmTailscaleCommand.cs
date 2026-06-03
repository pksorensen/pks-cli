using System.ComponentModel;
using PKS.Commands.Tailscale;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Start a VM and join it to the tailnet (Tailscale SSH + accept-routes + exit-node per
/// `pks tailscale init`), so it can reach NAS/subnet devices for experiments.
/// </summary>
[Description("Start a VM and connect it to your Tailscale network")]
public class VmTailscaleCommand : Command<VmTailscaleCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAzureVmService _vmService;
    private readonly ISshExecutor _ssh;
    private readonly ITailscaleService _tailscale;
    private readonly TailscaleInitCommand _tailscaleInit;
    private readonly IAnsiConsole _console;

    public VmTailscaleCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        ISshTargetConfigurationService sshTargets,
        IAzureVmService vmService,
        ISshExecutor ssh,
        ITailscaleService tailscale,
        TailscaleInitCommand tailscaleInit,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
        _sshTargets = sshTargets;
        _vmService = vmService;
        _ssh = ssh;
        _tailscale = tailscale;
        _tailscaleInit = tailscaleInit;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name (interactive picker if omitted)")]
        public string? VmName { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(context, settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        // 1. Ensure Tailscale is configured (chain into init if not)
        if (!await _tailscale.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[cyan]Tailscale not configured yet. Starting setup...[/]");
            _tailscaleInit.Execute(context, new TailscaleInitCommand.Settings());
            if (!await _tailscale.IsAuthenticatedAsync())
            {
                _console.MarkupLine("[red]Tailscale setup required.[/]");
                return 1;
            }
        }
        var creds = (await _tailscale.GetStoredCredentialsAsync())!;

        // 2. Pick VM
        var local = await _vmMetadata.ListAsync();
        List<AzureVmRecord> vms = new();
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync("Discovering VMs...", async _ => vms = await _providers.MergeWithDiscoveryAsync(local));

        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs found. Use [bold]pks vm init[/] first.[/]");
            return 0;
        }

        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to join to Tailscale:[/]");
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

        // 3. Start + wait for SSH
        var ip = await VmConnection.EnsureReachableAsync(record, provider, _vmService, _console);
        if (string.IsNullOrEmpty(ip))
        {
            _console.MarkupLine("[yellow]VM started but no public IP became available in time.[/]");
            return 1;
        }
        await VmConnection.RegisterTargetAsync(_sshTargets, record);

        // 4. Need a usable local key to SSH in
        var keyPath = string.IsNullOrEmpty(record.SshKeyPath) ? VmConnection.KeyPathFor(record.VmName) : record.SshKeyPath;
        if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
        {
            _console.MarkupLine($"[red]No local SSH key for '{Markup.Escape(record.VmName)}'.[/]");
            _console.MarkupLine("[dim]Run [bold]pks vm add-ssh-key[/] (paste the key) so we can configure Tailscale over SSH.[/]");
            return 1;
        }

        var target = new SshTarget { Host = ip, Username = record.AdminUsername, Port = record.Port(), KeyPath = keyPath };
        var sudo = record.AdminUsername == "root" ? string.Empty : "sudo ";

        // 5. Install Tailscale
        var installResult = await RunStep("Installing Tailscale...",
            $"command -v tailscale >/dev/null 2>&1 || (curl -fsSL https://tailscale.com/install.sh | {sudo}sh)",
            target, TimeSpan.FromMinutes(3));
        if (installResult == null) return 1;

        // 6. Enable IP forwarding (needed for exit-node / subnet routing)
        if (creds.AdvertiseExitNode || creds.AcceptRoutes)
        {
            await RunStep("Enabling IP forwarding...",
                $"printf 'net.ipv4.ip_forward=1\\nnet.ipv6.conf.all.forwarding=1\\n' | {sudo}tee /etc/sysctl.d/99-tailscale.conf >/dev/null && {sudo}sysctl -p /etc/sysctl.d/99-tailscale.conf >/dev/null 2>&1 || true",
                target, TimeSpan.FromSeconds(30));
        }

        // 7. tailscale up
        var upArgs = _tailscale.BuildUpArgs(creds, record.VmName);
        var upResult = await RunStep("Joining the tailnet (tailscale up)...",
            $"{sudo}tailscale up {upArgs}", target, TimeSpan.FromMinutes(2));
        if (upResult == null) return 1;
        if (upResult.ExitCode != 0)
        {
            _console.MarkupLine($"[red]tailscale up failed:[/] {Markup.Escape(string.IsNullOrWhiteSpace(upResult.Stderr) ? upResult.Stdout : upResult.Stderr)}");
            return 1;
        }

        // 8. Read the tailnet IP
        string? tsIp = null;
        var ipResult = await RunStep("Reading tailnet IP...", $"{sudo}tailscale ip -4", target, TimeSpan.FromSeconds(20));
        if (ipResult != null && ipResult.ExitCode == 0)
            tsIp = ipResult.Stdout.Trim().Split('\n').FirstOrDefault()?.Trim();

        _console.MarkupLine("[green]VM joined the tailnet.[/]");
        if (creds.AdvertiseExitNode)
            _console.MarkupLine("[yellow]Exit node advertised — approve it in the Tailscale admin console (Machines → … → Edit route settings).[/]");
        VmConnection.RenderConnectionPanel(_console, VmConnection.ToConnectionInfo(record, provider.DisplayName, tsIp));
        return 0;
    }

    private async Task<SshResult?> RunStep(string label, string command, SshTarget target, TimeSpan timeout)
    {
        SshResult? result = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
            .StartAsync(label, async _ => { result = await _ssh.RunAsync(target, command, timeout); });

        if (result == null || result.TimedOut)
        {
            _console.MarkupLine($"[red]Step timed out:[/] {Markup.Escape(label)}");
            return null;
        }
        return result;
    }
}
