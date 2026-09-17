using System.ComponentModel;
using System.Text.Json;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List recent requests/traces from every enabled Application Insights resource")]
public class OtelTracesCommand : Command<OtelTracesCommand.Settings>
{
    public class Settings : OtelQuerySettings
    {
        [CommandOption("--has-error")]
        [Description("Only show requests that resulted in errors")]
        public bool HasError { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAzureResourceRegistry _registry;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelTracesCommand(
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

        if (settings.Verbose)
            _console.MarkupLine($"[dim]Resources: {string.Join(", ", targets.Select(t => $"{t.Name} ({t.Key})")).EscapeMarkup()}[/]");

        var result = await _queryService.QueryTracesManyAsync(
            targets, settings.ParsedSince, settings.Limit,
            settings.HasError ? true : null,
            settings.AppName);
        var exit = OtelFanOut.ReportFailures(result, ErrorConsole ?? AzureQueryTargets.StderrConsole());
        var traces = result.Items;

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(traces, JsonOpts));
            return exit;
        }

        if (traces.Count == 0)
        {
            if (exit == 0)
                _console.MarkupLine("[dim]No traces found in the specified time range.[/]");
            return exit;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Resource")
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
                t.Resource.EscapeMarkup(),
                t.Timestamp.ToString("HH:mm:ss"),
                t.Name.EscapeMarkup(),
                t.AppName.EscapeMarkup(),
                $"{t.DurationMs:F1}",
                status,
                (t.ResultCode ?? "").EscapeMarkup());
        }

        _console.Write(table);
        return exit;
    }
}
