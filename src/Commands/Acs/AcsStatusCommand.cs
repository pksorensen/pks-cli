using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Runner;
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
    private readonly IAgenticsRunnerConfigurationService _runners;
    private readonly IAnsiConsole _console;

    public AcsStatusCommand(
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        IAcsSmsService sms,
        IAgenticsRunnerConfigurationService runners,
        IAnsiConsole console)
    {
        _registry = registry;
        _tenants = tenants;
        _sms = sms;
        _runners = runners;
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

        // A saved operator profile *narrows* the advertised list: a runner configured before
        // sms existed carries a capability list without it and would drop it silently on
        // every poll, however well ACS is set up. Name those runners rather than let the
        // first live test end in "queued" and no SMS.
        if (defaults.IsConfigured)
        {
            foreach (var registration in await _runners.ListRegistrationsAsync())
            {
                var saved = registration.Profile?.Capabilities;
                if (saved == null || saved.Contains(PKS.Commands.Agentics.Runner.AgenticsRunnerRunCommand.SmsCapability)) continue;

                _console.MarkupLine(
                    $"  [yellow]![/] runner [cyan]{registration.Name.EscapeMarkup()}[/] ({registration.Owner.EscapeMarkup()}/{registration.Project.EscapeMarkup()}) " +
                    "has a saved capability list without [cyan]sms[/] — it will not advertise it. " +
                    "Re-run [cyan]pks agentics runner run --configure[/] and tick sms.");
            }
        }

        await AzureResourceStatusRenderer.WriteTenantsAsync(_console, _tenants, AzureResourceKind.CommunicationServices);
        return 0;
    }
}
