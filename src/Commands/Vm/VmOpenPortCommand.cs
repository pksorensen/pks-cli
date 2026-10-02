using System.ComponentModel;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Vm;

[Description("Open inbound TCP ports on a VM's network security group")]
public class VmOpenPortCommand : Command<VmOpenPortCommand.Settings>
{
    private readonly IAzureAuthService _azureAuth;
    private readonly IAzureVmService _vmService;
    private readonly IAzureVmMetadataService _vmMetadata;
    private readonly VmProviderRegistry _providers;
    private readonly IActionGuard _guard;
    private readonly IAnsiConsole _console;

    public VmOpenPortCommand(
        IAzureAuthService azureAuth,
        IAzureVmService vmService,
        IAzureVmMetadataService vmMetadata,
        VmProviderRegistry providers,
        IActionGuard guard,
        IAnsiConsole console)
    {
        _azureAuth = azureAuth;
        _vmService = vmService;
        _vmMetadata = vmMetadata;
        _providers = providers;
        _guard = guard;
        _console = console;
    }

    public class Settings : VmSettings
    {
        [CommandArgument(0, "[VM_NAME]")]
        [Description("VM name or label (interactive if omitted)")]
        public string? VmName { get; set; }

        [CommandArgument(1, "[PORTS]")]
        [Description("Comma-separated TCP ports or ranges, e.g. 80,443 or 8000-8010 (default: 80,443)")]
        public string? Ports { get; set; }

        [CommandOption("--rule <NAME>")]
        [Description("Name of the NSG rule to create (default: Allow-<ports>)")]
        public string? RuleName { get; set; }
    }

    private static readonly Regex PortSpec = new(@"^\d{1,5}(-\d{1,5})?$", RegexOptions.Compiled);

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(settings).GetAwaiter().GetResult();
    }

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var ports = ParsePorts(settings.Ports ?? "80,443");
        if (ports is null)
        {
            _console.MarkupLine("[red]Invalid ports. Use e.g. 80,443 or 8000-8010 (1–65535).[/]");
            return 1;
        }

        var records = await _vmMetadata.ListAsync();
        if (records.Count == 0)
        {
            _console.MarkupLine("[red]No VMs tracked. Use 'pks vm init' to create a VM.[/]");
            return 1;
        }

        var vmName = settings.VmName;
        if (string.IsNullOrWhiteSpace(vmName))
        {
            vmName = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select a VM:[/]")
                    .AddChoices(records.Select(r => r.VmName)));
        }

        var record = await _vmMetadata.FindAsync(vmName);
        if (record == null)
        {
            _console.MarkupLine($"[red]VM '{Markup.Escape(vmName)}' not found in tracked VMs.[/]");
            return 1;
        }

        // The rule lives on the `<vm>-nsg` that `pks vm init` creates; other providers have no NSG.
        var provider = _providers.Resolve(record);
        if (provider.ProviderKey != "azure")
        {
            _console.MarkupLine($"[yellow]Opening ports is not supported for {Markup.Escape(provider.DisplayName)} VMs yet.[/]");
            return 1;
        }

        var portList = string.Join(",", ports);
        try { await _guard.RequireAsync(new ActionRequest(ActionIds.VmFirewallWrite, $"Open TCP {portList} on VM '{record.VmName}'")); }
        catch (ActionGuardDeniedException ex) { _console.MarkupLine($"[red]Denied:[/] {Markup.Escape(ex.Message)}"); return 1; }

        var token = await _azureAuth.GetAccessTokenAsync("https://management.azure.com/.default");
        if (string.IsNullOrEmpty(token))
        {
            _console.MarkupLine("[red]Failed to obtain Azure management token. Run 'pks azure init' first.[/]");
            return 1;
        }

        var ruleName = settings.RuleName ?? $"Allow-{string.Join("_", ports)}";
        try
        {
            await _console.Status().SpinnerStyle(Style.Parse("cyan")).Spinner(Spinner.Known.Dots)
                .StartAsync($"Opening TCP {portList} on {record.VmName}-nsg…", async _ =>
                    await _vmService.EnsureInboundPortsAsync(
                        token, record.SubscriptionId, record.ResourceGroup, record.VmName, ruleName, ports));
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Could not open ports:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        _console.MarkupLine($"[green]TCP {Markup.Escape(portList)} open[/] on {Markup.Escape(record.VmName)}" +
            (string.IsNullOrEmpty(record.PublicIpAddress) ? "" : $" [dim]({Markup.Escape(record.PublicIpAddress)})[/]"));
        return 0;
    }

    /// <summary>Splits "80,443" / "8000-8010" into NSG port specs; null if any part is invalid.</summary>
    internal static string[]? ParsePorts(string spec)
    {
        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        foreach (var part in parts)
        {
            if (!PortSpec.IsMatch(part)) return null;
            var bounds = part.Split('-').Select(int.Parse).ToArray();
            if (bounds.Any(p => p < 1 || p > 65535)) return null;
            if (bounds.Length == 2 && bounds[0] > bounds[1]) return null;
        }

        return parts.Distinct().ToArray();
    }
}
