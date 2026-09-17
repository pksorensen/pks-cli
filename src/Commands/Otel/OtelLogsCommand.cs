using System.ComponentModel;
using System.Text.Json;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List structured log entries from every enabled Application Insights resource")]
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

    private readonly IAzureResourceRegistry _registry;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelLogsCommand(
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

        var result = await _queryService.QueryLogsManyAsync(
            targets, settings.ParsedSince, settings.Severity, settings.TraceId, settings.AppName);
        var exit = OtelFanOut.ReportFailures(result, ErrorConsole ?? AzureQueryTargets.StderrConsole());
        var logs = result.Items;

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(logs, JsonOpts));
            return exit;
        }

        if (logs.Count == 0)
        {
            if (exit == 0)
                _console.MarkupLine("[dim]No log entries found in the specified time range.[/]");
            return exit;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Resource")
            .AddColumn("Timestamp")
            .AddColumn("Severity")
            .AddColumn("Message")
            .AddColumn("App");

        foreach (var l in logs)
        {
            var msg = l.Message.Length > 70 ? l.Message[..67] + "..." : l.Message;
            table.AddRow(
                l.Resource.EscapeMarkup(),
                l.Timestamp.ToString("HH:mm:ss"),
                l.Severity.EscapeMarkup(),
                msg.EscapeMarkup(),
                l.AppName.EscapeMarkup());
        }

        _console.Write(table);
        return exit;
    }
}
