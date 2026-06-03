using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Print a copy-pasteable shell command that recreates a pks-created VM's private key and
/// connects over SSH — for handing an agent (here or elsewhere) access to the box.
/// </summary>
[Description("Print a command to install a VM's SSH key locally and connect")]
public class VmExportSshKeyCommand : Command<VmExportSshKeyCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAnsiConsole _console;

    public VmExportSshKeyCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
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
            _console.MarkupLine("[yellow]No VMs found.[/]");
            return 0;
        }

        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to export the key for:[/]");
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(settings.VmName ?? "")}' not found.[/]");
            return 1;
        }

        var keyPath = string.IsNullOrEmpty(record.SshKeyPath) ? VmConnection.KeyPathFor(record.VmName) : record.SshKeyPath;
        if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
        {
            _console.MarkupLine($"[red]No local private key for '{Markup.Escape(record.VmName)}'.[/]");
            _console.MarkupLine("[dim]This VM wasn't created by pks. Run [bold]pks vm add-ssh-key[/] to paste its key first.[/]");
            return 1;
        }

        var keyText = (await File.ReadAllTextAsync(keyPath)).Replace("\r\n", "\n").TrimEnd() + "\n";
        var host = string.IsNullOrEmpty(record.PublicIpAddress) ? "<ip>" : record.PublicIpAddress;
        var dest = $"~/.ssh/{record.VmName}";

        _console.MarkupLine($"[dim]# Paste this where the agent runs to install the key and connect to [bold]{Markup.Escape(record.VmName)}[/]:[/]");
        _console.WriteLine();
        _console.WriteLine($"mkdir -p ~/.ssh && cat > {dest} <<'PKS_EOF'");
        _console.WriteLine(keyText.TrimEnd());
        _console.WriteLine("PKS_EOF");
        _console.WriteLine($"chmod 600 {dest}");
        _console.WriteLine($"ssh -i {dest} -o StrictHostKeyChecking=no -p {record.Port()} {record.AdminUsername}@{host}");
        return 0;
    }
}
