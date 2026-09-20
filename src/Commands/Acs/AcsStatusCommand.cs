using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Acs;

[Description("Show the registered SMS senders, the default sender and recipient, whether the runner advertises sms, and the tenant sign-in state")]
public class AcsStatusCommand : Command<AcsStatusCommand.Settings>
{
    public class Settings : AcsSettings { }

    private readonly IAzureResourceRegistry _registry;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAcsSmsService _sms;
    private readonly IAnsiConsole _console;

    public AcsStatusCommand(IAzureResourceRegistry registry, IAzureTenantCredentialStore tenants, IAcsSmsService sms, IAnsiConsole console)
    {
        _registry = registry;
        _tenants = tenants;
        _sms = sms;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync()
    {
        var entries = await _registry.ListAsync(AzureResourceKind.CommunicationServices);
        await AzureResourceStatusRenderer.WriteEntriesAsync(_console, _tenants, AzureResourceKind.CommunicationServices, entries);

        var defaults = await _sms.GetDefaultsAsync();
        _console.WriteLine();
        _console.MarkupLine($"Default sender: {(defaults.From == null ? "[dim]not set[/]" : $"[cyan]{defaults.From.Key.EscapeMarkup()}[/]{(defaults.From.Enabled ? "" : " [yellow](disabled)[/]")}")}");
        _console.MarkupLine($"Recipient:      {(defaults.Recipient == null ? "[dim]not set[/]" : $"[cyan]{defaults.Recipient.EscapeMarkup()}[/]")}");
        _console.MarkupLine(defaults.IsConfigured
            ? "Runner capability [cyan]sms[/]: [green]advertised[/] (restart the runner if it is already running)"
            : "Runner capability [cyan]sms[/]: [yellow]not advertised[/] [dim]— run [cyan]pks acs init[/] to enable a sender and set a recipient[/]");

        await AzureResourceStatusRenderer.WriteTenantsAsync(_console, _tenants, AzureResourceKind.CommunicationServices);
        return 0;
    }
}
