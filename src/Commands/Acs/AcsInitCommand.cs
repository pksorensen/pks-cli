using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Acs;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Acs;

[Description("Sign in to Azure, find the SMS-capable phone numbers on your Communication Services resources and choose the senders and the recipient this host uses")]
public class AcsInitCommand : Command<AcsInitCommand.Settings>
{
    public class Settings : AcsSettings, IAzureResourceInitSettings
    {
        [CommandOption("-f|--force")]
        [Description("Kept for symmetry with the other init commands; same as a plain init. Never clears credentials.")]
        public bool Force { get; set; }

        [CommandOption("--reauth [TENANT]")]
        [Description("Sign in again in the browser, optionally to one tenant id or email. Without a value every known tenant is signed in again.")]
        public FlagValue<string>? Reauth { get; set; }

        [CommandOption("-t|--tenant <ID_OR_EMAIL>")]
        [Description("Limit to one tenant (id or email). Signs in only if the tenant is not known yet.")]
        public string? Tenant { get; set; }

        [CommandOption("-s|--subscription <ID>")]
        [Description("Limit discovery to one subscription (id or display name)")]
        public string? Subscription { get; set; }

        [CommandOption("--enable <SENDER>")]
        [Description("Enable a registered sender (phone number or alphanumeric id) without discovery or prompt (repeatable)")]
        public string[] Enable { get; set; } = Array.Empty<string>();

        [CommandOption("--disable <SENDER>")]
        [Description("Disable a registered sender (repeatable). The entry is kept.")]
        public string[] Disable { get; set; } = Array.Empty<string>();

        [CommandOption("--list")]
        [Description("List every registered sender and whether it is enabled")]
        public bool List { get; set; }
    }

    private readonly IAzureResourceInitFlow _flow;
    private readonly IAzureResourceRegistry _registry;
    private readonly IAzureTenantCredentialStore _tenants;
    private readonly IAzureArmDiscovery _discovery;
    private readonly IAcsSmsService _sms;
    private readonly IAnsiConsole _console;

    public AcsInitCommand(
        IAzureResourceInitFlow flow,
        IAzureResourceRegistry registry,
        IAzureTenantCredentialStore tenants,
        IAzureArmDiscovery discovery,
        IAcsSmsService sms,
        IAnsiConsole console)
    {
        _flow = flow;
        _registry = registry;
        _tenants = tenants;
        _discovery = discovery;
        _sms = sms;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var options = settings.ToOptions();
        var rc = await _flow.RunAsync(AzureResourceKind.CommunicationServices, options, _console);
        if (rc != 0) return rc;

        // --list / --enable / --disable are complete on their own; only a real init continues into
        // the sender-id, default-sender, recipient and test-send questions.
        var flagsOnly = options.List || options.Enable.Count > 0 || options.Disable.Count > 0;
        if (flagsOnly || !_console.Profile.Capabilities.Interactive) return 0;

        await OfferAlphanumericSenderAsync(options);
        await ChooseDefaultsAsync();
        await OfferTestSendAsync();
        return 0;
    }

    // ── alphanumeric sender id ─────────────────────────────────────────────────

