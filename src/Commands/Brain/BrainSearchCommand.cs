using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;
using PKS.Infrastructure.Services.Brain;

namespace PKS.Commands.Brain;

public class BrainSearchSettings : BrainSettings
{
    [CommandArgument(0, "<QUERY>")]
    [Description("Substring or regex to search for.")]
    public string Query { get; set; } = string.Empty;

    [CommandOption("--in")]
    [Description("Sources to search: prompts, tools, files, errors, extracts, or all (default).")]
    [DefaultValue("all")]
    public string In { get; set; } = "all";

    [CommandOption("-n|--limit")]
    [Description("Max results (default 20).")]
    [DefaultValue(20)]
    public int Limit { get; set; } = 20;

    [CommandOption("--since")]
    [Description("Only search rows newer than this (e.g. 7d, 24h, ISO date).")]
    public string? Since { get; set; }

    [CommandOption("--project")]
    [Description("Restrict to a specific project slug (default: all).")]
    public string? Project { get; set; }

    [CommandOption("--regex")]
    [Description("Treat the query as a regex (default: case-insensitive substring).")]
    public bool Regex { get; set; }

    [CommandOption("--case-sensitive")]
    [Description("Match case (substring mode only).")]
    public bool CaseSensitive { get; set; }
}

public class BrainSearchCommand : AsyncCommand<BrainSearchSettings>
{
    private readonly IBrainPathResolver _paths;

