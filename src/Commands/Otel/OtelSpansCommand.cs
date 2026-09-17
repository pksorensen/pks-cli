using System.ComponentModel;
using System.Text.Json;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List spans for a specific trace from every enabled Application Insights resource")]
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

    private readonly IAzureResourceRegistry _registry;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelSpansCommand(
        IAzureResourceRegistry registry,
        IAppInsightsQueryService queryService,
        IAnsiConsole console)
    {
        _registry = registry;
        _queryService = queryService;
        _console = console;
    }

    /// <summary>Where per-resource failures go: stderr, so Json on stdout stays parseable. Tests inject a capture.</summary>
    internal IAnsiConsole? ErrorConsole { get; init; }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var targets = await OtelFanOut.ResolveAsync(_registry, settings, _console);
        if (targets is null)
            return 1;

        var result = await _queryService.QuerySpansManyAsync(targets, settings.OperationId!);
        var exit = OtelFanOut.ReportFailures(result, ErrorConsole ?? AzureQueryTargets.StderrConsole());
        var spans = result.Items;

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(spans, JsonOpts));
            return exit;
        }

        if (spans.Count == 0)
        {
            if (exit == 0)
                _console.MarkupLine("[dim]No spans found for the specified operation ID.[/]");
            return exit;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Resource")
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
                s.Resource.EscapeMarkup(),
                s.Timestamp.ToString("HH:mm:ss"),
                s.SpanId.EscapeMarkup(),
                s.Name.EscapeMarkup(),
                s.Type.EscapeMarkup(),
                (s.Target ?? "").EscapeMarkup(),
                $"{s.DurationMs:F1}",
                status);
        }

        _console.Write(table);
        return exit;
    }
}
