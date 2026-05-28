using System.Text;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class NaturalnessApplier : INaturalnessApplier
{
    public NaturalnessApplyPlan Plan(string postContent, NaturalnessCandidatesFile candidates, NaturalnessPicksFile picks)
    {
        var byId = candidates.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var edits = new List<NaturalnessApplyEdit>();
        foreach (var pick in picks.Picks)
        {
            if (pick.Applied) continue;
            if (pick.Chosen.Equals("skip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byId.TryGetValue(pick.CandidateId, out var cand)) continue;

            string? replacement = null;
            if (pick.Chosen.Equals("other", StringComparison.OrdinalIgnoreCase))
            {
                replacement = pick.CustomText;
            }
            else
            {
                var alt = cand.Alternatives.FirstOrDefault(a =>
                    a.Label.Equals(pick.Chosen, StringComparison.OrdinalIgnoreCase));
                replacement = alt?.Text;
            }
            if (string.IsNullOrWhiteSpace(replacement)) continue;

            edits.Add(new NaturalnessApplyEdit
            {
                CandidateId = pick.CandidateId,
                Line = cand.Line,
                Original = cand.Original,
                Replacement = replacement!,
                Chosen = pick.Chosen,
                TriggerSummary = cand.Issue,
            });
        }

        var diff = BuildUnifiedDiff(postContent, edits);
        return new NaturalnessApplyPlan { Edits = edits, UnifiedDiff = diff };
    }

    public NaturalnessApplyResult Apply(string postContent, NaturalnessApplyPlan plan)
    {
        var lines = postContent.Replace("\r\n", "\n").Split('\n').ToList();
        var applied = new List<NaturalnessApplyEdit>();
        var skipped = new List<NaturalnessApplyEdit>();
        var warnings = new List<string>();

        // Apply highest-line-first to keep indices stable when a replacement
        // contains multiple lines.
        foreach (var edit in plan.Edits.OrderByDescending(e => e.Line))
        {
            var idx = edit.Line - 1;
            if (idx < 0 || idx >= lines.Count)
            {
                warnings.Add($"{edit.CandidateId}: line {edit.Line} out of range");
                skipped.Add(edit);
                continue;
            }

            // Line drift safety: prefer line index, but if the line no longer
            // contains a fragment of the original, scan a +/-3 window.
            var target = idx;
            if (!ContainsCore(lines[idx], edit.Original))
            {
                var found = -1;
                for (int delta = 1; delta <= 3 && found < 0; delta++)
                {
                    if (idx - delta >= 0 && ContainsCore(lines[idx - delta], edit.Original)) found = idx - delta;
                    else if (idx + delta < lines.Count && ContainsCore(lines[idx + delta], edit.Original)) found = idx + delta;
                }
                if (found < 0)
                {
                    warnings.Add($"{edit.CandidateId}: line {edit.Line} has drifted — original text not found within ±3 lines");
                    skipped.Add(edit);
                    continue;
                }
                target = found;
            }

            lines[target] = edit.Replacement;
            applied.Add(edit);
        }

        return new NaturalnessApplyResult
        {
            NewContent = string.Join('\n', lines),
            Applied = applied,
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    private static bool ContainsCore(string haystack, string needle)
    {
        // Use a 24-char core fragment so minor whitespace edits don't break the match.
        var trimmed = (needle ?? "").Trim();
        if (trimmed.Length == 0) return false;
        var core = trimmed.Length > 24 ? trimmed.Substring(0, 24) : trimmed;
        return haystack.Contains(core, StringComparison.Ordinal);
    }

    internal static string BuildUnifiedDiff(string postContent, IReadOnlyList<NaturalnessApplyEdit> edits)
    {
        if (edits.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("--- a/post");
        sb.AppendLine("+++ b/post");
        var lines = postContent.Replace("\r\n", "\n").Split('\n');
        foreach (var e in edits.OrderBy(x => x.Line))
        {
            var idx = e.Line - 1;
            if (idx < 0 || idx >= lines.Length) continue;
            sb.AppendLine($"@@ -{e.Line},1 +{e.Line},1 @@");
            sb.AppendLine("-" + lines[idx]);
            sb.AppendLine("+" + e.Replacement);
        }
        return sb.ToString();
    }
}
