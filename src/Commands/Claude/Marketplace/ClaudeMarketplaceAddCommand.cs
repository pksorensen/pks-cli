using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Claude.Marketplace;

[Description("Add a Claude Code plugin marketplace")]
public class ClaudeMarketplaceAddCommand : AsyncCommand<ClaudeMarketplaceAddCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IClaudeMarketplaceFetcher _fetcher;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<SOURCE>")]
        [Description("Marketplace source (URL, github:owner/repo, github:owner/repo@ref)")]
        public string Source { get; set; } = "";

        [CommandOption("--label <LABEL>")]
        [Description("Optional display label for the marketplace")]
        public string? Label { get; set; }

        [CommandOption("--non-interactive")]
        [Description("Skip interactive prompts")]
        public bool NonInteractive { get; set; }

        [CommandOption("--enable-all")]
        [Description("Enable all plugins when adding (only used with --non-interactive)")]
        public bool EnableAll { get; set; }
    }

    public ClaudeMarketplaceAddCommand(
        IClaudeMarketplaceConfigurationService configService,
        IClaudeMarketplaceFetcher fetcher,
        IAnsiConsole console)
    {
        _configService = configService;
        _fetcher = fetcher;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            _console.MarkupLine($"[cyan]Fetching marketplace from:[/] {settings.Source.EscapeMarkup()}");

            var source = _fetcher.ParseSource(settings.Source);
            var marketplaceJson = await _fetcher.FetchAsync(source);

            var plugins = marketplaceJson.Plugins.Select(p => new ClaudeMarketplacePluginSnapshot
            {
                Name = p.Name,
                Version = p.Version,
                Description = p.Description,
                Enabled = settings.NonInteractive ? settings.EnableAll : false
            }).ToList();

            // Interactive plugin selection
            if (!settings.NonInteractive && plugins.Count > 0)
            {
                var selected = _console.Prompt(
                    new MultiSelectionPrompt<string>()
                        .Title("Select plugins to enable:")
                        .NotRequired()
                        .AddChoices(plugins.Select(p => p.Name)));

                foreach (var plugin in plugins)
                    plugin.Enabled = selected.Contains(plugin.Name);
            }

            var marketplace = new ClaudeMarketplace
            {
                Id = marketplaceJson.Id,
                Label = settings.Label ?? marketplaceJson.Label,
                Source = source,
                AddedAt = DateTime.UtcNow,
                LastFetchedAt = DateTime.UtcNow,
                Plugins = plugins
            };

            await _configService.AddOrUpdateMarketplaceAsync(marketplace);

            _console.MarkupLine($"[green]Marketplace '{marketplace.Id.EscapeMarkup()}' added successfully.[/]");
            _console.MarkupLine($"[dim]Plugins: {plugins.Count} total, {plugins.Count(p => p.Enabled)} enabled[/]");

            return 0;
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[red]Error adding marketplace: {ex.Message.EscapeMarkup()}[/]");
            return 1;
        }
    }
}
