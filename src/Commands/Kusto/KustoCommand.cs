using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKS.Commands.Azure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Azure;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Kusto;

[Description("Run a KQL query against every enabled Log Analytics workspace")]
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

        [CommandOption("-w|--workspace <NAME_OR_GUID>")]
        [Description("Query only this workspace: a registered name, or a workspace GUID (queried directly, registered or not)")]
        public string? Workspace { get; set; }

        [CommandOption("--format <FORMAT>")]
        [Description("Output format: Table, Json or Csv (default: Table). Json and Csv rows carry a leading 'workspace' field.")]
        [DefaultValue("Table")]
        public string Format { get; set; } = "Table";

        [CommandOption("-v|--verbose")]
        [Description("Show the target workspaces, timespan and the KQL that was sent")]
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
    private const string WorkspaceColumn = "workspace";
    private const string InitCommand = "pks loganalytics init";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IAzureResourceRegistry _registry;
    private readonly ILogAnalyticsQueryService _queryService;
    private readonly IAnsiConsole _console;

    public KustoCommand(
        IAzureResourceRegistry registry,
        ILogAnalyticsQueryService queryService,
        IAnsiConsole console)
    {
        _registry = registry;
        _queryService = queryService;
        _console = console;
    }

    /// <summary>Where per-workspace failures go: stderr, so Json/Csv on stdout stay parseable. Tests inject a capture.</summary>
    internal IAnsiConsole? ErrorConsole { get; init; }

    public override int Execute(CommandContext context, Settings settings)
        => ExecuteAsync(settings).GetAwaiter().GetResult();

    private async Task<int> ExecuteAsync(Settings settings)
    {
        var targets = await ResolveTargetsAsync(settings.Workspace);
        if (targets is null)
            return 1;

        var kql = ReadQuery(settings);
        if (string.IsNullOrWhiteSpace(kql))
        {
            _console.MarkupLine("[red]No query given.[/]");
            _console.MarkupLine("[dim]Pass it as an argument, with [cyan]--file[/], or on stdin.[/]");
            return 1;
        }

        if (settings.Verbose)
        {
            _console.MarkupLine($"[dim]Workspaces ({targets.Count}):[/]");
            foreach (var t in targets)
                _console.MarkupLine($"[dim]  {t.Name.EscapeMarkup()}  {t.Key.EscapeMarkup()}{(t.TenantId is null ? "" : "  tenant " + t.TenantId.EscapeMarkup())}[/]");
            _console.MarkupLine($"[dim]Timespan:  {(LogAnalyticsQueryService.FormatTimespan(settings.ParsedSince) ?? "(from query)").EscapeMarkup()}[/]");
            _console.MarkupLine($"[dim]KQL:       {kql.EscapeMarkup()}[/]");
            _console.WriteLine();
        }

        var results = await _queryService.QueryManyAsync(targets, kql, settings.ParsedSince);

        var errorConsole = ErrorConsole ?? AzureQueryTargets.StderrConsole();
        foreach (var failed in results.Where(r => !r.Succeeded))
            AzureQueryTargets.ReportFailure(errorConsole, "Workspace", failed.Workspace, failed.Error ?? new InvalidOperationException("No response"), InitCommand);

        var succeeded = results.Where(r => r.Succeeded).ToList();
        switch (settings.Format.ToLowerInvariant())
        {
            case "json": WriteJson(succeeded); break;
            case "csv": WriteCsv(succeeded); break;
            default: WriteTable(succeeded, showHeadings: targets.Count > 1); break;
        }

        return succeeded.Count == 0 ? 1 : 0;
    }

    /// <summary>
    /// Which workspaces to query. No <c>--workspace</c> ⇒ every enabled one. A bare GUID ⇒ that
    /// workspace directly, registered or not (a registered entry, even a disabled one, lends its
    /// name and tenant). Anything else ⇒ an enabled entry by name or key, or an error.
    /// </summary>
    private async Task<IReadOnlyList<AzureResourceEntry>?> ResolveTargetsAsync(string? workspace)
    {
        var requested = workspace?.Trim();
        if (!string.IsNullOrEmpty(requested) && Guid.TryParse(requested, out _))
        {
            var registered = await _registry.FindAsync(AzureResourceKind.LogAnalytics, requested);
            return
            [
                registered ?? new AzureResourceEntry
                {
                    Kind = AzureResourceKind.LogAnalytics,
                    Key = requested,
                    Name = requested,
                    Enabled = true
                }
            ];
        }

        return await AzureQueryTargets.ResolveAsync(
            _registry, AzureResourceKind.LogAnalytics,
            string.IsNullOrEmpty(requested) ? null : [requested],
            _console, "Log Analytics workspaces", InitCommand);
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

    private void WriteTable(IReadOnlyList<WorkspaceQueryResult> results, bool showHeadings)
    {
        foreach (var result in results)
        {
            var response = result.Response!;
            if (showHeadings)
                _console.Write(new Rule($"[bold]Workspace {result.Workspace.Name.EscapeMarkup()}[/]").LeftJustified());

            if (response.Tables.Count == 0 || response.Tables.All(t => t.Rows.Count == 0))
            {
                _console.MarkupLine("[dim]No rows returned.[/]");
                continue;
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
        }
    }

    /// <summary>One flat array across every workspace; each row leads with the workspace name.</summary>
    private static void WriteJson(IReadOnlyList<WorkspaceQueryResult> results)
    {
        var rows = new JsonArray();
        foreach (var result in results)
        {
            var table = result.Response!.Tables.FirstOrDefault();
            if (table is null) continue;

            foreach (var row in table.Rows)
            {
                var obj = new JsonObject { [WorkspaceColumn] = result.Workspace.Name };
                for (var i = 0; i < table.Columns.Count; i++)
                    obj[table.Columns[i].Name] = i < row.Count ? JsonNode.Parse(row[i].GetRawText()) : null;
                rows.Add(obj);
            }
        }

        Console.WriteLine(rows.ToJsonString(JsonOpts));
    }

    /// <summary>One header (from the first workspace that answered) with a leading workspace column, then every row.</summary>
    private static void WriteCsv(IReadOnlyList<WorkspaceQueryResult> results)
    {
        var first = results.Select(r => r.Response!.Tables.FirstOrDefault()).FirstOrDefault(t => t is not null);
        if (first is null) return;

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { WorkspaceColumn }.Concat(first.Columns.Select(c => CsvEscape(c.Name)))));
        foreach (var result in results)
        {
            var table = result.Response!.Tables.FirstOrDefault();
            if (table is null) continue;
            foreach (var row in table.Rows)
                sb.AppendLine(string.Join(",",
                    new[] { CsvEscape(result.Workspace.Name) }
                        .Concat(Enumerable.Range(0, table.Columns.Count).Select(i => CsvEscape(CellText(row, i))))));
        }

        Console.Write(sb.ToString());
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
