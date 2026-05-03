using System.ComponentModel;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Destroy a VM and all its associated Azure resources")]
public class VmDestroyCommand : Command<VmDestroyCommand.Settings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly ISshTargetConfigurationService _sshTargets;
    private readonly IAnsiConsole _console;

    public VmDestroyCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmMetadataService vmMetadata,
        ISshTargetConfigurationService sshTargets,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _vmMetadata = vmMetadata;
        _sshTargets = sshTargets;
        _console = console;
    }

    public class Settings : VmSettings { }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(Settings settings)
    {
        var vms = await _vmMetadata.ListAsync();
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

        if (!await _azureAuth.IsAuthenticatedAsync())
        {
            _console.MarkupLine("[red]No Azure credentials. Run 'pks azure init' first.[/]");
            return 1;
        }

        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain Azure management token.[/]");
            return 1;
        }

        return await DestroyVmAsync(record, token);
    }

    public async Task<int> DestroyVmAsync(AzureVmRecord record, string token)
    {
        _console.MarkupLine($"[yellow]This will permanently delete VM [bold]{Markup.Escape(record.VmName)}[/] and all its resources.[/]");
        var confirmed = _console.Confirm("[red]Are you sure?[/]", defaultValue: false);
        if (!confirmed) return 0;

        Exception? destroyError = null;
        await _console.Status()
            .SpinnerStyle(Style.Parse("red"))
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Destroying VM and resources...", async ctx =>
            {
                try
                {
                    await _vmService.DestroyVmAsync(
                        token, record.SubscriptionId, record.ResourceGroup, record.VmName,
                        msg => ctx.Status(msg));
                }
                catch (Exception ex) { destroyError = ex; }
            });

        if (destroyError != null)
        {
            _console.MarkupLine($"[red]Destroy failed:[/] {Markup.Escape(destroyError.Message)}");
            return 1;
        }

        // Remove SSH target
        try { await _sshTargets.RemoveTargetAsync(record.VmName); } catch { }

        // Remove from metadata store
        await _vmMetadata.RemoveAsync(record.VmName);

        _console.MarkupLine($"[green]VM [bold]{Markup.Escape(record.VmName)}[/] and all resources destroyed.[/]");
        return 0;
    }
}
