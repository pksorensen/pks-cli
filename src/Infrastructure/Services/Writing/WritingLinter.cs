using System.Text.RegularExpressions;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class WritingLinter : IWritingLinter
{
    public Task<IReadOnlyList<WritingFinding>> LintAsync(
        string content,
        IReadOnlyList<AnglicismEntry> anglicisms,
        IReadOnlySet<string> allowlist,
        CancellationToken ct = default)
    {
        var findings = new List<WritingFinding>();
        if (anglicisms.Count == 0 || string.IsNullOrEmpty(content))
            return Task.FromResult<IReadOnlyList<WritingFinding>>(findings);

        // Strip fenced code blocks and inline code from the lint surface — they're
        // tech-term territory and would generate noise. We keep line numbers
        // by replacing matched ranges with same-length whitespace.
        var scanText = MaskCodeRegions(content);

        // Skip blanks; lines are referenced 1-indexed in the report.
        var lines = scanText.Split('\n');

        foreach (var entry in anglicisms)
        {
            if (allowlist.Contains(entry.English)) continue;

            // Word-boundary regex, case-insensitive. Danish letters allowed in word chars.
            var pattern = $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(entry.English)}(?![\p{{L}}\p{{N}}_])";
            var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            for (int i = 0; i < lines.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (Match m in rx.Matches(lines[i]))
                {
                    if (allowlist.Contains(m.Value)) continue;

                    findings.Add(new WritingFinding
                    {
                        RuleId = "Writing.Anglicisms",
                        Severity = WritingSeverity.Warning,
                        Line = i + 1,
                        Column = m.Index + 1,
                        Match = m.Value,
                        Message = entry.Note is { Length: > 0 }
                            ? $"'{m.Value}' — {entry.Note}"
                            : $"'{m.Value}' reads as an anglicism in Danish.",
                        Suggestions = new List<string>(entry.DanishAlternatives),
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyList<WritingFinding>>(findings);
    }

    /// Replaces fenced code blocks (``` … ```) and inline `code` spans with
    /// same-length whitespace, preserving line offsets for accurate Line/Column.
    internal static string MaskCodeRegions(string content)
    {
        var chars = content.ToCharArray();

        // Fenced blocks: ``` … ```
        var fence = new Regex(@"```[\s\S]*?```", RegexOptions.Multiline);
        foreach (Match m in fence.Matches(content))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
                if (chars[i] != '\n') chars[i] = ' ';
        }

        // Inline code spans: `…` (single backtick pairs, non-greedy, no newline).
        var inline = new Regex(@"`[^`\n]+`");
        foreach (Match m in inline.Matches(new string(chars)))
        {
            for (int i = m.Index; i < m.Index + m.Length; i++)
                if (chars[i] != '\n') chars[i] = ' ';
        }

        return new string(chars);
    }
}