    private async Task OfferAlphanumericSenderAsync(AzureResourceInitOptions options)
    {
        var resources = await KnownResourcesAsync(options);
        if (resources.Count == 0) return;

        _console.WriteLine();
        _console.MarkupLine("[dim]An alphanumeric sender id (e.g. [cyan]Agentics[/], up to 11 letters/digits) needs no phone number and works for one-way SMS to Denmark and most of Europe.[/]");
        if (!_console.Confirm("Add an alphanumeric sender id?", defaultValue: false)) return;

        var id = _console.Prompt(new TextPrompt<string>("Sender id:")
            .Validate(v => AcsSmsService.IsValidAlphanumericSender(v.Trim())
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]1–11 ASCII letters or digits, at least one letter[/]")))
            .Trim();

        var resource = resources.Count == 1
            ? resources[0]
            : _console.Prompt(new SelectionPrompt<CommunicationServiceResource>()
                .Title("Which Communication Services resource sends it?")
                .UseConverter(r => $"{r.Name.EscapeMarkup()}  [dim]({r.Properties.HostName.EscapeMarkup()})[/]")
                .AddChoices(resources));

        var entry = AzureResourceInitFlow.SmsSenderEntry(resource.TenantId, resource.Subscription, resource, id, $"{id} · alphanumeric · {resource.Name}");
        entry.Enabled = true;
        await _registry.UpsertAsync(new[] { entry });
        await _registry.SetEnabledAsync(AzureResourceKind.CommunicationServices, id, true);
        _console.MarkupLine($"[green]✓ Sender id[/] [cyan]{id.EscapeMarkup()}[/] [green]enabled on {resource.Name.EscapeMarkup()}[/]");
    }

    /// <summary>The ACS resources we can attach an alphanumeric id to: those already behind a
    /// registry entry, or — when discovery found no SMS-capable number anywhere — an ARM walk of
    /// the tenants and subscriptions in scope.</summary>
    private async Task<List<CommunicationServiceResource>> KnownResourcesAsync(AzureResourceInitOptions options)
    {
        var fromRegistry = (await _registry.ListAsync(AzureResourceKind.CommunicationServices))
            .Where(e => !string.IsNullOrEmpty(e.Endpoint))
            .GroupBy(e => e.Endpoint!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var armId = first.ResourceId?.Split("/smsSenders/")[0] ?? "";
                var name = armId.Split('/').LastOrDefault() ?? first.Endpoint!;
                return new CommunicationServiceResource
                {
                    Id = armId,
                    Name = name,
                    Properties = new CommunicationServiceProperties { HostName = first.Endpoint! },
                    TenantId = first.TenantId,
                    Subscription = first.SubscriptionId == null ? null : new AzureSubscription { SubscriptionId = first.SubscriptionId, DisplayName = first.SubscriptionName ?? "" },
                };
            })
            .ToList();
        if (fromRegistry.Count > 0) return fromRegistry;

        var found = new List<CommunicationServiceResource>();
        try
        {
            var tenants = options.Tenant != null && !options.Tenant.Contains('@')
                ? new[] { options.Tenant }
                : (await _tenants.ListTenantsAsync()).Select(t => t.TenantId).ToArray();
            foreach (var tenantId in tenants)
            {
                var subscriptions = await _discovery.ListSubscriptionsAsync(tenantId);
                foreach (var sub in subscriptions)
                {
                    if (options.Subscription != null
                        && !sub.SubscriptionId.Equals(options.Subscription, StringComparison.OrdinalIgnoreCase)
                        && !sub.DisplayName.Equals(options.Subscription, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var r in await _discovery.ListCommunicationServicesAsync(tenantId, sub.SubscriptionId))
                    {
                        r.TenantId = tenantId;
                        r.Subscription = sub;
                        found.Add(r);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[yellow]Could not list Communication Services resources for an alphanumeric sender: {ex.Message.EscapeMarkup()}[/]");
        }
        return found;
    }

    // ── defaults ──────────────────────────────────────────────────────────────

    private async Task ChooseDefaultsAsync()
    {
        var enabled = await _registry.ListEnabledAsync(AzureResourceKind.CommunicationServices);
        var defaults = await _sms.GetDefaultsAsync();

        string? from = null;
        if (enabled.Count == 1)
        {
            from = enabled[0].Key;
        }
        else if (enabled.Count > 1)
        {
            var prompt = new SelectionPrompt<AzureResourceEntry>()
                .Title("Default sender for runner notifications:")
                .UseConverter(e => e.Name.EscapeMarkup());
            var ordered = enabled.OrderBy(e => e.Key == defaults.From?.Key ? 0 : 1).ToList();
            prompt.AddChoices(ordered);
            from = _console.Prompt(prompt).Key;
        }

        var recipientPrompt = new TextPrompt<string>("Who should receive this runner's SMS notifications? [dim](E.164, e.g. +4512345678)[/]")
            .Validate(v => AcsSmsService.IsE164(v.Trim()) ? ValidationResult.Success() : ValidationResult.Error("[red]Use E.164: a plus sign and 7–15 digits[/]"));
        if (!string.IsNullOrWhiteSpace(defaults.Recipient)) recipientPrompt.DefaultValue(defaults.Recipient);
        var recipient = _console.Prompt(recipientPrompt).Trim();

        await _sms.SetDefaultsAsync(from, recipient);
        if (from != null) _console.MarkupLine($"[green]✓ Default sender:[/] [cyan]{from.EscapeMarkup()}[/]");
        _console.MarkupLine($"[green]✓ Recipient:[/] [cyan]{recipient.EscapeMarkup()}[/]");
        if (from == null)
            _console.MarkupLine("[yellow]No sender enabled yet — the runner will not advertise SMS until one is.[/]");
    }

    // ── test send ─────────────────────────────────────────────────────────────

    private async Task OfferTestSendAsync()
    {
        if (!await _sms.IsConfiguredAsync()) return;
        if (!_console.Confirm("Send a test SMS now?", defaultValue: true)) return;

        var result = await _console.Status().StartAsync("Sending...", _ => _sms.SendAsync("pks acs: test message. SMS notifications from this runner work."));
        if (result.Ok)
            _console.MarkupLine($"[green]✓ Sent[/] [dim](message id {result.MessageId.EscapeMarkupOrDash()})[/]. The runner advertises [cyan]sms[/] on its next start.");
        else
            _console.MarkupLine($"[red]Send failed:[/] {result.Error.EscapeMarkupOrDash()}");
    }
}

internal static class AcsMarkupExtensions
{
    public static string EscapeMarkupOrDash(this string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.EscapeMarkup();
}
