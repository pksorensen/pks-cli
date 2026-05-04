using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("Disable plugins in a Claude Code marketplace")]
public class MarketplaceDisableCommand : AsyncCommand<MarketplaceDisableCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<MARKETPLACE_ID>")]
        [Description("Marketplace ID")]
        public string MarketplaceId { get; set; } = "";

        [CommandArgument(1, "[PLUGIN_NAMES]")]
        [Description("Plugin names to disable (omit to disable all)")]
        public string[]? PluginNames { get; set; }
    }

    public MarketplaceDisableCommand(
        IClaudeMarketplaceConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var marketplace = await _configService.FindMarketplaceAsync(settings.MarketplaceId);
        if (marketplace == null)
        {
            _console.MarkupLine($"[red]Marketplace '{settings.MarketplaceId.EscapeMarkup()}' not found.[/]");
            return 1;
        }

        var pluginNames = settings.PluginNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in marketplace.Plugins)
        {
            if (pluginNames == null || pluginNames.Contains(plugin.Name))
                plugin.Enabled = false;
        }

        await _configService.AddOrUpdateMarketplaceAsync(marketplace);
        _console.MarkupLine("[green]Plugins disabled.[/]");
        return 0;
    }
}