    public BrainSearchCommand(IBrainPathResolver paths)
    {
        _paths = paths;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainSearchSettings settings)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold magenta]pks brain search[/] [grey]\"{settings.Query}\"[/]").RuleStyle("magenta dim"));
        AnsiConsole.WriteLine();

        if (string.IsNullOrWhiteSpace(settings.Query))
        {
            AnsiConsole.MarkupLine("[red]Empty query.[/]");
            return 1;
        }

        Func<string, bool> match;
        if (settings.Regex)
        {
            Regex rx;
            try { rx = new Regex(settings.Query, settings.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch (ArgumentException ex)
            {
                AnsiConsole.MarkupLine($"[red]Invalid regex:[/] {ex.Message}");
                return 1;
            }
            match = line => rx.IsMatch(line);
        }
        else
        {
            var cmp = settings.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            match = line => line.Contains(settings.Query, cmp);
        }

        DateTime? sinceUtc = null;
        if (settings.Since is { Length: > 0 } s && !TryParseSince(s, out sinceUtc))
        {
            AnsiConsole.MarkupLine($"[red]Could not parse --since:[/] {s}");
            return 1;
        }

        var sources = ParseSources(settings.In);
        var results = new List<MatchRow>();
        var ct = CancellationToken.None;

        foreach (var src in sources)
        {
            if (results.Count >= settings.Limit) break;
            switch (src)
            {
                case "prompts":  await ScanFirehoseAsync(BrainFirehose.Prompts, settings, match, sinceUtc, results, ct); break;
                case "tools":    await ScanFirehoseAsync(BrainFirehose.Tools,   settings, match, sinceUtc, results, ct); break;
                case "files":    await ScanFirehoseAsync(BrainFirehose.Files,   settings, match, sinceUtc, results, ct); break;
                case "errors":   await ScanFirehoseAsync(BrainFirehose.Errors,  settings, match, sinceUtc, results, ct); break;
                case "extracts": await ScanExtractsAsync(settings, match, results, ct); break;
            }
        }

        if (results.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No matches.[/] Searched: {string.Join(", ", sources)}.");
            return 0;
        }

        var table = new Table().Border(TableBorder.MinimalHeavyHead);
        table.AddColumn("[grey]source[/]");
        table.AddColumn("[grey]when[/]");
        table.AddColumn("[grey]session[/]");
        table.AddColumn("snippet");
        foreach (var r in results.Take(settings.Limit))
        {
            table.AddRow(
                $"[cyan]{r.Source}[/]",
                r.TimestampUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-",
                $"[grey]{Short(r.SessionId)}[/]",
                Markup.Escape(Truncate(r.Snippet, 90))
            );
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]{results.Count} match(es) shown[/]" + (results.Count >= settings.Limit ? " [yellow](limit reached — increase with --limit)[/]" : ""));
        return 0;
    }

    // ── source iteration ──────────────────────────────────────────────────────

    private async Task ScanFirehoseAsync(BrainFirehose firehose, BrainSearchSettings settings,
        Func<string, bool> match, DateTime? sinceUtc, List<MatchRow> results, CancellationToken ct)
    {
        var path = _paths.GlobalFirehose(firehose);
        if (!File.Exists(path)) return;
        var sourceLabel = firehose.ToString().ToLowerInvariant();

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (results.Count >= settings.Limit) return;
            if (line.Length == 0) continue;
            if (!match(line)) continue;
            // Cheap pre-filter via Contains on project slug if provided.
            if (settings.Project is { Length: > 0 } proj &&
                !line.Contains("\"projectSlug\":\"" + proj, StringComparison.Ordinal))
                continue;

            JsonElement root;
            try { root = JsonSerializer.Deserialize<JsonElement>(line); }
            catch (JsonException) { continue; }

            var sessionId = root.TryGetProperty("sessionId", out var sid) && sid.ValueKind == JsonValueKind.String
                ? sid.GetString() ?? "" : "";
            var ts = TryDate(root, "timestampUtc");
            if (sinceUtc is { } since && ts is { } t && t < since) continue;

            var snippet = ExtractSnippet(firehose, root, settings.Query);
            results.Add(new MatchRow(sourceLabel, ts, sessionId, snippet));
        }
    }

    private async Task ScanExtractsAsync(BrainSearchSettings settings,
        Func<string, bool> match, List<MatchRow> results, CancellationToken ct)
    {
        // Per-project extracts dir — scope to the current cwd's git repo.
        var projectRoot = _paths.ResolveProjectRoot(Directory.GetCurrentDirectory());
        if (projectRoot is null) return;
        var extractsDir = Path.Combine(projectRoot, "extracts");
        if (!Directory.Exists(extractsDir)) return;

        foreach (var path in Directory.EnumerateFiles(extractsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            if (results.Count >= settings.Limit) return;
            string content;
            try { content = await File.ReadAllTextAsync(path, ct); } catch { continue; }
            // Cheap pass: skip files with no match before iterating lines.
            if (!match(content)) continue;
            var sessionId = Path.GetFileNameWithoutExtension(path);
            DateTime? mtime = File.GetLastWriteTimeUtc(path);
            foreach (var raw in content.Split('\n'))
            {
                if (results.Count >= settings.Limit) return;
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (!match(line)) continue;
                results.Add(new MatchRow("extracts", mtime, sessionId, line.TrimStart()));
            }
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string ExtractSnippet(BrainFirehose firehose, JsonElement root, string query)
    {
        // Pick the most useful field per firehose so the snippet shows the meaningful content.
        switch (firehose)
        {
            case BrainFirehose.Prompts:
                if (root.TryGetProperty("text", out var pt) && pt.ValueKind == JsonValueKind.String)
                    return CenteredSnippet(pt.GetString() ?? "", query);
                break;
            case BrainFirehose.Tools:
                var name = root.TryGetProperty("toolName", out var n) ? n.GetString() ?? "" : "";
                var prev = root.TryGetProperty("inputPreview", out var p) ? p.GetString() ?? "" : "";
                return $"{name}: {CenteredSnippet(prev, query)}";
            case BrainFirehose.Files:
                var op = root.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
                var fp = root.TryGetProperty("filePath", out var f) ? f.GetString() ?? "" : "";
                return $"{op} {fp}";
            case BrainFirehose.Errors:
                var tn = root.TryGetProperty("toolName", out var en) ? en.GetString() ?? "" : "";
                var sn = root.TryGetProperty("snippet", out var es) ? es.GetString() ?? "" : "";
                return $"{tn}: {CenteredSnippet(sn, query)}";
        }
        return root.GetRawText();
    }

    private static string CenteredSnippet(string body, string query)
    {
        if (string.IsNullOrEmpty(body)) return "";
        var idx = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return body;
        var start = Math.Max(0, idx - 40);
        var end = Math.Min(body.Length, idx + query.Length + 50);
        var snippet = body[start..end].Replace('\n', ' ').Replace('\r', ' ');
        if (start > 0) snippet = "…" + snippet;
        if (end < body.Length) snippet = snippet + "…";
        return snippet;
    }

    private static IEnumerable<string> ParseSources(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.Equals("all", StringComparison.OrdinalIgnoreCase))
            return ["prompts", "tools", "files", "errors", "extracts"];
        return spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(s => s.ToLowerInvariant());
    }

    private static bool TryParseSince(string s, out DateTime? value)
    {
        value = null;
        s = s.Trim();
        if (s.Length >= 2 && char.IsLetter(s[^1]) && double.TryParse(
                s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            TimeSpan d = s[^1] switch
            {
                'd' or 'D' => TimeSpan.FromDays(n),
                'h' or 'H' => TimeSpan.FromHours(n),
                'm' or 'M' => TimeSpan.FromMinutes(n),
                's' or 'S' => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
            if (d == TimeSpan.Zero) return false;
            value = DateTime.UtcNow - d;
            return true;
        }
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            value = dt; return true;
        }
        return false;
    }

    private static DateTime? TryDate(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(p.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    private static string Short(string s) => s.Length <= 14 ? s : s[..14];
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private sealed record MatchRow(string Source, DateTime? TimestampUtc, string SessionId, string Snippet);
}
