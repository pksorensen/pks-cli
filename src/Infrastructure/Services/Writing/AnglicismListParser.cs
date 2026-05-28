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

    /// Parses the human-editable calque list. Same `literal → alts | why`
    /// shape as anglicisms, just different semantics (the `literal` side is
    /// Danish, the alternatives are what to use instead).
    public static List<CalqueEntry> ParseCalques(string content)
    {
        var entries = new List<CalqueEntry>();
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

            var literal = line[..arrowIdx].Trim();
            var rest = line[(arrowIdx + arrowLen)..].Trim();
            if (literal.Length == 0) continue;

            string? why = null;
            var pipeIdx = rest.IndexOf('|');
            if (pipeIdx >= 0)
            {
                why = rest[(pipeIdx + 1)..].Trim();
                rest = rest[..pipeIdx].Trim();
            }
            var alts = rest
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            entries.Add(new CalqueEntry
            {
                LiteralDanish = literal,
                Alternatives = alts,
                Why = string.IsNullOrEmpty(why) ? null : why,
            });
        }
        return entries;
    }

    public static string RenderCalques(IEnumerable<CalqueEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# pks writing — calques (loan-translations)");
        sb.AppendLine("# A Danish word that is literally translated from English");
        sb.AppendLine("# and carries the wrong meaning in Danish tech context.");
        sb.AppendLine("# Format: literal_danish → alt1, alt2  | why it's wrong");
        sb.AppendLine();
        foreach (var e in entries.OrderBy(e => e.LiteralDanish, StringComparer.Ordinal))
        {
            sb.Append(e.LiteralDanish).Append(" → ");
            sb.Append(string.Join(", ", e.Alternatives));
            if (!string.IsNullOrEmpty(e.Why))
            {
                sb.Append("  | ").Append(e.Why);
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
