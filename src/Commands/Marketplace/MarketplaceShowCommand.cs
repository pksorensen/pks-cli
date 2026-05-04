using PKS.Infrastructure.Services.Claude;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace PKS.Commands.Marketplace;

[Description("Show details of a Claude Code plugin marketplace")]
public class MarketplaceShowCommand : AsyncCommand<MarketplaceShowCommand.Settings>
{
    private readonly IClaudeMarketplaceConfigurationService _configService;
    private readonly IAnsiConsole _console;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<ID>")]
        [Description("Marketplace ID")]
        public string Id { get; set; } = "";
    }

    public MarketplaceShowCommand(
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

        _console.MarkupLine($"[bold cyan]Marketplace:[/] {marketplace.Id.EscapeMarkup()}");
        _console.MarkupLine($"[cyan]Label:[/] {(marketplace.Label ?? "(none)").EscapeMarkup()}");
        _console.MarkupLine($"[cyan]Source Type:[/] {marketplace.Source.SourceType.EscapeMarkup()}");
        if (marketplace.Source.Url != null)
            _console.MarkupLine($"[cyan]URL:[/] {marketplace.Source.Url.EscapeMarkup()}");
        if (marketplace.Source.Repo != null)
            _console.MarkupLine($"[cyan]Repo:[/] {marketplace.Source.Repo.EscapeMarkup()} @ {(marketplace.Source.Ref ?? "main").EscapeMarkup()}");
        _console.MarkupLine($"[cyan]Added:[/] {marketplace.AddedAt:yyyy-MM-dd HH:mm:ss} UTC");
        if (marketplace.LastFetchedAt.HasValue)
            _console.MarkupLine($"[cyan]Last Fetched:[/] {marketplace.LastFetchedAt.Value:yyyy-MM-dd HH:mm:ss} UTC");

        if (marketplace.Plugins.Count > 0)
        {
            _console.WriteLine();
            var table = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold]Plugins[/]")
                .AddColumn("Name")
                .AddColumn("Version")
                .AddColumn("Enabled")
                .AddColumn("Description");

            foreach (var plugin in marketplace.Plugins)
            {
                table.AddRow(
                    plugin.Name.EscapeMarkup(),
                    (plugin.Version ?? "").EscapeMarkup(),
                    plugin.Enabled ? "[green]yes[/]" : "[dim]no[/]",
                    (plugin.Description ?? "").EscapeMarkup());
            }
            _console.Write(table);
        }

        return 0;
    }
}
