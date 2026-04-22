using System.ComponentModel;
using System.Text.Json;
using PKS.Infrastructure.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Otel;

[Description("List recent exceptions from Application Insights")]
public class OtelErrorsCommand : Command<OtelErrorsCommand.Settings>
{
    public class Settings : OtelQuerySettings
    {
        [CommandOption("--operation-id <ID>")]
        [Description("Filter by operation/correlation ID")]
        public string? OperationId { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAppInsightsConfigService _configService;
    private readonly IAppInsightsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public OtelErrorsCommand(
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

        var since = settings.ParsedSince;

        if (settings.Verbose)
        {
            var appId = await _queryService.GetConfiguredAppIdAsync();
            var kql = AppInsightsQueryService.BuildErrorsKql(since, settings.Limit, settings.AppName, settings.OperationId);
            _console.MarkupLine($"[dim]App ID  : {(appId ?? "unknown").EscapeMarkup()}[/]");
            _console.MarkupLine($"[dim]Window  : {since.TotalHours:0.#}h ({settings.Since})[/]");
            _console.MarkupLine($"[dim]KQL     :[/]");
            _console.MarkupLine($"[dim]{kql.EscapeMarkup()}[/]");
            _console.WriteLine();
        }

        var errors = await _queryService.QueryErrorsAsync(
            since, settings.Limit, settings.AppName, settings.OperationId);

        if (settings.Format.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(errors, JsonOpts));
            return 0;
        }

        if (errors.Count == 0)
        {
            _console.MarkupLine("[dim]No exceptions found in the specified time range.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
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
                e.Timestamp.ToString("HH:mm:ss"),
                e.ExceptionType.EscapeMarkup(),
                msg.EscapeMarkup(),
                opId.EscapeMarkup(),
                e.AppName.EscapeMarkup());
        }

        _console.Write(table);
        return 0;
    }
}
