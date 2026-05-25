using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Parses the human-editable anglicism list format:
///
///   # comment
///   deploye → udrulle, deploye  | verb form leaks through
///   feature → funktion
///
/// Lines starting with '#' or blank are ignored. The arrow may be '→' or '->'.
/// The note (after '|') is optional; alternatives are comma-separated.
public static class AnglicismListParser
{
    public static List<AnglicismEntry> Parse(string content)
    {
        var entries = new List<AnglicismEntry>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var arrowIdx = line.IndexOf('→');
            int arrowLen = 1;
            if (arrowIdx < 0)
            {
                arrowIdx = line.IndexOf("->", StringComparison.Ordinal);
                arrowLen = 2;
            }
            if (arrowIdx < 0) continue;

            var english = line[..arrowIdx].Trim();
            var rest = line[(arrowIdx + arrowLen)..].Trim();
            if (english.Length == 0) continue;

            string? note = null;
            var pipeIdx = rest.IndexOf('|');
            if (pipeIdx >= 0)
            {
                note = rest[(pipeIdx + 1)..].Trim();
                rest = rest[..pipeIdx].Trim();
            }

            var alts = rest
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            entries.Add(new AnglicismEntry
            {
                English = english,
                DanishAlternatives = alts,
                Note = string.IsNullOrEmpty(note) ? null : note,
            });
        }
        return entries;
    }

    public static string Render(IEnumerable<AnglicismEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# pks writing — anglicism list");
        sb.AppendLine("# Format: english → danish_alternative1, danish_alternative2  | optional note");
        sb.AppendLine("# Comments start with '#'. Edit by hand or via `pks writing learn`.");
        sb.AppendLine();
        foreach (var e in entries.OrderBy(e => e.English, StringComparer.Ordinal))
        {
            sb.Append(e.English).Append(" → ");
            sb.Append(string.Join(", ", e.DanishAlternatives));
            if (!string.IsNullOrEmpty(e.Note))
            {
                sb.Append("  | ").Append(e.Note);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static List<string> ParseAllowlist(string content) =>
        content.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static string RenderAllowlist(IEnumerable<string> terms)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# pks writing — allowlist");
        sb.AppendLine("# Terms here are never flagged as anglicisms (e.g. tech names).");
        sb.AppendLine("# One term per line. Comments start with '#'.");
        sb.AppendLine();
        foreach (var t in terms.OrderBy(t => t, StringComparer.Ordinal))
        {
            sb.AppendLine(t);
        }
        return sb.ToString();
    }
}
