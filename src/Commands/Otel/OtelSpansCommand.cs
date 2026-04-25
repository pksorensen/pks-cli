using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List spans for a specific trace from Application Insights")]
public class OtelSpansCommand : Command<OtelSpansCommand.Settings>
{
    public class Settings : OtelSettings
    {
        [CommandOption("--operation-id <ID>")]
        [Description("Operation/trace ID to fetch spans for (required)")]
        public string? OperationId { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("Output format: Table or Json (default: Table)")]
        [DefaultValue("Table")]
        public string Format { get; set; } = "Table";

        public override ValidationResult Validate()
            => string.IsNullOrWhiteSpace(OperationId)
                ? ValidationResult.Error("--operation-id is required for spans")
                : ValidationResult.Success();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelSpansCommand(
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

        var spans = await _queryService.QuerySpansAsync(settings.OperationId!);

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(spans, JsonOpts));
            return 0;
        }

        if (spans.Count == 0)
        {
            _console.MarkupLine("[dim]No spans found for the specified operation ID.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Timestamp")
            .AddColumn("Span ID")
            .AddColumn("Name")
            .AddColumn("Type")
            .AddColumn("Target")
            .AddColumn("Duration (ms)")
            .AddColumn("Status");

        foreach (var s in spans)
        {
            var status = s.Success ? "[green]✓[/]" : "[red]✗[/]";
            table.AddRow(
                s.Timestamp.ToString("HH:mm:ss"),
                s.SpanId.EscapeMarkup(),
                s.Name.EscapeMarkup(),
                s.Type.EscapeMarkup(),
                (s.Target ?? "").EscapeMarkup(),
                $"{s.DurationMs:F1}",
                status);
        }

        _console.Write(table);
        return 0;
    }
}
