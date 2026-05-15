using System.Text.Json;
using System.Text.RegularExpressions;

namespace PKS.Infrastructure.Services.Brain;

public sealed class ExtractReader : IExtractReader
{
    private static readonly Regex TagPattern = new(@"`([a-z0-9][a-z0-9_-]{1,40})`", RegexOptions.Compiled);
    private static readonly Regex H1Pattern  = new(@"^#\s+(?:Session\s+\S+\s+—\s+)?(?<title>.+)$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions SidecarJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<List<ParsedExtract>> ReadAllAsync(string extractsDir, CancellationToken ct = default)
    {
        var results = new List<ParsedExtract>();
        if (!Directory.Exists(extractsDir)) return results;
        foreach (var md in Directory.EnumerateFiles(extractsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var parsed = await ReadAsync(md, ct);
            if (parsed is not null) results.Add(parsed);
        }
        return results;
    }

    public async Task<ParsedExtract?> ReadAsync(string mdFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(mdFilePath)) return null;
        var text = await File.ReadAllTextAsync(mdFilePath, ct);
        var sessionId = Path.GetFileNameWithoutExtension(mdFilePath);

        var extract = new ParsedExtract
        {
            SessionId = sessionId,
            FilePath = mdFilePath,
        };

        // Line-based section walker. The skill template defines fixed H2 sections —
        // we accumulate lines into the matching bucket and switch on each `## ` line.
        var section = "preamble";
        var buf = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                var h1 = H1Pattern.Match(line);
                if (h1.Success) extract.Title = h1.Groups["title"].Value.Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush(section, buf, extract);
                section = NormalizeHeading(line[3..]);
                buf.Clear();
                continue;
            }

            buf.Add(line);
        }
        Flush(section, buf, extract);

        // Sidecar metadata sits next to the .md as <id>.meta.json
        var sidecarPath = Path.Combine(Path.GetDirectoryName(mdFilePath)!, sessionId + ".meta.json");
        if (File.Exists(sidecarPath))
        {
            try
            {
                extract.Sidecar = JsonSerializer.Deserialize<ExtractMetadata>(
                    await File.ReadAllTextAsync(sidecarPath, ct), SidecarJson);
            }
            catch (JsonException) { /* tolerate stale/corrupt sidecar */ }
        }

        return extract;
    }

    private static void Flush(string section, IReadOnlyList<string> raw, ParsedExtract extract)
    {
        var lines = raw.Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return;

        var bullets = lines.Where(l => l.StartsWith("- ") || l.StartsWith("* "))
                           .Select(l => l[2..].Trim())
                           .ToList();
        var paragraph = string.Join(' ', lines.Where(l => !l.StartsWith("- ") && !l.StartsWith("* "))).Trim();

        switch (section)
        {
            case "what was worked on":
                extract.WhatWasWorkedOn = paragraph;
                break;
            case "what worked / what struggled":
            case "what worked or what struggled":
                foreach (var b in bullets)
                {
                    if (b.StartsWith("✓")) extract.WhatWorked.Add(b.TrimStart('✓').Trim());
                    else if (b.StartsWith("⚠")) extract.WhatStruggled.Add(b.TrimStart('⚠').Trim());
                    else if (b.Length > 0) extract.WhatWorked.Add(b);   // fallback bucket
                }
                break;
            case "bottlenecks & token-waste signals":
            case "bottlenecks and token-waste signals":
            case "bottlenecks":
                extract.Bottlenecks.AddRange(bullets);
                break;
            case "prompt-technique observations":
            case "prompt technique observations":
                extract.PromptObservations.AddRange(bullets);
                break;
            case "reconstructed feature / user-story":
            case "reconstructed feature/user-story":
            case "reconstructed feature":
                if (paragraph.Length > 0) extract.UserStory = paragraph;
                break;
            case "tags":
                // Either `tag1`, `tag2`, `tag3` on one line, or one tag per line in
                // backticks. Capture all backtick-wrapped lowercase identifiers.
                foreach (var l in lines)
                {
                    foreach (Match m in TagPattern.Matches(l))
                    {
                        var t = m.Groups[1].Value;
                        if (!extract.Tags.Contains(t)) extract.Tags.Add(t);
                    }
                }
                break;
        }
    }

    private static string NormalizeHeading(string s) =>
        s.Trim().ToLowerInvariant();
}
