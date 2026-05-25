using System.Text;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Renders a `LearnProposal` as agent-readable Markdown. The accompanying
/// JSON is the source of truth — this is for humans/agents to scan.
public static class LearnProposalRenderer
{
    public static string RenderMarkdown(LearnProposal p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Learn proposal — {Path.GetFileName(p.SourcePath)}");
        sb.AppendLine();
        sb.AppendLine($"- **Generated:** {p.GeneratedUtc:u}");
        sb.AppendLine($"- **Channel:** {p.Channel}");
        sb.AppendLine($"- **Actions:** {p.Actions.Count} ({p.Actions.Count(a => a.Accept)} accepted by default)");

        if (p.DimensionScores.Count > 0)
        {
            sb.Append("- **Scores:** ");
            sb.AppendLine(string.Join(" · ", p.DimensionScores
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value}/5")));
        }
        sb.AppendLine();
        sb.AppendLine("> **Agent**: review the actions below. Edit the matching `.LEARN.json`");
        sb.AppendLine("> to flip `accept` on any action you disagree with, then run");
        sb.AppendLine($"> `pks writing apply <this-file>.json`.");
        sb.AppendLine();

        RenderSection(sb, "Allowlist proposals", p.Actions.Where(a => a.Kind == LearnActionKind.Allowlist), RenderAllowlistRow,
            new[] { "Accept", "Term", "Lines", "Rationale" });

        RenderSection(sb, "Anglicism proposals", p.Actions.Where(a => a.Kind == LearnActionKind.Anglicism), RenderAnglicismRow,
            new[] { "Accept", "English", "Danish alternatives", "Lines", "Rationale" });

        RenderSection(sb, "Lesson proposals", p.Actions.Where(a => a.Kind == LearnActionKind.Lesson), RenderLessonRow,
            new[] { "Accept", "Dimension", "Lesson", "Lines" });

        return sb.ToString();
    }

    private static void RenderSection(StringBuilder sb, string title,
        IEnumerable<LearnAction> items, Action<StringBuilder, LearnAction> renderRow, string[] headers)
    {
        var list = items.ToList();
        sb.AppendLine($"## {title}  ({list.Count})");
        sb.AppendLine();
        if (list.Count == 0)
        {
            sb.AppendLine("_None._");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var a in list) renderRow(sb, a);
        sb.AppendLine();
    }

    private static void RenderAllowlistRow(StringBuilder sb, LearnAction a)
    {
        sb.Append("| ").Append(a.Accept ? "✅" : "⬜").Append(" | ");
        sb.Append('`').Append(Esc(a.Term ?? "")).Append("` | ");
        sb.Append(string.Join(",", a.EvidenceLines)).Append(" | ");
        sb.AppendLine(Esc(a.Rationale) + " |");
    }

    private static void RenderAnglicismRow(StringBuilder sb, LearnAction a)
    {
        sb.Append("| ").Append(a.Accept ? "✅" : "⬜").Append(" | ");
        sb.Append('`').Append(Esc(a.Term ?? "")).Append("` | ");
        sb.Append(a.DanishAlternatives.Count > 0
            ? string.Join(", ", a.DanishAlternatives)
            : "—").Append(" | ");
        sb.Append(string.Join(",", a.EvidenceLines)).Append(" | ");
        sb.AppendLine(Esc(a.Rationale) + " |");
    }

    private static void RenderLessonRow(StringBuilder sb, LearnAction a)
    {
        sb.Append("| ").Append(a.Accept ? "✅" : "⬜").Append(" | ");
        sb.Append(Esc(a.Dimension ?? "")).Append(" | ");
        sb.Append(Esc(Truncate(a.Lesson ?? "", 160))).Append(" | ");
        sb.AppendLine(string.Join(",", a.EvidenceLines) + " |");
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
