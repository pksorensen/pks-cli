using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.AppInsights;

[Description("Discover Application Insights resources and choose which ones to enable for telemetry queries")]
public class AppInsightsInitCommand : Command<AppInsightsInitCommand.Settings>
{
    public class Settings : AppInsightsSettings, IAzureResourceInitSettings
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
        [Description("Enable a registered resource by name or app id (repeatable). Skips discovery and the prompt.")]
        public string[] Enable { get; set; } = Array.Empty<string>();

        [CommandOption("--disable <NAME_OR_KEY>")]
        [Description("Disable a registered resource by name or app id (repeatable). The entry is kept.")]
        public string[] Disable { get; set; } = Array.Empty<string>();

        [CommandOption("--list")]
        [Description("List every registered resource and whether it is enabled")]
        public bool List { get; set; }
    }

    private readonly IAzureResourceInitFlow _flow;
    private readonly IAnsiConsole _console;

    public AppInsightsInitCommand(IAzureResourceInitFlow flow, IAnsiConsole console)
    {
        _flow = flow;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => _flow.RunAsync(AzureResourceKind.AppInsights, settings.ToOptions(), _console).GetAwaiter().GetResult();
}
