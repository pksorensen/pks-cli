using System.Text;
using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class NaturalnessPatternStore : INaturalnessPatternStore
{
    private readonly IWritingPathResolver _paths;

    public NaturalnessPatternStore(IWritingPathResolver paths) { _paths = paths; }

    public async Task<IReadOnlyList<NaturalnessPattern>> LoadAllAsync(CancellationToken ct = default)
    {
        var path = _paths.GlobalNaturalnessPatternsPath;
        if (!File.Exists(path)) return Array.Empty<NaturalnessPattern>();
        var md = await File.ReadAllTextAsync(path, ct);
        return Parse(md);
    }

    public async Task<string> RenderMarkdownAsync(CancellationToken ct = default)
    {
        var path = _paths.GlobalNaturalnessPatternsPath;
        if (!File.Exists(path)) return "";
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task UpsertAsync(NaturalnessPattern pattern, CancellationToken ct = default)
    {
        var existing = (await LoadAllAsync(ct)).ToList();
        var idx = existing.FindIndex(p =>
            string.Equals(p.TriggerSummary?.Trim(), pattern.TriggerSummary?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (idx >= 0)
        {
            existing[idx].AcceptedCount += 1;
            // keep first-seen + accepted-example stable; rejected example is overwritten only if new one is supplied
            if (!string.IsNullOrWhiteSpace(pattern.RejectedExample))
                existing[idx].RejectedExample = pattern.RejectedExample;
        }
        else
        {
            existing.Add(pattern);
        }

        await WriteAllAsync(existing, ct);
    }

    private async Task WriteAllAsync(IReadOnlyList<NaturalnessPattern> patterns, CancellationToken ct)
    {
        var path = _paths.GlobalNaturalnessPatternsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder();
        sb.AppendLine("# Naturalness Patterns");
        sb.AppendLine();
        sb.AppendLine("Append-only learning store. Each `pattern` fence is parsed back in as");
        sb.AppendLine("few-shot for future `pks writing naturalness prompt` calls.");
        sb.AppendLine();
        foreach (var p in patterns)
        {
            sb.AppendLine($"## Pattern: {Sanitize(p.TriggerSummary)}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(p.FirstSeenSource))
                sb.AppendLine($"First seen: {p.FirstSeenSource}");
            sb.AppendLine($"Accepted {p.AcceptedCount} time(s).");
            sb.AppendLine();
            sb.AppendLine("```pattern");
            sb.AppendLine($"trigger_summary: {EscapeYamlScalar(p.TriggerSummary)}");
            sb.AppendLine($"accepted_example: {EscapeYamlScalar(p.AcceptedExample)}");
            if (!string.IsNullOrWhiteSpace(p.RejectedExample))
                sb.AppendLine($"rejected_example: {EscapeYamlScalar(p.RejectedExample!)}");
            if (!string.IsNullOrWhiteSpace(p.AcceptedFromCritic))
                sb.AppendLine($"accepted_from_critic: {EscapeYamlScalar(p.AcceptedFromCritic!)}");
            sb.AppendLine("```");
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static string Sanitize(string s) =>
        (s ?? "").Replace("\n", " ").Trim();

    private static string EscapeYamlScalar(string s)
    {
        var clean = (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return "\"" + clean.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    internal static IReadOnlyList<NaturalnessPattern> Parse(string markdown)
    {
        var patterns = new List<NaturalnessPattern>();
        var fences = Regex.Matches(markdown,
            @"```pattern\s*\n(?<body>[\s\S]*?)\n```",
            RegexOptions.IgnoreCase);

        // Find the surrounding "Accepted N time(s)" marker before each fence by tracking
        // text positions and slicing the preceding section.
        foreach (Match m in fences)
        {
            var body = m.Groups["body"].Value;
            var pat = new NaturalnessPattern();
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var key = line[..colon].Trim();
                var val = line[(colon + 1)..].Trim();
                val = UnquoteYamlScalar(val);
                switch (key)
                {
                    case "trigger_summary": pat.TriggerSummary = val; break;
                    case "accepted_example": pat.AcceptedExample = val; break;
                    case "rejected_example": pat.RejectedExample = val; break;
                    case "accepted_from_critic": pat.AcceptedFromCritic = val; break;
                }
            }

            // Look back at preceding ~600 chars for "Accepted N time(s)"
            var start = Math.Max(0, m.Index - 600);
            var preceding = markdown.Substring(start, m.Index - start);
            var countMatch = Regex.Match(preceding, @"Accepted\s+(\d+)\s+time", RegexOptions.IgnoreCase);
            if (countMatch.Success && int.TryParse(countMatch.Groups[1].Value, out var n))
                pat.AcceptedCount = n;

            var firstSeenMatch = Regex.Match(preceding, @"First seen:\s*(.+)$", RegexOptions.Multiline);
            if (firstSeenMatch.Success)
                pat.FirstSeenSource = firstSeenMatch.Groups[1].Value.Trim();

            if (!string.IsNullOrWhiteSpace(pat.TriggerSummary))
                patterns.Add(pat);
        }
        return patterns;
    }

    private static string UnquoteYamlScalar(string v)
    {
        if (v.Length >= 2 && v.StartsWith('"') && v.EndsWith('"'))
        {
            var inner = v[1..^1];
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return v;
    }
}
