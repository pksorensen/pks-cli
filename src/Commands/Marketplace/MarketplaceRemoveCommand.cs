using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("Remove a Claude Code plugin marketplace")]
public class MarketplaceRemoveCommand : AsyncCommand<MarketplaceRemoveCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[ID]")]
        [Description("Marketplace ID to remove (omit to pick interactively)")]
        public string? Id { get; set; }

        [CommandOption("--yes")]
        [Description("Skip confirmation prompt")]
        public bool Yes { get; set; }
    }

    public MarketplaceRemoveCommand(
        IClaudeMarketplaceConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        string id;

        if (string.IsNullOrWhiteSpace(settings.Id))
        {
            var all = await _configService.ListMarketplacesAsync();
            if (all.Count == 0)
            {
                _console.MarkupLine("[yellow]No marketplaces registered.[/]");
                return 0;
            }

            id = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select marketplace to remove:[/]")
                    .HighlightStyle(Style.Parse("cyan"))
                    .AddChoices(all.Select(m => m.Id)));
        }
        else
        {
            id = settings.Id;
        }

        var marketplace = await _configService.FindMarketplaceAsync(id);
        if (marketplace == null)
        {
            _console.MarkupLine($"[red]Marketplace '{id.EscapeMarkup()}' not found.[/]");
            return 1;
        }

        if (!settings.Yes && !_console.Confirm($"Remove marketplace '{id}'?", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _configService.RemoveMarketplaceAsync(id);
        _console.MarkupLine($"[green]Marketplace '{id.EscapeMarkup()}' removed.[/]");
        return 0;
    }
}
