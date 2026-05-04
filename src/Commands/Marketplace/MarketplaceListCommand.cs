using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("List registered Claude Code plugin marketplaces")]
public class MarketplaceListCommand : AsyncCommand<MarketplaceListCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings { }

    public MarketplaceListCommand(
        IClaudeMarketplaceConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var marketplaces = await _configService.ListMarketplacesAsync();

        if (marketplaces.Count == 0)
        {
            _console.MarkupLine("[yellow]No marketplaces registered. Use 'pks claude marketplace add <source>' to add one.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold cyan]Claude Code Marketplaces[/]")
            .AddColumn("ID")
            .AddColumn("Label")
            .AddColumn("Source")
            .AddColumn("Plugins")
            .AddColumn("Added");

        foreach (var m in marketplaces)
        {
            var enabledCount = m.Plugins.Count(p => p.Enabled);
            table.AddRow(
                m.Id.EscapeMarkup(),
                (m.Label ?? "").EscapeMarkup(),
                (m.Source.Url ?? m.Source.Repo ?? m.Source.SourceType).EscapeMarkup(),
                $"{enabledCount}/{m.Plugins.Count} enabled",
                m.AddedAt.ToString("yyyy-MM-dd"));
        }

        _console.Write(table);
        return 0;
    }
}
