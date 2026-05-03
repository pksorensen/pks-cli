using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Claude.Marketplace;

[Description("Refresh plugin metadata from a Claude Code marketplace source")]
public class ClaudeMarketplaceRefreshCommand : AsyncCommand<ClaudeMarketplaceRefreshCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IClaudeMarketplaceFetcher _fetcher;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[ID]")]
        [Description("Marketplace ID to refresh (omit to refresh all)")]
        public string? Id { get; set; }
    }

    public ClaudeMarketplaceRefreshCommand(
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
        var marketplaces = await _configService.ListMarketplacesAsync();

        if (!string.IsNullOrEmpty(settings.Id))
        {
            marketplaces = marketplaces
                .Where(m => string.Equals(m.Id, settings.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (marketplaces.Count == 0)
            {
                _console.MarkupLine($"[red]Marketplace '{settings.Id.EscapeMarkup()}' not found.[/]");
                return 1;
            }
        }

        var errors = 0;
        foreach (var marketplace in marketplaces)
        {
            try
            {
                _console.MarkupLine($"[dim]Refreshing {marketplace.Id.EscapeMarkup()}...[/]");
                var fetched = await _fetcher.FetchAsync(marketplace.Source);

                // Preserve enabled state for existing plugins, add new ones as disabled
                var existingEnabled = marketplace.Plugins
                    .Where(p => p.Enabled)
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                marketplace.Plugins = fetched.Plugins.Select(p => new ClaudeMarketplacePluginSnapshot
                {
                    Name = p.Name,
                    Version = p.Version,
                    Description = p.Description,
                    Enabled = existingEnabled.Contains(p.Name)
                }).ToList();

                marketplace.LastFetchedAt = DateTime.UtcNow;
                await _configService.AddOrUpdateMarketplaceAsync(marketplace);
                _console.MarkupLine($"[green]Refreshed {marketplace.Id.EscapeMarkup()} ({fetched.Plugins.Count} plugins)[/]");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red]Failed to refresh {marketplace.Id.EscapeMarkup()}: {ex.Message.EscapeMarkup()}[/]");
                errors++;
            }
        }

        return errors > 0 ? 1 : 0;
    }
}
