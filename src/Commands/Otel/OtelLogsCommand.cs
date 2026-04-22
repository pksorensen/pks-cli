using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List structured log entries from Application Insights")]
public class OtelLogsCommand : Command<OtelLogsCommand.Settings>
{
    public class Settings : OtelQuerySettings
    {
        [CommandOption("--severity <LEVEL>")]
        [Description("Minimum severity: Trace, Info, Warning, Error, Critical")]
        public string? Severity { get; set; }

        [CommandOption("--trace-id <ID>")]
        [Description("Filter by trace/operation ID")]
        public string? TraceId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelLogsCommand(
        IAppInsightsConfigService configService,
        IAppInsightsQueryService queryService,
        IAnsiConsole console)
    {
        _configService = configService;
        _queryService = queryService;
        _console = console;
    }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        if (!await _configService.IsConfiguredAsync())
        {
            _console.MarkupLine("[yellow]Application Insights is not configured.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks appinsights init[/] to configure.[/]");
            return 1;
        }

        var logs = await _queryService.QueryLogsAsync(
            settings.ParsedSince, settings.Severity, settings.TraceId, settings.AppName);

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(logs, JsonOpts));
            return 0;
        }

        if (logs.Count == 0)
        {
            _console.MarkupLine("[dim]No log entries found in the specified time range.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Timestamp")
            .AddColumn("Severity")
            .AddColumn("Message")
            .AddColumn("App");

        foreach (var l in logs)
        {
            var msg = l.Message.Length > 70 ? l.Message[..67] + "..." : l.Message;
            table.AddRow(
                l.Timestamp.ToString("HH:mm:ss"),
                l.Severity.EscapeMarkup(),
                msg.EscapeMarkup(),
                l.AppName.EscapeMarkup());
        }

        _console.Write(table);
        return 0;
    }
}
