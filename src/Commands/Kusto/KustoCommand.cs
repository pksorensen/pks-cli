using System.ComponentModel;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Kusto;

[Description("Run a KQL query against the configured Log Analytics workspace")]
public class KustoCommand : Command<KustoCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[query]")]
        [Description("KQL query. Omit to read the query from --file or stdin.")]
        public string? Query { get; set; }

        [CommandOption("--file <PATH>")]
        [Description("Read the KQL query from a file instead of the argument")]
        public string? File { get; set; }

        [CommandOption("--since <DURATION>")]
        [Description("Time window applied as the API timespan: 30m, 1h, 24h, 7d (default: whatever the query says)")]
        public string? Since { get; set; }

        [CommandOption("-w|--workspace <GUID>")]
        [Description("Query this workspace GUID instead of the configured one")]
        public string? Workspace { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("Output format: Table, Json or Csv (default: Table)")]
        [DefaultValue("Table")]
        public string Format { get; set; } = "Table";

        [CommandOption("-v|--verbose")]
        [Description("Show workspace, timespan and the KQL that was sent")]
        public bool Verbose { get; set; }

        public TimeSpan? ParsedSince
        {
            get
            {
                var s = Since?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s)) return null;
                if (s.EndsWith('d') && int.TryParse(s[..^1], out var days) && days > 0)
                    return TimeSpan.FromDays(days);
                if (s.EndsWith('h') && int.TryParse(s[..^1], out var hours) && hours > 0)
                    return TimeSpan.FromHours(hours);
                if (s.EndsWith('m') && int.TryParse(s[..^1], out var mins) && mins > 0)
                    return TimeSpan.FromMinutes(mins);
                return null;
            }
        }
    }

    private const int MaxCellWidth = 80;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ILogAnalyticsConfigService _configService;
    private readonly ILogAnalyticsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public KustoCommand(
        ILogAnalyticsConfigService configService,
        ILogAnalyticsQueryService queryService,
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
        if (string.IsNullOrWhiteSpace(settings.Workspace) && !await _configService.IsConfiguredAsync())
        {
            _console.MarkupLine("[yellow]Log Analytics is not configured.[/]");
            _console.MarkupLine("[dim]Run [cyan]pks loganalytics init[/] to configure, or pass [cyan]--workspace <GUID>[/].[/]");
            return 1;
        }

        var kql = ReadQuery(settings);
        if (string.IsNullOrWhiteSpace(kql))
        {
            _console.MarkupLine("[red]No query given.[/]");
            _console.MarkupLine("[dim]Pass it as an argument, with [cyan]--file[/], or on stdin.[/]");
            return 1;
        }

        if (settings.Verbose)
        {
            var workspaceId = settings.Workspace ?? await _queryService.GetConfiguredWorkspaceIdAsync();
            _console.MarkupLine($"[dim]Workspace: {(workspaceId ?? "?").EscapeMarkup()}[/]");
            _console.MarkupLine($"[dim]Timespan:  {(LogAnalyticsQueryService.FormatTimespan(settings.ParsedSince) ?? "(from query)").EscapeMarkup()}[/]");
            _console.MarkupLine($"[dim]KQL:       {kql.EscapeMarkup()}[/]");
            _console.WriteLine();
        }

        KustoQueryResponse response;
        try
        {
            response = await _queryService.QueryAsync(kql, settings.ParsedSince, settings.Workspace);
        }
        catch (Exception ex) when (ex is LogAnalyticsQueryException or InvalidOperationException)
        {
            _console.MarkupLine($"[red]{ex.Message.EscapeMarkup()}[/]");
            return 1;
        }

        return settings.Format.ToLowerInvariant() switch
        {
            "json" => WriteJson(response),
            "csv" => WriteCsv(response),
            _ => WriteTable(response)
        };
    }

    private string? ReadQuery(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            if (!System.IO.File.Exists(settings.File))
            {
                _console.MarkupLine($"[red]Query file not found:[/] {settings.File.EscapeMarkup()}");
                return null;
            }
            return System.IO.File.ReadAllText(settings.File);
        }

        if (!string.IsNullOrWhiteSpace(settings.Query))
            return settings.Query;

        return Console.IsInputRedirected ? Console.In.ReadToEnd() : null;
    }

    private int WriteTable(KustoQueryResponse response)
    {
        if (response.Tables.Count == 0 || response.Tables.All(t => t.Rows.Count == 0))
        {
            _console.MarkupLine("[dim]No rows returned.[/]");
            return 0;
        }

        foreach (var t in response.Tables)
        {
            if (response.Tables.Count > 1)
                _console.MarkupLine($"[bold]{t.Name.EscapeMarkup()}[/]");

            var table = new Table().Border(TableBorder.Rounded);
            foreach (var c in t.Columns)
                table.AddColumn(c.Name.EscapeMarkup());

            foreach (var row in t.Rows)
            {
                var cells = new string[t.Columns.Count];
                for (var i = 0; i < t.Columns.Count; i++)
                    cells[i] = Truncate(CellText(row, i)).EscapeMarkup();
                table.AddRow(cells);
            }

            _console.Write(table);
            _console.MarkupLine($"[dim]{t.Rows.Count} row(s)[/]");
        }

        return 0;
    }

    private static int WriteJson(KustoQueryResponse response)
    {
        var table = response.Tables.FirstOrDefault();
        if (table is null)
        {
            Console.WriteLine("[]");
            return 0;
        }

        var rows = table.Rows.Select(row =>
        {
            var obj = new Dictionary<string, JsonElement>();
            for (var i = 0; i < table.Columns.Count; i++)
                obj[table.Columns[i].Name] = i < row.Count ? row[i] : default;
            return obj;
        }).ToList();

        Console.WriteLine(JsonSerializer.Serialize(rows, JsonOpts));
        return 0;
    }

    private static int WriteCsv(KustoQueryResponse response)
    {
        var table = response.Tables.FirstOrDefault();
        if (table is null) return 0;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", table.Columns.Select(c => CsvEscape(c.Name))));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(",", Enumerable.Range(0, table.Columns.Count).Select(i => CsvEscape(CellText(row, i)))));

        Console.Write(sb.ToString());
        return 0;
    }

    internal static string CellText(List<JsonElement> row, int index)
    {
        if (index >= row.Count) return string.Empty;
        var el = row[index];
        return el.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => el.GetString() ?? string.Empty,
            _ => el.GetRawText()
        };
    }

    internal static string CsvEscape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static string Truncate(string value)
    {
        var oneLine = value.ReplaceLineEndings(" ");
        return oneLine.Length <= MaxCellWidth ? oneLine : oneLine[..(MaxCellWidth - 1)] + "…";
    }
}
