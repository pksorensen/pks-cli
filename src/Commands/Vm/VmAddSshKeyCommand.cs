using System.ComponentModel;
using System.Diagnostics;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

/// <summary>
/// Register an SSH private key for a VM that pks did not create (e.g. an instance made in
/// the cloud console). Paste the key or point at a file; it's stored under ~/.pks-cli/keys/
/// and registered as an SSH target so 'pks ssh connect' / 'pks claude' work.
/// </summary>
[Description("Add an SSH private key for a VM (paste it) so we can connect")]
public class VmAddSshKeyCommand : Command<VmAddSshKeyCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAnsiConsole _console;

    public VmAddSshKeyCommand(
        VmProviderRegistry providers,
        IAzureVmMetadataService vmMetadata,
        ISshTargetConfigurationService sshTargets,
        IAnsiConsole console)
    {
        _providers = providers;
        _vmMetadata = vmMetadata;
        _sshTargets = sshTargets;
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
            _console.MarkupLine("[yellow]No VMs found. Use [bold]pks vm list[/] first.[/]");
            return 0;
        }

        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to add a key for:[/]");
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(settings.VmName ?? "")}' not found.[/]");
            return 1;
        }

        var source = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Private key source:[/]")
                .HighlightStyle(Style.Parse("cyan"))
                .AddChoices("Paste it here", "Read from a file path"));

        string? keyText;
        if (source.StartsWith("Read"))
        {
            var path = _console.Prompt(new TextPrompt<string>("[cyan]Path to private key:[/]"));
            path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!File.Exists(path))
            {
                _console.MarkupLine($"[red]File not found: {Markup.Escape(path)}[/]");
                return 1;
            }
            keyText = await File.ReadAllTextAsync(path);
            if (!keyText.Contains("PRIVATE KEY"))
            {
                _console.MarkupLine("[red]That file doesn't look like a private key.[/]");
                return 1;
            }
        }
        else
        {
            _console.MarkupLine("[dim]Paste the full private key now. Reading stops automatically at the END line.[/]");
            keyText = SshKeyText.ReadPrivateKey(Console.In.ReadLine);
            if (keyText == null)
            {
                _console.MarkupLine("[red]No valid '-----BEGIN … PRIVATE KEY-----' block was read.[/]");
                return 1;
            }
        }

        // Write to ~/.pks-cli/keys/<name> with 600 perms
        var keyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "keys");
        Directory.CreateDirectory(keyDir);
        var keyPath = Path.Combine(keyDir, record.VmName);
        await File.WriteAllTextAsync(keyPath, keyText.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }

        // Derive the public key (best-effort)
        try
        {
            var psi = new ProcessStartInfo("ssh-keygen")
            {
                Arguments = $"-y -f \"{keyPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var pub = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(pub))
                    await File.WriteAllTextAsync(keyPath + ".pub", pub.Trim() + "\n");
            }
        }
        catch { /* pub key derivation is optional */ }

        // Persist the record (so the key path sticks across discovery) + register SSH target
        record.SshKeyPath = keyPath;
        await _vmMetadata.SaveAsync(record);
        await VmConnection.RegisterTargetAsync(_sshTargets, record);

        _console.MarkupLine($"[green]Key stored at [bold]{Markup.Escape(keyPath)}[/] and registered for [bold]{Markup.Escape(record.VmName)}[/].[/]");
        var provider = _providers.Resolve(record);
        VmConnection.RenderConnectionPanel(_console, VmConnection.ToConnectionInfo(record, provider.DisplayName));
        return 0;
    }
}
