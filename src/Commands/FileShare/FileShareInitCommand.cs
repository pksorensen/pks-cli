using System.ComponentModel;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.FileShares;

[Description("Sign in to Azure and choose which storage accounts to enable")]
public class FileShareInitCommand : Command<FileShareInitCommand.Settings>
{
    private readonly FileShareProviderRegistry _registry;
    private readonly IAzureResourceInitFlow _flow;
    private readonly IAnsiConsole _console;

    public FileShareInitCommand(FileShareProviderRegistry registry, IAzureResourceInitFlow flow, IAnsiConsole console)
    {
        _registry = registry;
        _flow = flow;
        _console = console;
    }

    public class Settings : FileShareSettings, IAzureResourceInitSettings
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
        [Description("Enable a registered storage account by name (repeatable). Skips discovery and the prompt.")]
        public string[] Enable { get; set; } = Array.Empty<string>();

        [CommandOption("--disable <NAME_OR_KEY>")]
        [Description("Disable a registered storage account by name (repeatable). The entry is kept.")]
        public string[] Disable { get; set; } = Array.Empty<string>();

        [CommandOption("--list")]
        [Description("List every registered storage account and whether it is enabled")]
        public bool List { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var providers = _registry.GetAllProviders().ToList();
        if (providers.Count == 0)
        {
            _console.MarkupLine("[red]No file share providers are registered.[/]");
            return 1;
        }

        // Only one provider exists today; the selection stays so a second one plugs in here.
        var provider = providers.Count == 1
            ? providers[0]
            : SelectProvider(providers);

        if (settings.Verbose)
            _console.MarkupLine($"[dim]Provider: {Markup.Escape(provider.ProviderName)}[/]");

        return await _flow.RunAsync(AzureResourceKind.Storage, settings.ToOptions(), _console);
    }

    private IFileShareProvider SelectProvider(List<IFileShareProvider> providers)
    {
        var providerName = _console.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a file share provider:[/]")
                .AddChoices(providers.Select(p => p.ProviderName)));
        return providers.First(p => p.ProviderName == providerName);
    }
}
