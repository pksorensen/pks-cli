using System.ComponentModel;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Claude;

public class ClaudeUsageSettings : ClaudeSettings
{
    [CommandArgument(0, "[PROJECT]")]
    [Description("Filter to a specific project by folder name under ~/.claude/projects/. Omit to scan all projects.")]
    public string? ProjectName { get; set; }

    [CommandOption("-s|--session <SESSION>")]
    [Description("Filter to specific session id(s) — the .jsonl filename, matched as a prefix/substring across all projects. Repeat for multiple. Combines with PROJECT (intersection) when both are given.")]
    public string[] Sessions { get; set; } = [];

    [CommandOption("-d|--days")]
    [Description("Highlight most-recent N days in red (default: 7)")]
    [DefaultValue(7)]
    public int RecentDays { get; set; } = 7;
}

public class ClaudeUsageCommand : AsyncCommand<ClaudeUsageSettings>
{
    private const int CacheVersion = 1;

    // Offline fallback only — the live LiteLLM table (LoadLiteLLMPricingAsync) takes
    // precedence. (input, output, cache-write, cache-read) per token at Anthropic list
    // prices. NOTE: Opus 4.5+ is $5/$25 per Mtok — a 3x cut from Opus 4.1's $15/$75 — so
    // these Opus rows are intentionally NOT the old $15/$75.
    private static readonly Dictionary<string, ModelPricing> HardcodedPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["haiku-4-5"]  = new(1e-6, 5e-6,   1.25e-6, 1e-7),
        ["sonnet-4-6"] = new(3e-6, 1.5e-5, 3.75e-6, 3e-7),
        ["opus-4-6"]   = new(5e-6, 2.5e-5, 6.25e-6, 5e-7),
        ["opus-4-7"]   = new(5e-6, 2.5e-5, 6.25e-6, 5e-7),
        ["opus-4-8"]   = new(5e-6, 2.5e-5, 6.25e-6, 5e-7),
    };

    public override async Task<int> ExecuteAsync(CommandContext context, ClaudeUsageSettings settings)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeRoot = Path.Combine(home, ".claude", "projects");
        if (!Directory.Exists(claudeRoot))
            claudeRoot = Path.Combine(home, ".config", "claude", "projects");

        var jsonlFiles = GetJsonlFiles(claudeRoot, settings.ProjectName, settings.Sessions).ToList();

        if (jsonlFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No Claude Code session files found...[/]");
            return 0;
        }

        var liteLLM = await LoadLiteLLMPricingAsync(home);
        var pricingCache = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

        // Incremental parse: session files are append-only, so (size, mtime) is a
        // reliable cheap change-key. Unchanged files reuse cached token rows instead
        // of being re-read — turning a multi-GB scan into a few changed files.
        var cache = LoadCache(home);
        var fresh = new Dictionary<string, CachedFile>(cache.Files.Count);
        var allRows = new List<UsageRow>();
        bool dirty = false;
        int parsed = 0, reused = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Scanning {jsonlFiles.Count} session files…", async ctx =>
            {
                foreach (var file in jsonlFiles)
                {
                    long size, mtime;
                    try { var fi = new FileInfo(file); size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
                    catch { continue; }

                    if (cache.Files.TryGetValue(file, out var cf) && cf.Size == size && cf.MtimeTicks == mtime)
                    {
                        fresh[file] = cf;
                        allRows.AddRange(cf.Rows);
                        reused++;
                        continue;
                    }

                    try
                    {
                        var rows = await ParseUsageRowsAsync(file);
                        var nf = new CachedFile(size, mtime, rows);
                        fresh[file] = nf;
                        allRows.AddRange(rows);
                        dirty = true;
                        parsed++;
                        ctx.Status($"Parsing changed files… ({parsed} new, {reused} cached)");
                    }
                    catch { }
                }
            });

        // Persist cache if anything changed (new/modified files) or stale entries were pruned.
        if (dirty || fresh.Count != cache.Files.Count)
            SaveCache(home, new CacheManifest(CacheVersion, fresh));

        if (parsed > 0)
            AnsiConsole.MarkupLine($"[dim]Parsed {parsed} new/changed file(s); reused {reused} from cache.[/]");

        // Global dedup: every persisted content-block row of one API response shares the
        // server-assigned requestId+message.id, so count each billed request exactly once.
        var entries = BuildEntries(allRows, liteLLM, pricingCache);

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No cost data found in session files.[/]");
            return 0;
        }

        var lastDate = entries.Max(e => e.Date);
        var cutoff = lastDate.AddDays(-settings.RecentDays);

        var byDay = entries
            .GroupBy(e => e.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyCost(g.Key, g.Sum(e => e.Cost)))
            .ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold cyan]Claude Code — Cost Analysis[/]").RuleStyle("cyan dim"));
        AnsiConsole.WriteLine();

        RenderHourlyCostChart(entries);
        AnsiConsole.WriteLine();

        RenderCostChart(byDay, cutoff, settings.RecentDays);
        AnsiConsole.WriteLine();

        RenderCostSummary(entries, byDay);
        AnsiConsole.WriteLine();

        return 0;
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    internal static IEnumerable<string> GetJsonlFiles(string claudeRoot, string? projectName, string[]? sessions = null)
    {
        if (!Directory.Exists(claudeRoot))
            return [];

        IEnumerable<string> files;
        if (projectName is { Length: > 0 })
        {
            files = Directory.GetDirectories(claudeRoot)
                .Where(d => Path.GetFileName(d).Contains(projectName, StringComparison.OrdinalIgnoreCase))
                .SelectMany(d => Directory.GetFiles(d, "*.jsonl", SearchOption.TopDirectoryOnly));
        }
        else
        {
            files = Directory.GetFiles(claudeRoot, "*.jsonl", SearchOption.AllDirectories);
        }

        // Explicit session list: keep files whose name (the session id) matches any given id.
        // Session ids are UUIDs, so a substring/prefix match lets the user pass a short prefix.
        if (sessions is { Length: > 0 })
        {
            files = files.Where(f =>
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                return sessions.Any(s => s is { Length: > 0 }
                    && stem.Contains(s, StringComparison.OrdinalIgnoreCase));
            });
        }

        return files;
    }

    // ── Pricing ───────────────────────────────────────────────────────────────

    private static async Task<JsonElement?> LoadLiteLLMPricingAsync(string home)
    {
        var cacheFile = Path.Combine(home, ".claude", "pricing_cache.json");

        if (File.Exists(cacheFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile)).TotalHours < 24)
        {
            try
            {
                var cached = await File.ReadAllTextAsync(cacheFile);
                return JsonSerializer.Deserialize<JsonElement>(cached);
            }
            catch { }
        }

        try
        {
            AnsiConsole.MarkupLine("[dim]Fetching model pricing...[/]");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await http.GetStringAsync(
                "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");
            try { await File.WriteAllTextAsync(cacheFile, json); } catch { }
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch { }

        return null;
    }

    private static ModelPricing? GetModelPricing(
        string model, JsonElement? liteLLM, Dictionary<string, ModelPricing> cache)
    {
        if (cache.TryGetValue(model, out var cached)) return cached;

        ModelPricing? result = null;

        if (liteLLM is { } doc)
        {
            foreach (var prefix in new[] { "anthropic/", "" })
            {
                if (doc.TryGetProperty(prefix + model, out var entry))
                {
                    result = TryParseLiteLLMEntry(entry);
                    if (result != null) break;
                }
            }

            if (result == null)
            {
                foreach (var prop in doc.EnumerateObject())
                {
                    if (prop.Name.Contains(model, StringComparison.OrdinalIgnoreCase))
                    {
                        result = TryParseLiteLLMEntry(prop.Value);
                        if (result != null) break;
                    }
                }
            }
        }

        result ??= HardcodedPricing
            .Where(kv => model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .FirstOrDefault();

        if (result != null) cache[model] = result;
        return result;
    }

    private static ModelPricing? TryParseLiteLLMEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("input_cost_per_token", out var inp)) return null;
        if (!entry.TryGetProperty("output_cost_per_token", out var out_)) return null;
        if (!inp.TryGetDouble(out var inputCost)) return null;
        if (!out_.TryGetDouble(out var outputCost)) return null;

        double cacheCreate = 0, cacheRead = 0;
        if (entry.TryGetProperty("cache_creation_input_token_cost", out var cc) && cc.TryGetDouble(out var ccv))
            cacheCreate = ccv;
        if (entry.TryGetProperty("cache_read_input_token_cost", out var cr) && cr.TryGetDouble(out var crv))
            cacheRead = crv;

        return new ModelPricing(inputCost, outputCost, cacheCreate, cacheRead);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    // Parse one session file into billed-request rows. A single API response is written
    // to the transcript as many rows (one per content block: thinking / text / tool_use),
    // each repeating that response's requestId, message.id and *cumulative* usage. We fold
    // those rows here (keep the first per key) so the file contributes one row per real
    // request; cross-file folding happens later in BuildEntries.
    internal static async Task<List<UsageRow>> ParseUsageRowsAsync(string path)
    {
        var rows = new List<UsageRow>();
        var seen = new HashSet<string>();
        int lineNo = 0;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            if (root.TryGetProperty("isApiErrorMessage", out var errProp) && errProp.ValueKind == JsonValueKind.True)
                continue;

            if (!root.TryGetProperty("timestamp", out var tsProp)) continue;
            if (!DateTime.TryParse(tsProp.GetString(), out var ts)) continue;
            var tsUtc = ts.ToUniversalTime();

            double direct = 0;
            if (root.TryGetProperty("costUSD", out var costProp) &&
                costProp.ValueKind == JsonValueKind.Number &&
                costProp.TryGetDouble(out var d) && d > 0)
                direct = d;

            string model = "unknown";
            long inp = 0, outp = 0, cc = 0, cr = 0;
            string mid = "";
            if (root.TryGetProperty("message", out var msgEl))
            {
                if (msgEl.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                    model = mEl.GetString() ?? "unknown";
                if (msgEl.TryGetProperty("id", out var midEl) && midEl.ValueKind == JsonValueKind.String)
                    mid = midEl.GetString() ?? "";
                if (msgEl.TryGetProperty("usage", out var usageEl))
                {
                    inp  = GetLong(usageEl, "input_tokens");
                    outp = GetLong(usageEl, "output_tokens");
                    cc   = GetLong(usageEl, "cache_creation_input_tokens");
                    cr   = GetLong(usageEl, "cache_read_input_tokens");
                }
            }

            // Nothing billable on this row.
            if (direct <= 0 && inp <= 0 && outp <= 0) continue;

            // requestId is server-assigned, one per billed request; message.id pins the
            // assistant response. Rows lacking both can't be deduped — keep each (unique key).
            string rid = root.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String
                ? ridEl.GetString() ?? "" : "";
            string key = (rid.Length > 0 || mid.Length > 0)
                ? rid + "|" + mid
                : "noid|" + lineNo;

            if (!seen.Add(key)) continue;
            rows.Add(new UsageRow(tsUtc, model, inp, outp, cc, cr, direct, key));
        }

        return rows;
    }

    // Fold rows from all files into one cost entry per billed request (global dedup),
    // pricing each surviving row once.
    internal static List<CostEntry> BuildEntries(
        IEnumerable<UsageRow> rows, JsonElement? liteLLM, Dictionary<string, ModelPricing> pricingCache)
    {
        var seen = new HashSet<string>();
        var entries = new List<CostEntry>();

        foreach (var r in rows)
        {
            // "noid|" keys are file-local line numbers; keep them all (can't collide safely).
            if (!r.DedupKey.StartsWith("noid|", StringComparison.Ordinal) && !seen.Add(r.DedupKey))
                continue;

            double cost = r.Direct;
            if (cost <= 0)
            {
                var pricing = GetModelPricing(r.Model, liteLLM, pricingCache);
                if (pricing != null)
                    cost = r.Input  * pricing.InputPerToken
                         + r.Output * pricing.OutputPerToken
                         + r.CacheCreate * pricing.CacheCreatePerToken
                         + r.CacheRead   * pricing.CacheReadPerToken;
            }

            if (cost > 0)
                entries.Add(new CostEntry(r.Timestamp, cost, r.Model));
        }

        return entries;
    }

    private static long GetLong(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    // ── Result cache (~/.pks-cli/usage-cache/manifest.json) ─────────────────────

    private static string CacheManifestPath(string home) =>
        Path.Combine(home, ".pks-cli", "usage-cache", "manifest.json");

    private static CacheManifest LoadCache(string home)
    {
        try
        {
            var p = CacheManifestPath(home);
            if (File.Exists(p))
            {
                var m = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(p));
                if (m is { Version: CacheVersion, Files: not null })
                    return m;
            }
        }
        catch { }
        return new CacheManifest(CacheVersion, new Dictionary<string, CachedFile>());
    }

    private static void SaveCache(string home, CacheManifest manifest)
    {
        try
        {
            var p = CacheManifestPath(home);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(manifest));
        }
        catch { }
    }

    // ── Hourly chart ──────────────────────────────────────────────────────────

    private static void RenderHourlyCostChart(List<CostEntry> entries)
    {
        AnsiConsole.Write(new Rule("[bold]Cost per hour — last 24 hours  (USD)[/]").RuleStyle("dim"));

        var now = DateTime.UtcNow;
        var since = now.AddHours(-24);

        var hourlyData = Enumerable.Range(0, 24)
            .Select(h =>
            {
                var start = since.AddHours(h);
                var end = start.AddHours(1);
                var cost = entries
                    .Where(e => e.Timestamp >= start && e.Timestamp < end)
                    .Sum(e => e.Cost);
                return new HourlyCost(start, cost);
            })
            .ToList();

        double maxCost = hourlyData.Max(h => h.Cost);
        if (maxCost <= 0)
        {
            AnsiConsole.MarkupLine("[dim]  No activity in the last 24 hours.[/]");
            return;
        }

        int winW = TerminalWidth();
        int yAxisW = 8;
        int chartW = winW - yAxisW - 1;
        int chartH = 10;

        int colW = Math.Max(1, chartW / 24);
        int gapW = colW >= 3 ? 1 : 0;
        if (gapW > 0)
            colW = Math.Max(1, (chartW - 23) / 24);
        int totalBarW = 24 * colW + 23 * gapW;

        double[] niceIncrements = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0];
        double yIncrement = niceIncrements.FirstOrDefault(inc => maxCost / inc >= 3, 0.01);
        double yMax = Math.Ceiling(maxCost / yIncrement) * yIncrement;
        double range = Math.Max(yIncrement, yMax);
        double rowH = range / chartH;

        int yLabelEvery = Math.Max(1, chartH / 4);

        var currentHourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);

        var output = new System.Text.StringBuilder();

        for (int row = 0; row < chartH; row++)
        {
            double rowTop = yMax - row * rowH;
            double rowBot = rowTop - rowH;

            bool showLabel = row % yLabelEvery == 0 || row == chartH - 1;
            string costLabel = rowTop >= 1 ? $"${rowTop:F1}" : $"${rowTop:F3}";
            string yLbl = showLabel ? $"{costLabel,6} " : "       ";
            char axChar = row == chartH - 1 ? '┼' : '│';

            output.Append(Dim).Append(yLbl).Append(axChar).Append(R);

            for (int i = 0; i < 24; i++)
            {
                var h = hourlyData[i];
                bool isCurrent = h.HourStart == currentHourStart;
                string col = isCurrent ? Re : Cy;

                string blockChar;
                if (h.Cost >= rowTop)
                    blockChar = "█";
                else if (h.Cost > rowBot)
                {
                    double frac = (h.Cost - rowBot) / rowH;
                    int idx = (int)Math.Round(frac * 8);
                    blockChar = HourlyBlocks[Math.Clamp(idx, 1, 8)];
                }
                else
                    blockChar = " ";

                output.Append(col);
                for (int w = 0; w < colW; w++) output.Append(blockChar);
                output.Append(R);

                if (gapW > 0 && i < 23) output.Append(' ');
            }

            output.AppendLine();
        }

        output.Append(Dim)
              .Append(new string(' ', yAxisW))
              .Append('└')
              .Append(new string('─', totalBarW))
              .AppendLine(R);

        // X-axis hour labels — place every 4 hours
        int stride = colW + gapW;
        int lineLen = yAxisW + totalBarW + 2;
        var xLine = new char[lineLen];
        Array.Fill(xLine, ' ');

        for (int i = 0; i < 24; i += 4)
        {
            string lbl = hourlyData[i].HourStart.ToString("HH:mm");
            int center = yAxisW + i * stride + colW / 2;
            int xPos = Math.Clamp(center - lbl.Length / 2, 0, lineLen - lbl.Length);
            for (int c = 0; c < lbl.Length && xPos + c < lineLen; c++)
                xLine[xPos + c] = lbl[c];
        }
        // Always write last hour
        {
            string lbl = hourlyData[23].HourStart.ToString("HH:mm");
            int center = yAxisW + 23 * stride + colW / 2;
            int xPos = Math.Clamp(center - lbl.Length / 2, 0, lineLen - lbl.Length);
            // clear around it
            int from = Math.Max(0, xPos - 1);
            int to = Math.Min(lineLen, xPos + lbl.Length + 1);
            for (int c = from; c < to; c++) xLine[c] = ' ';
            for (int c = 0; c < lbl.Length && xPos + c < lineLen; c++)
                xLine[xPos + c] = lbl[c];
        }

        output.Append(Dim).Append(new string(xLine)).AppendLine(R);

        double total24h = hourlyData.Sum(h => h.Cost);
        output.Append(new string(' ', yAxisW + 2));
        output.AppendLine(
            $"{Cy}█{R} prior hours  {Re}█{R} current hour  24h total: ${total24h:F4}");

        Console.Write(output.ToString());
    }

    // ── Bar chart ─────────────────────────────────────────────────────────────

    private static readonly string[] HourlyBlocks = [" ", "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];
    private static readonly string[] Blocks = [" ", "▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    private const string R   = "\x1b[0m";
    private const string Dim = "\x1b[2m";
    private const string Cy  = "\x1b[36m";
    private const string Re  = "\x1b[91m";

    private static void RenderCostChart(List<DailyCost> byDay, DateTime cutoff, int recentDays)
    {
        AnsiConsole.Write(new Rule("[bold]Cost per day  (USD)[/]").RuleStyle("dim"));

        if (byDay.Count == 0) return;

        int winW = TerminalWidth();
        int winH = TerminalHeight();
        int yAxisW = 8;
        int chartW = winW - yAxisW - 1;
        int chartH = Math.Max(10, winH - 10 - 3);

        int numBars = Math.Min(byDay.Count, chartW);
        var data = byDay.TakeLast(numBars).ToList();

        int colW = Math.Max(1, chartW / Math.Max(1, data.Count));
        int gapW = colW >= 3 ? 1 : 0;
        if (gapW > 0)
            colW = Math.Max(1, (chartW - (data.Count - 1)) / Math.Max(1, data.Count));
        int totalBarW = data.Count * colW + (data.Count - 1) * gapW;

        double rawMax = data.Max(d => d.Cost);
        double[] niceIncrements = [0.5, 1.0, 2.0, 5.0, 10.0, 20.0, 50.0, 100.0];
        double yIncrement = niceIncrements.FirstOrDefault(inc => rawMax / inc >= 3, 0.5);
        double yMax = Math.Ceiling(rawMax / yIncrement) * yIncrement;
        double range = Math.Max(yIncrement, yMax);
        double rowH = range / chartH;

        int yLabelEvery = Math.Max(1, chartH / 6);

        var output = new System.Text.StringBuilder();

        for (int row = 0; row < chartH; row++)
        {
            double rowTop = yMax - row * rowH;
            double rowBot = rowTop - rowH;

            bool showLabel = row % yLabelEvery == 0 || row == chartH - 1;
            string costLabel = rowTop >= 10 ? $"${rowTop:F0}" : $"${rowTop:F2}";
            string yLbl = showLabel ? $"{costLabel,6} " : "       ";
            char axChar = row == chartH - 1 ? '┼' : '│';

            output.Append(Dim).Append(yLbl).Append(axChar).Append(R);

            for (int i = 0; i < data.Count; i++)
            {
                var day = data[i];
                bool recent = day.Date >= cutoff;
                string col = recent ? Re : Cy;

                string blockChar;
                if (day.Cost >= rowTop)
                    blockChar = "█";
                else if (day.Cost > rowBot)
                {
                    double frac = (day.Cost - rowBot) / rowH;
                    int idx = (int)Math.Round(frac * 8);
                    blockChar = Blocks[Math.Clamp(idx, 1, 8)];
                }
                else
                    blockChar = " ";

                output.Append(col);
                for (int w = 0; w < colW; w++) output.Append(blockChar);
                output.Append(R);

                if (gapW > 0 && i < data.Count - 1) output.Append(' ');
            }

            output.AppendLine();
        }

        output.Append(Dim)
              .Append(new string(' ', yAxisW))
              .Append('└')
              .Append(new string('─', totalBarW))
              .AppendLine(R);

        int stride = colW + gapW;
        const int LblW = 5;
        int labelEvery = Math.Max(1, (int)Math.Ceiling((double)(LblW + 1) / stride));
        int[] steps = [1, 2, 3, 5, 7, 10, 14, 21, 30];
        int step = steps.FirstOrDefault(s => s >= labelEvery, 30);

        int lineLen = yAxisW + totalBarW + 2;
        var xLine = new char[lineLen];
        Array.Fill(xLine, ' ');

        void PlaceLabel(int barIndex, string lbl, bool clearAround = false)
        {
            int center = yAxisW + barIndex * stride + colW / 2;
            int xPos = center - lbl.Length / 2;
            xPos = Math.Clamp(xPos, 0, lineLen - lbl.Length);
            if (clearAround)
            {
                int from = Math.Max(0, xPos - 1);
                int to = Math.Min(lineLen, xPos + lbl.Length + 1);
                for (int c = from; c < to; c++) xLine[c] = ' ';
            }
            for (int c = 0; c < lbl.Length && xPos + c < lineLen; c++)
                xLine[xPos + c] = lbl[c];
        }

        for (int i = 0; i < data.Count - 1; i += step)
            PlaceLabel(i, data[i].Date.ToString("MM-dd"));
        if (data.Count > 0)
            PlaceLabel(data.Count - 1, data[^1].Date.ToString("MM-dd"), clearAround: true);

        output.Append(Dim).Append(new string(xLine)).AppendLine(R);

        double total = byDay.Sum(d => d.Cost);
        int recentDayCount = data.Count(d => d.Date >= cutoff);
        output.Append(new string(' ', yAxisW + 2));
        output.AppendLine(
            $"{Cy}█{R} prior  {Re}█{R} last {recentDayCount} days  total: ${total:F2}");

        Console.Write(output.ToString());
    }

    // ── Summary table ─────────────────────────────────────────────────────────

    private static void RenderCostSummary(List<CostEntry> entries, List<DailyCost> byDay)
    {
        double total = entries.Sum(e => e.Cost);
        int activeDays = byDay.Count;
        double avgPerDay = activeDays > 0 ? total / activeDays : 0;
        var busiest = byDay.MaxBy(d => d.Cost)!;

        AnsiConsole.Write(new Rule("[bold]Summary[/]").RuleStyle("dim"));
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Total Cost[/]")
            .AddColumn("[dim]Avg / Active Day[/]")
            .AddColumn("[dim]Busiest Day[/]");

        summaryTable.AddRow(
            $"[bold cyan]${total:F2}[/]",
            $"[bold]${avgPerDay:F2}[/]",
            $"[bold]{busiest.Date:yyyy-MM-dd}[/] [dim](${busiest.Cost:F2})[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        var byModel = entries
            .GroupBy(e => e.Model)
            .Select(g => (Model: g.Key, Cost: g.Sum(e => e.Cost)))
            .OrderByDescending(x => x.Cost)
            .Take(5)
            .ToList();

        if (byModel.Count == 0) return;

        var modelTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[dim]Model[/]")
            .AddColumn(new TableColumn("[dim]Total Cost[/]").RightAligned())
            .AddColumn(new TableColumn("[dim]% of Total[/]").RightAligned());

        foreach (var (model, cost) in byModel)
        {
            double pct = total > 0 ? cost / total * 100 : 0;
            modelTable.AddRow(Markup.Escape(model), $"${cost:F2}", $"{pct:F1}%");
        }

        AnsiConsole.Write(modelTable);
    }

    // ── Terminal sizing ───────────────────────────────────────────────────────

    private static int TerminalWidth()
    {
        if (!Console.IsOutputRedirected && Console.WindowWidth > 10)
            return Console.WindowWidth;
        if (int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out int cols) && cols > 10)
            return cols;
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("stty", "size")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var raw = p?.StandardOutput.ReadLine()?.Trim().Split(' ');
            p?.WaitForExit();
            if (raw?.Length == 2 && int.TryParse(raw[1], out int c) && c > 10) return c;
        }
        catch { }
        return 120;
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
        return 40;
    }

    // ── Records ───────────────────────────────────────────────────────────────

    internal record ModelPricing(
        double InputPerToken,
        double OutputPerToken,
        double CacheCreatePerToken,
        double CacheReadPerToken);

    internal record CostEntry(DateTime Timestamp, double Cost, string Model)
    {
        public DateTime Date => Timestamp.Date;
    }

    // One billed request, after per-file folding. Token counts are stored (not cost) so the
    // cache survives pricing changes — cost is recomputed each run, which is cheap.
    internal record UsageRow(
        [property: JsonPropertyName("t")] DateTime Timestamp,
        [property: JsonPropertyName("m")] string Model,
        [property: JsonPropertyName("i")] long Input,
        [property: JsonPropertyName("o")] long Output,
        [property: JsonPropertyName("c")] long CacheCreate,
        [property: JsonPropertyName("r")] long CacheRead,
        [property: JsonPropertyName("d")] double Direct,
        [property: JsonPropertyName("k")] string DedupKey);

    private record CachedFile(
        [property: JsonPropertyName("s")]  long Size,
        [property: JsonPropertyName("mt")] long MtimeTicks,
        [property: JsonPropertyName("rows")] List<UsageRow> Rows);

    private record CacheManifest(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("files")] Dictionary<string, CachedFile> Files);

    private record DailyCost(DateTime Date, double Cost);

    private record HourlyCost(DateTime HourStart, double Cost);
}
