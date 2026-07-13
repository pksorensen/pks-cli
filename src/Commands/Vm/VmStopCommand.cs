using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Stop a VM (power off; disk/attached storage is preserved — this is not destroy)")]
public class VmStopCommand : Command<VmStopCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly IAnsiConsole _console;

    public VmStopCommand(
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

    public async Task<int> ExecuteAsync(Settings settings)
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

        var record = VmSelection.Pick(_console, vms, settings.VmName, "[cyan]Pick a VM to stop:[/]");
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

        // Outside a Status spinner so the guarded provider can prompt for a two-factor code if gated
        // (Spectre forbids interactive prompts inside a live display).
        Exception? stopError = null;
        try
        {
            await provider.StopAsync(record);
        }
        catch (Exception ex) { stopError = ex; }

        if (stopError != null)
        {
            _console.MarkupLine($"[red]Stop failed:[/] {Markup.Escape(stopError.Message)}");
            return 1;
        }

        _console.MarkupLine(
            $"[green]VM '{Markup.Escape(record.VmName)}' stopped.[/] Disk preserved — start again with " +
            $"'pks vm start {Markup.Escape(record.VmName)}' or 'pks vm tailscale {Markup.Escape(record.VmName)}'.");
        return 0;
    }
}
