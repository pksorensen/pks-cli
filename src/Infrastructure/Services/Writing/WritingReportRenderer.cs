using System.Text;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public static class WritingReportRenderer
{
    public static string RenderMarkdown(WritingReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Writing report — {Path.GetFileName(report.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine($"- **Channel:** {report.Channel}");
        sb.AppendLine($"- **Generated:** {report.GeneratedUtc:u}");
        if (report.Score is int s)
            sb.AppendLine($"- **Score:** {s}/100");
        if (report.CriticModel is { Length: > 0 } m)
            sb.AppendLine($"- **Critic:** {m}");
        sb.AppendLine($"- **Findings:** {report.Findings.Count}");
        sb.AppendLine();

        if (report.DimensionScores.Count > 0)
        {
            sb.AppendLine("## Dimension scores");
            sb.AppendLine();
            sb.AppendLine("| Dimension | Score |");
            sb.AppendLine("|-----------|------:|");
            foreach (var (name, value) in report.DimensionScores.OrderByDescending(kv => kv.Value))
            {
                var bar = new string('█', value) + new string('░', 5 - value);
                sb.AppendLine($"| {name} | {bar} {value}/5 |");
            }
            sb.AppendLine();
        }

        if (report.Findings.Count > 0)
        {
            sb.AppendLine("## Findings");
            sb.AppendLine();
            sb.AppendLine("| Line | Severity | Rule | Match | Suggestion |");
            sb.AppendLine("|-----:|----------|------|-------|------------|");
            foreach (var f in report.Findings.OrderBy(f => f.Line).ThenBy(f => f.Column))
            {
                var suggestion = f.Suggestions.Count > 0
                    ? string.Join(" / ", f.Suggestions)
                    : "—";
                sb.AppendLine($"| {f.Line} | {f.Severity} | `{Escape(f.RuleId)}` | `{Escape(f.Match)}` | {Escape(suggestion)} |");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.CriticNotes))
        {
            sb.AppendLine("## Critic notes");
            sb.AppendLine();
            sb.AppendLine(report.CriticNotes);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string s) =>
        s.Replace("|", "\\|").Replace("\n", " ");
}
