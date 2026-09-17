using System.ComponentModel;
using System.Text.Json;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List recent exceptions from every enabled Application Insights resource")]
public class OtelErrorsCommand : Command<OtelErrorsCommand.Settings>
{
    public class Settings : OtelQuerySettings
    {
        [CommandOption("--operation-id <ID>")]
        [Description("Filter by operation/correlation ID")]
        public string? OperationId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAzureResourceRegistry _registry;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelErrorsCommand(
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

        var since = settings.ParsedSince;

        if (settings.Verbose)
        {
            var kql = AppInsightsQueryService.BuildErrorsKql(since, settings.Limit, settings.AppName, settings.OperationId);
            _console.MarkupLine($"[dim]Resources: {string.Join(", ", targets.Select(t => $"{t.Name} ({t.Key})")).EscapeMarkup()}[/]");
            _console.MarkupLine($"[dim]Window   : {since.TotalHours:0.#}h ({settings.Since})[/]");
            _console.MarkupLine($"[dim]KQL      :[/]");
            _console.MarkupLine($"[dim]{kql.EscapeMarkup()}[/]");
            _console.WriteLine();
        }

        var result = await _queryService.QueryErrorsManyAsync(
            targets, since, settings.Limit, settings.AppName, settings.OperationId);
        var exit = OtelFanOut.ReportFailures(result, ErrorConsole ?? AzureQueryTargets.StderrConsole());
        var errors = result.Items;

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(errors, JsonOpts));
            return exit;
        }

        if (errors.Count == 0)
        {
            if (exit == 0)
                _console.MarkupLine("[dim]No exceptions found in the specified time range.[/]");
            return exit;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Resource")
            .AddColumn("Timestamp")
            .AddColumn("Exception Type")
            .AddColumn("Message")
            .AddColumn("Operation ID")
            .AddColumn("App");

        foreach (var e in errors)
        {
            var msg = e.Message.Length > 60 ? e.Message[..57] + "..." : e.Message;
            var opId = e.OperationId.Length > 16 ? e.OperationId[..16] : e.OperationId;
            table.AddRow(
                e.Resource.EscapeMarkup(),
                e.Timestamp.ToString("HH:mm:ss"),
                e.ExceptionType.EscapeMarkup(),
                msg.EscapeMarkup(),
                opId.EscapeMarkup(),
                e.AppName.EscapeMarkup());
        }

        _console.Write(table);
        return exit;
    }
}
