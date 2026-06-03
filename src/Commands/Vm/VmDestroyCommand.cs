using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Destroy a VM and all its associated cloud resources")]
public class VmDestroyCommand : Command<VmDestroyCommand.Settings>
{
    private readonly VmProviderRegistry _providers;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAnsiConsole _console;

    public VmDestroyCommand(
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

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(Settings settings)
    {
        var local = await _vmMetadata.ListAsync();
        var vms = await _providers.MergeWithDiscoveryAsync(local);
        if (vms.Count == 0)
        {
            _console.MarkupLine("[yellow]No VMs tracked. Nothing to destroy.[/]");
            return 0;
        }

        AzureVmRecord record;
        if (vms.Count == 1)
        {
            record = vms[0];
        }
        else
        {
            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Pick a VM to destroy:[/]")
                    .AddChoices(vms.Select(v => v.VmName)));
            record = vms.First(v => v.VmName == choice);
        }

        var provider = _providers.Resolve(record);
        if (!await provider.IsAuthenticatedAsync())
        {
            _console.MarkupLine($"[red]Not authenticated with {Markup.Escape(provider.DisplayName)}. Run 'pks {provider.ProviderKey} init' first.[/]");
            return 1;
        }

        return await DestroyVmAsync(record);
    }

    public async Task<int> DestroyVmAsync(AzureVmRecord record)
    {
        var provider = _providers.Resolve(record);

        _console.MarkupLine($"[yellow]This will permanently delete VM [bold]{Markup.Escape(record.VmName)}[/] ([dim]{Markup.Escape(provider.DisplayName)}[/]) and all its resources.[/]");
        var confirmed = _console.Confirm("[red]Are you sure?[/]", defaultValue: false);
        if (!confirmed) return 0;

        // Outside a Status spinner so the guarded provider can prompt for a two-factor code if gated
        // (Spectre forbids interactive prompts inside a live display); progress is shown as plain lines.
        Exception? destroyError = null;
        try
        {
            await provider.DestroyAsync(record, msg => _console.MarkupLine($"[dim]{Markup.Escape(msg)}[/]"));
        }
        catch (Exception ex) { destroyError = ex; }

        if (destroyError != null)
        {
            _console.MarkupLine($"[red]Destroy failed:[/] {Markup.Escape(destroyError.Message)}");
            return 1;
        }

        // Remove SSH target — find by label/host (Id is a GUID, not the VM name)
        try
        {
            var sshTarget = await _sshTargets.FindTargetAsync(record.VmName);
            if (sshTarget != null)
                await _sshTargets.RemoveTargetAsync(sshTarget.Id);
        }
        catch { }

        // Remove from metadata store (no-op for discovered-only records)
        await _vmMetadata.RemoveAsync(record.VmName);

        _console.MarkupLine($"[green]VM [bold]{Markup.Escape(record.VmName)}[/] and all resources destroyed.[/]");
        return 0;
    }
}
