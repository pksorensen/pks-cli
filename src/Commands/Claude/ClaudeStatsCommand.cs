using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

public class ClaudeStatsSettings : ClaudeSettings
{
    [CommandOption("-d|--days")]
    [Description("Number of recent days to highlight as 'recent' (default: 7)")]
    [DefaultValue(7)]
    public int RecentDays { get; set; } = 7;

    [CommandOption("-p|--project")]
    [Description("Path to the project directory to analyse (default: current directory)")]
    public string? ProjectPath { get; set; }

    [CommandOption("--all-projects")]
    [Description("Analyse all projects in ~/.claude/projects/ instead of just the current one")]
    public bool AllProjects { get; set; }
}

/// <summary>
/// Analyses Claude Code session JSONL files and prints a response-time
/// performance report directly in the terminal using Spectre.Console.
///
/// Usage:  pks claude stats
///         pks claude stats --days 14
///         pks claude stats --project /path/to/other-project
///         pks claude stats --all-projects
/// </summary>
public class ClaudeStatsCommand : AsyncCommand<ClaudeStatsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ClaudeStatsSettings settings)
    {
        var projectPath = settings.ProjectPath ?? Directory.GetCurrentDirectory();
        var claudeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "projects");

        IEnumerable<string> sessionDirs;
        string scope;

        if (settings.AllProjects)
        {
            sessionDirs = Directory.Exists(claudeRoot)
                ? [claudeRoot]
                : [];
            scope = "all projects";
        }
        else
        {
            // Convert the project path to the encoded folder name Claude uses:
            // e.g.  /workspaces/my-repo  →  -workspaces-my-repo
            var encoded = projectPath.Replace(Path.DirectorySeparatorChar, '-').Replace('/', '-');
            // Normalise leading separators
            if (!encoded.StartsWith('-')) encoded = "-" + encoded;
            var dir = Path.Combine(claudeRoot, encoded);
            sessionDirs = Directory.Exists(dir) ? [dir] : [];
            scope = projectPath;
        }

        var jsonlFiles = sessionDirs
            .SelectMany(d => Directory.GetFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly))
            .ToList();

        if (jsonlFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No Claude session files found for:[/] [dim]{scope}[/]");
            AnsiConsole.MarkupLine("[dim]Run Claude Code in this project first, then re-run this command.[/]");
            return 1;
        }

        // ── Parse ────────────────────────────────────────────────────────────
        List<RequestDataPoint> points = [];

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Parsing {jsonlFiles.Count} session files…", async _ =>
            {
                foreach (var file in jsonlFiles)
                {
                    try { points.AddRange(await ParseSessionAsync(file)); }
                    catch { /* skip corrupt files */ }
                }
            });

        if (points.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No request/response pairs found.[/]");
            return 1;
        }

        points.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // ── Aggregate ────────────────────────────────────────────────────────
        var cutoff = points[^1].Timestamp.AddDays(-settings.RecentDays);
        var recentPts = points.Where(p => p.Timestamp >= cutoff).ToList();
        var priorPts  = points.Where(p => p.Timestamp <  cutoff).ToList();

        double recentMedian = Median(recentPts.Select(p => p.MsPerOutputToken));
        double priorMedian  = Median(priorPts.Select(p => p.MsPerOutputToken));
        double pctChange    = priorPts.Count > 0
            ? (recentMedian - priorMedian) / priorMedian * 100
            : 0;

        var byDay = points
            .GroupBy(p => p.Timestamp.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyStat(
                g.Key,
                Median(g.Select(p => p.MsPerOutputToken)),
                g.Count()))
            .ToList();

        // ── Render ───────────────────────────────────────────────────────────
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]Claude Code — Response Time Analysis[/]").RuleStyle("cyan dim"));
        AnsiConsole.WriteLine();

        RenderSummaryCards(points, recentMedian, priorMedian, pctChange, settings.RecentDays, scope);
        AnsiConsole.WriteLine();

        RenderTimeSeriesChart(byDay, cutoff);
        AnsiConsole.WriteLine();

        RenderModelBreakdown(points);
        AnsiConsole.WriteLine();

        RenderPercentileTable(points, byDay);
        AnsiConsole.WriteLine();

        return 0;
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private static async Task<List<RequestDataPoint>> ParseSessionAsync(string path)
    {
        var userMsgs    = new Dictionary<string, DateTime>();    // uuid → timestamp
        var requestMap  = new Dictionary<string, RequestEntry>(); // requestId → last entry

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (!root.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            if (type == "user" &&
                root.TryGetProperty("uuid", out var uuidProp) &&
                root.TryGetProperty("timestamp", out var tsProp) &&
                DateTime.TryParse(tsProp.GetString(), out var userTs))
            {
                userMsgs[uuidProp.GetString()!] = userTs;
            }
            else if (type == "assistant" &&
                     root.TryGetProperty("requestId", out var reqIdProp) &&
                     root.TryGetProperty("timestamp", out var tsProp2) &&
                     root.TryGetProperty("parentUuid", out var parentProp) &&
                     DateTime.TryParse(tsProp2.GetString(), out var asstTs))
            {
                var reqId = reqIdProp.GetString()!;
                var usage = ExtractUsage(root);
                if (!requestMap.TryGetValue(reqId, out var existing))
                {
                    requestMap[reqId] = new RequestEntry(
                        parentProp.GetString()!,
                        asstTs,
                        usage,
                        ExtractModel(root));
                }
                else
                {
                    if (asstTs > existing.LastTs) existing = existing with { LastTs = asstTs };
                    if (usage is not null) existing = existing with { Usage = usage };
                    requestMap[reqId] = existing;
                }
            }
        }

        var result = new List<RequestDataPoint>();
        foreach (var (_, req) in requestMap)
        {
            if (!userMsgs.TryGetValue(req.ParentUuid, out var userTs)) continue;
            if (req.Usage is not { } usage) continue;

            var durationMs = (req.LastTs - userTs).TotalMilliseconds;
            if (durationMs <= 0 || durationMs > 10 * 60 * 1000) continue;

            var outputTokens = usage.OutputTokens;
            if (outputTokens < 5) continue;

            var inputTokens = usage.InputTokens
                            + usage.CacheRead
                            + usage.CacheCreate;

            result.Add(new RequestDataPoint(
                userTs,
                (long)durationMs,
                inputTokens,
                outputTokens,
                durationMs / outputTokens,
                req.Model ?? "unknown"));
        }
        return result;
    }

    private static UsageData? ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("usage", out var usage)) return null;
        return new UsageData(
            GetInt(usage, "input_tokens"),
            GetInt(usage, "output_tokens"),
            GetInt(usage, "cache_read_input_tokens"),
            GetInt(usage, "cache_creation_input_tokens"));
    }

    private static string? ExtractModel(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        return msg.TryGetProperty("model", out var m) ? m.GetString() : null;
    }

    private static int GetInt(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) ? v.GetInt32() : 0;

    // ── Renderers ─────────────────────────────────────────────────────────────

    private static void RenderSummaryCards(
        List<RequestDataPoint> points,
        double recentMedian, double priorMedian, double pctChange,
        int recentDays, string scope)
    {
        var isSlower = pctChange > 0;
        var changeColor = isSlower ? "red" : "green";
        var changeArrow = isSlower ? "▲" : "▼";
        var changeLabel = isSlower ? "slower" : "faster";
        var sign = isSlower ? "+" : "";

        var dateFrom = points[0].Timestamp.ToString("yyyy-MM-dd");
        var dateTo   = points[^1].Timestamp.ToString("yyyy-MM-dd");

        AnsiConsole.MarkupLine($"[dim]Scope:[/] {Markup.Escape(scope)}");
        AnsiConsole.MarkupLine($"[dim]Range:[/] {dateFrom} → {dateTo}   [dim]|[/]   {points.Count} requests");
        AnsiConsole.WriteLine();

        var grid = new Grid();
        grid.AddColumn(); grid.AddColumn(); grid.AddColumn(); grid.AddColumn();

        grid.AddRow(
            CardPanel($"Last {recentDays} days",  $"[bold cyan]{recentMedian:F0} ms/tok[/]"),
            CardPanel("Prior period",              $"[bold dim]{priorMedian:F0} ms/tok[/]"),
            CardPanel("Change",                    $"[bold {changeColor}]{sign}{pctChange:F1}% {changeArrow} {changeLabel}[/]"),
            CardPanel("Total requests",            $"[bold white]{points.Count:N0}[/]")
        );

        AnsiConsole.Write(grid);
    }

    private static Panel CardPanel(string title, string value) =>
        new Panel(new Markup(value))
            .Header($" [dim]{title}[/] ")
            .Border(BoxBorder.Rounded)
            .BorderStyle(new Style(Color.Grey))
            .Padding(1, 0);

    // ── Canvas chart (time on X axis, auto-sized to terminal) ────────────────

    private static readonly string[] Blocks = [" ", "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    // ANSI codes — bypasses Spectre markup for per-character coloring
    private const string R   = "\x1b[0m";       // reset
    private const string Dim = "\x1b[2m";
    private const string Cy  = "\x1b[36m";      // cyan  – prior
    private const string Re  = "\x1b[91m";      // red   – recent
    private const string Ye  = "\x1b[33m";      // yellow – median line

    private static void RenderTimeSeriesChart(List<DailyStat> byDay, DateTime cutoff)
    {
        AnsiConsole.Write(new Rule("[bold]Response time over time  (ms / output token)[/]").RuleStyle("dim"));

        if (byDay.Count == 0) return;

        // ── Sizing: fill the terminal ─────────────────────────────────────────
        // Console.WindowWidth/Height can return 0 or 1 when output is redirected
        // or when the terminal doesn't propagate dimensions (some VS Code configs).
        // Fall back to COLUMNS/LINES env vars, then to generous defaults.
        int winW = TerminalWidth();
        int winH = TerminalHeight();
        int yAxisW = 6;   // "  42 │"
        int chartW = winW - yAxisW - 1;

        // chart rows = terminal height minus lines already printed above
        // (title rule=1, blank=1, scope=1, range=1, blank=1, cards=3, blank=1, chart rule=1 = 10)
        // plus chart overhead: x-axis=1, labels=1, legend=1 = 3
        int chartH = Math.Max(10, winH - 10 - 3);

        // ── Bar width: expand to fill full chart width ────────────────────────
        int numBars = Math.Min(byDay.Count, chartW);
        var data    = byDay.TakeLast(numBars).ToList();

        // Each bar gets an equal slice of chartW; gap only when bars are wide enough
        int colW  = Math.Max(1, chartW / Math.Max(1, data.Count));
        int gapW  = colW >= 3 ? 1 : 0;
        // Recalc colW accounting for gaps
        if (gapW > 0)
            colW = Math.Max(1, (chartW - (data.Count - 1)) / Math.Max(1, data.Count));
        int totalBarW = data.Count * colW + (data.Count - 1) * gapW;

        // ── Scale ─────────────────────────────────────────────────────────────
        double rawMax = data.Max(d => d.Median);
        double rawMin = data.Min(d => d.Median);
        double yMax   = Math.Ceiling(rawMax * 1.15 / 5) * 5;
        double yMin   = Math.Max(0, Math.Floor(rawMin * 0.80 / 5) * 5);
        double range  = Math.Max(1, yMax - yMin);
        double rowH   = range / chartH;

        double globalMedian = Median(data.Select(d => d.Median));

        // Y-axis label interval: aim for ~6 labels across the height
        int yLabelEvery = Math.Max(1, chartH / 6);

        // ── Render rows top→bottom ────────────────────────────────────────────
        var output = new System.Text.StringBuilder();

        for (int row = 0; row < chartH; row++)
        {
            double rowTop = yMax - row * rowH;
            double rowBot = rowTop - rowH;
            bool isMedianRow = globalMedian >= rowBot && globalMedian < rowTop;

            bool showLabel = row % yLabelEvery == 0 || row == chartH - 1;
            string yLbl = showLabel ? $"{rowTop,4:F0} " : "     ";
            char axChar = row == chartH - 1 ? '┼' : '│';

            output.Append(Dim).Append(yLbl).Append(axChar).Append(R);

            for (int i = 0; i < data.Count; i++)
            {
                var day = data[i];
                bool recent = day.Date >= cutoff;
                string col = recent ? Re : Cy;

                string blockChar;
                if (day.Median >= rowTop)
                    blockChar = "█";
                else if (day.Median > rowBot)
                {
                    double frac = (day.Median - rowBot) / rowH;
                    int idx = (int)Math.Round(frac * 8);
                    blockChar = Blocks[Math.Clamp(idx, 1, 8)];
                }
                else
                    blockChar = isMedianRow ? Ye + "·" + col : " ";

                // Fill the full bar width with the same block char
                output.Append(col);
                for (int w = 0; w < colW; w++) output.Append(blockChar);
                output.Append(R);

                if (gapW > 0 && i < data.Count - 1) output.Append(' ');
            }

            output.AppendLine();
        }

        // ── X-axis line ───────────────────────────────────────────────────────
        output.Append(Dim)
              .Append(new string(' ', yAxisW))
              .Append('└')
              .Append(new string('─', totalBarW))
              .AppendLine(R);

        // ── Date labels ───────────────────────────────────────────────────────
        int stride = colW + gapW;           // chars per bar slot
        const int LblW = 5;                 // "MM-dd"
        int labelEvery = Math.Max(1, (int)Math.Ceiling((double)(LblW + 1) / stride));
        int[] steps = [1, 2, 3, 5, 7, 10, 14, 21, 30];
        int step = steps.FirstOrDefault(s => s >= labelEvery, 30);

        int lineLen = yAxisW + totalBarW + 2;
        var xLine = new char[lineLen];
        Array.Fill(xLine, ' ');

        void PlaceLabel(int barIndex, string lbl, bool clearAround = false)
        {
            int center = yAxisW + barIndex * stride + colW / 2;
            int xPos   = center - lbl.Length / 2;
            xPos = Math.Clamp(xPos, 0, lineLen - lbl.Length);
            if (clearAround)
            {
                int from = Math.Max(0, xPos - 1);
                int to   = Math.Min(lineLen, xPos + lbl.Length + 1);
                for (int c = from; c < to; c++) xLine[c] = ' ';
            }
            for (int c = 0; c < lbl.Length && xPos + c < lineLen; c++)
                xLine[xPos + c] = lbl[c];
        }

        for (int i = 0; i < data.Count - 1; i += step)
            PlaceLabel(i, data[i].Date.ToString("MM-dd"));

        // Always write last date (clear around it to avoid overlap)
        if (data.Count > 0)
            PlaceLabel(data.Count - 1, data[^1].Date.ToString("MM-dd"), clearAround: true);

        output.Append(Dim).Append(new string(xLine)).AppendLine(R);

        // ── Legend ────────────────────────────────────────────────────────────
        int recentDayCount = data.Count(d => d.Date >= cutoff);
        output.Append(new string(' ', yAxisW + 2));
        output.AppendLine(
            $"{Cy}█{R} prior period   {Re}█{R} last {recentDayCount} days   " +
            $"{Ye}·{R} overall median ({globalMedian:F0} ms/tok)");

        Console.Write(output.ToString());
    }

    private static void RenderModelBreakdown(List<RequestDataPoint> points)
    {
        var byModel = points
            .GroupBy(p => p.Model)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (byModel.Count <= 1) return;

        AnsiConsole.Write(new Rule("[bold]By model[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Model[/]")
            .AddColumn(new TableColumn("[dim]Requests[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]Median ms/tok[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]p75 ms/tok[/]").RightAligned());

        foreach (var g in byModel)
        {
            var vals = g.Select(p => p.MsPerOutputToken).OrderBy(x => x).ToList();
            var p75  = vals[(int)(vals.Count * 0.75)];
            table.AddRow(
                Markup.Escape(g.Key),
                g.Count().ToString("N0"),
                $"{Median(g.Select(p => p.MsPerOutputToken)):F0}",
                $"{p75:F0}");
        }

        AnsiConsole.Write(table);
    }

    private static void RenderPercentileTable(List<RequestDataPoint> points, List<DailyStat> byDay)
    {
        AnsiConsole.Write(new Rule("[bold]Percentile breakdown — all time[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();

        var vals = points.Select(p => p.MsPerOutputToken).OrderBy(x => x).ToList();
        int n = vals.Count;

        double P(double pct) => vals[(int)(n * pct / 100)];

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Percentile[/]")
            .AddColumn(new TableColumn("[dim]ms / output token[/]").RightAligned());

        foreach (var (label, pct) in new[] { ("p10", 10.0), ("p25", 25.0), ("p50 (median)", 50.0), ("p75", 75.0), ("p90", 90.0), ("p99", 99.0) })
            table.AddRow($"[dim]{label}[/]", $"{P(pct):F0}");

        AnsiConsole.Write(table);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int TerminalWidth()
    {
        // Console.WindowWidth is reliable only on a live TTY
        if (!Console.IsOutputRedirected && Console.WindowWidth > 10)
            return Console.WindowWidth;
        // COLUMNS is set by many shells when the terminal resizes
        if (int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out int cols) && cols > 10)
            return cols;
        // stty size gives "rows cols" — try it as last resort
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stty", "size")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var raw = p?.StandardOutput.ReadLine()?.Trim().Split(' ');
            p?.WaitForExit();
            if (raw?.Length == 2 && int.TryParse(raw[1], out int c) && c > 10) return c;
        }
        catch { }
        return 120; // sensible default
    }

    private static int TerminalHeight()
    {
        if (!Console.IsOutputRedirected && Console.WindowHeight > 10)
            return Console.WindowHeight;
        if (int.TryParse(Environment.GetEnvironmentVariable("LINES"), out int lines) && lines > 10)
            return lines;
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stty", "size")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var raw = p?.StandardOutput.ReadLine()?.Trim().Split(' ');
            p?.WaitForExit();
            if (raw?.Length == 2 && int.TryParse(raw[0], out int r) && r > 10) return r;
        }
        catch { }
        return 40; // sensible default
    }

    private static double Median(IEnumerable<double> source)
    {
        var sorted = source.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return 0;
        return sorted[sorted.Count / 2];
    }

    // ── Records ───────────────────────────────────────────────────────────────

    private record RequestDataPoint(
        DateTime Timestamp,
        long DurationMs,
        int InputTokens,
        int OutputTokens,
        double MsPerOutputToken,
        string Model);

    private record UsageData(int InputTokens, int OutputTokens, int CacheRead, int CacheCreate);

    private record RequestEntry(string ParentUuid, DateTime LastTs, UsageData? Usage, string? Model);

    private record DailyStat(DateTime Date, double Median, int Count);
}
