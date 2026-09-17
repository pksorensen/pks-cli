using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.LogAnalytics;

[Description("Discover Log Analytics workspaces and choose which ones to enable for KQL queries")]
public class LogAnalyticsInitCommand : Command<LogAnalyticsInitCommand.Settings>
{
    public class Settings : LogAnalyticsSettings, IAzureResourceInitSettings
    {
        [CommandOption("-f|--force")]
        [Description("Kept for backward compatibility; same as a plain init. Never clears credentials.")]
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

        [CommandOption("--enable <NAME_OR_KEY>")]
        [Description("Enable a registered workspace by name or workspace id (repeatable). Skips discovery and the prompt.")]
        public string[] Enable { get; set; } = Array.Empty<string>();

        [CommandOption("--disable <NAME_OR_KEY>")]
        [Description("Disable a registered workspace by name or workspace id (repeatable). The entry is kept.")]
        public string[] Disable { get; set; } = Array.Empty<string>();

        [CommandOption("--list")]
        [Description("List every registered workspace and whether it is enabled")]
        public bool List { get; set; }

        [CommandOption("-w|--workspace <NAME_OR_GUID>")]
        [Description("A workspace GUID is registered and enabled directly; a name is enabled after discovery. Skips the prompt.")]
        public string? Workspace { get; set; }
    }

    private readonly IAzureResourceInitFlow _flow;
    private readonly IAzureResourceRegistry _registry;
    private readonly IAnsiConsole _console;

    public LogAnalyticsInitCommand(IAzureResourceInitFlow flow, IAzureResourceRegistry registry, IAnsiConsole console)
    {
        _flow = flow;
        _registry = registry;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var options = settings.ToOptions();
        var workspace = settings.Workspace?.Trim();

        // A bare GUID is already everything the query API needs — no ARM walk.
        if (!string.IsNullOrEmpty(workspace) && Guid.TryParse(workspace, out var directGuid))
        {
            var key = directGuid.ToString();
            await _registry.UpsertAsync(new[]
            {
                new AzureResourceEntry
                {
                    Kind = AzureResourceKind.LogAnalytics,
                    Key = key,
                    Name = key,
                    TenantId = null,
                    SubscriptionId = options.Subscription,
                    Enabled = true,
                },
            });
            await _registry.SetEnabledAsync(AzureResourceKind.LogAnalytics, key, true);
            _console.MarkupLine($"[green]✓ Enabled workspace:[/] [cyan]{key}[/]");
            _console.MarkupLine("[dim]Run [cyan]pks kusto \"Heartbeat | take 5\"[/] to query it.[/]");
            return 0;
        }

        if (!string.IsNullOrEmpty(workspace))
            options.EnableAfterDiscovery.Add(workspace);

        return await _flow.RunAsync(AzureResourceKind.LogAnalytics, options, _console);
    }
}
