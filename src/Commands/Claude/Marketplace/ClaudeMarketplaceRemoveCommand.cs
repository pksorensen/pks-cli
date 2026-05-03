using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Claude.Marketplace;

[Description("Remove a Claude Code plugin marketplace")]
public class ClaudeMarketplaceRemoveCommand : AsyncCommand<ClaudeMarketplaceRemoveCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Marketplace ID to remove")]
        public string Id { get; set; } = "";

        [CommandOption("--yes")]
        [Description("Skip confirmation prompt")]
        public bool Yes { get; set; }
    }

    public ClaudeMarketplaceRemoveCommand(
        IClaudeMarketplaceConfigurationService configService,
        IAnsiConsole console)
    {
        _configService = configService;
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var marketplace = await _configService.FindMarketplaceAsync(settings.Id);
        if (marketplace == null)
        {
            _console.MarkupLine($"[red]Marketplace '{settings.Id.EscapeMarkup()}' not found.[/]");
            return 1;
        }

        if (!settings.Yes && !_console.Confirm($"Remove marketplace '{settings.Id}'?", defaultValue: false))
        {
            _console.MarkupLine("[dim]Cancelled.[/]");
            return 0;
        }

        await _configService.RemoveMarketplaceAsync(settings.Id);
        _console.MarkupLine($"[green]Marketplace '{settings.Id.EscapeMarkup()}' removed.[/]");
        return 0;
    }
}
