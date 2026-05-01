using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Ssh;

[Description("Open an interactive SSH session to a registered target")]
public class SshConnectCommand : Command<SshConnectCommand.Settings>
{
    private readonly ISshTargetConfigurationService _configService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAnsiConsole _console;

    public SshConnectCommand(
        ISshTargetConfigurationService configService,
        IAzureVmMetadataService vmMetadata,
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAnsiConsole console)
    {
        _configService = configService;
        _vmMetadata = vmMetadata;
        _azureAuth = azureAuth;
        _vmService = vmService;
        _console = console;
    }

    public class Settings : SshSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [Description("SSH target label or host (interactive picker if omitted)")]
        public string? Target { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var targets = await _configService.ListTargetsAsync();

        // Prune SSH targets whose pks-provisioned VM no longer exists in local metadata
        var trackedVmNames = (await _vmMetadata.ListAsync()).Select(v => v.VmName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var orphan in targets.Where(t =>
            t.Label != null &&
            t.Label.StartsWith("pks-", StringComparison.OrdinalIgnoreCase) &&
            !trackedVmNames.Contains(t.Label)).ToList())
        {
            await _configService.RemoveTargetAsync(orphan.Id);
            targets.Remove(orphan);
        }

        if (targets.Count == 0)
        {
            _console.MarkupLine("[red]No SSH targets registered. Run 'pks ssh register' or 'pks vm init' first.[/]");
            return 1;
        }

        SshTarget target;

        if (!string.IsNullOrWhiteSpace(settings.Target))
        {
            var found = await _configService.FindTargetAsync(settings.Target);
            if (found == null)
            {
                _console.MarkupLine($"[red]SSH target '{Markup.Escape(settings.Target)}' not found.[/]");
                return 1;
            }
            target = found;
        }
        else
        {
            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select SSH target:[/]")
                    .AddChoices(targets.Select(t => t.Label ?? t.Host)));

            target = targets.First(t => (t.Label ?? t.Host) == choice);
        }

        // Check if this target maps to a tracked VM and start it if stopped
        if (!await EnsureVmRunningAsync(target))
            return 1;

        var args = $"-o StrictHostKeyChecking=no -p {target.Port}";
        if (!string.IsNullOrEmpty(target.KeyPath))
            args += $" -i \"{target.KeyPath}\"";
        args += $" {target.Username}@{target.Host}";

        _console.MarkupLine($"[dim]Connecting to {Markup.Escape(target.Username)}@{Markup.Escape(target.Host)}...[/]");

        var psi = new ProcessStartInfo("ssh")
        {
            Arguments = args,
            UseShellExecute = false
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            _console.MarkupLine("[red]Failed to start ssh process.[/]");
            return 1;
        }

        proc.WaitForExit();
        return proc.ExitCode;
    }

    private async Task<bool> EnsureVmRunningAsync(SshTarget target)
    {
        // Find a matching VM record by label or IP
        var vms = await _vmMetadata.ListAsync();
        var vmRecord = vms.FirstOrDefault(v =>
            string.Equals(v.VmName, target.Label, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.PublicIpAddress, target.Host, StringComparison.OrdinalIgnoreCase));

        if (vmRecord == null)
            return true; // Not a tracked Azure VM — proceed as-is

        if (!await _azureAuth.IsAuthenticatedAsync())
            return true; // Can't check — let SSH try anyway

        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
            return true;

        string? status = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Checking VM status...", async _ =>
            {
                status = await _vmService.GetVmStatusAsync(token, vmRecord.SubscriptionId, vmRecord.ResourceGroup, vmRecord.VmName);
            });

        if (status == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(vmRecord.VmName)}' no longer exists in Azure.[/]");
            return false;
        }

        if (status is "running" or "starting")
        {
            _console.MarkupLine($"[dim]VM status: {status}[/]");
            return true;
        }

        // Stopped or deallocated — offer to start
        _console.MarkupLine($"[yellow]VM '{Markup.Escape(vmRecord.VmName)}' is {Markup.Escape(status)}.[/]");
        var start = _console.Confirm("[cyan]Start the VM?[/]", defaultValue: true);
        if (!start)
            return false;

        Exception? startError = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting VM...", async _ =>
            {
                try { await _vmService.StartVmAsync(token, vmRecord.SubscriptionId, vmRecord.ResourceGroup, vmRecord.VmName); }
                catch (Exception ex) { startError = ex; }
            });

        if (startError != null)
        {
            _console.MarkupLine($"[red]Failed to start VM: {Markup.Escape(startError.Message)}[/]");
            return false;
        }

        var sshReady = false;
        await _console.Status()
            .SpinnerStyle(Style.Parse("cyan"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Waiting for SSH...", async _ =>
            {
                sshReady = await _vmService.WaitForSshAsync(target.Host, target.Port, TimeSpan.FromMinutes(3));
            });

        if (!sshReady)
        {
            _console.MarkupLine("[yellow]SSH did not become available in time. The VM may still be booting.[/]");
            return false;
        }

        _console.MarkupLine("[green]VM started.[/]");
        return true;
    }
}
