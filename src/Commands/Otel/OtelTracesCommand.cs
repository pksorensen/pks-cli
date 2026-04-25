using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List recent requests/traces from Application Insights")]
public class OtelTracesCommand : Command<OtelTracesCommand.Settings>
{
    public class Settings : OtelQuerySettings
    {
        [CommandOption("--has-error")]
        [Description("Only show requests that resulted in errors")]
        public bool HasError { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelTracesCommand(
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

        var traces = await _queryService.QueryTracesAsync(
            settings.ParsedSince, settings.Limit,
            settings.HasError ? true : null,
            settings.AppName);

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(traces, JsonOpts));
            return 0;
        }

        if (traces.Count == 0)
        {
            _console.MarkupLine("[dim]No traces found in the specified time range.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Timestamp")
            .AddColumn("Name")
            .AddColumn("App")
            .AddColumn("Duration (ms)")
            .AddColumn("Status")
            .AddColumn("Code");

        foreach (var t in traces)
        {
            var status = t.Success ? "[green]✓[/]" : "[red]✗[/]";
            table.AddRow(
                t.Timestamp.ToString("HH:mm:ss"),
                t.Name.EscapeMarkup(),
                t.AppName.EscapeMarkup(),
                $"{t.DurationMs:F1}",
                status,
                (t.ResultCode ?? "").EscapeMarkup());
        }

        _console.Write(table);
        return 0;
    }
}
