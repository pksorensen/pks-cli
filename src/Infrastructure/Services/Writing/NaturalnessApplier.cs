using System.Text;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class NaturalnessApplier : INaturalnessApplier
{
    public NaturalnessApplyPlan Plan(string postContent, NaturalnessCandidatesFile candidates, NaturalnessPicksFile picks)
    {
        var byId = candidates.Candidates.ToDictionary(c => c.Id, StringComparer.Ordinal);
        // Compute the last line of the YAML frontmatter (the closing `---`).
        // Candidates whose source line falls inside the frontmatter (title /
        // subtitle / description) get applied to the *body* by other surfaces
        // — the markdown applier must not touch YAML keys, or it will eat
        // their wrapper (e.g. `description: "..."` → naked prose).
        var frontmatterCloseLine = FindFrontmatterCloseLine(postContent);
        var edits = new List<NaturalnessApplyEdit>();
        foreach (var pick in picks.Picks)
        {
            if (pick.Applied) continue;
            if (pick.Chosen.Equals("skip", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byId.TryGetValue(pick.CandidateId, out var cand)) continue;
            if (frontmatterCloseLine > 0 && cand.Line <= frontmatterCloseLine) continue;

            string? replacement = null;
            string? acceptedFromCritic = null;
            string triggerSummary = cand.Issue;
            if (pick.Chosen.Equals("other", StringComparison.OrdinalIgnoreCase))
            {
                replacement = pick.CustomText;
            }
            else
            {
                // chosen may be "A" or "A-opus" — split off the source if present.
                ParseChosen(pick.Chosen, out var label, out var source);
                NaturalnessAlternative? alt = null;
                if (source is not null)
                {
                    alt = cand.Alternatives.FirstOrDefault(a =>
                        a.Label.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.Source, source, StringComparison.Ordinal));
                }
                if (alt is null)
                {
                    alt = cand.Alternatives.FirstOrDefault(a =>
                        a.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
                }
                replacement = alt?.Text;
                acceptedFromCritic = alt?.Source;
                // If the candidate has multi-source issues, prefer the one from
                // the accepted critic for a better pattern trigger summary.
                if (cand.Issues is { Count: > 0 } && acceptedFromCritic is not null)
                {
                    var matching = cand.Issues.FirstOrDefault(i =>
                        string.Equals(i.Source, acceptedFromCritic, StringComparison.Ordinal));
                    if (matching is not null && !string.IsNullOrWhiteSpace(matching.Text))
                        triggerSummary = matching.Text;
                }
            }
            if (string.IsNullOrWhiteSpace(replacement)) continue;

            edits.Add(new NaturalnessApplyEdit
            {
                CandidateId = pick.CandidateId,
                Line = cand.Line,
                Original = cand.Original,
                Replacement = replacement!,
                Chosen = pick.Chosen,
                TriggerSummary = triggerSummary,
                AcceptedFromCritic = acceptedFromCritic,
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

    internal static void ParseChosen(string chosen, out string label, out string? source)
    {
        if (string.IsNullOrEmpty(chosen)) { label = ""; source = null; return; }
        var dash = chosen.IndexOf('-');
        if (dash < 0)
        {
            label = chosen;
            source = null;
            return;
        }
        label = chosen[..dash];
        var rest = chosen[(dash + 1)..];
        source = string.IsNullOrEmpty(rest) ? null : rest;
    }

    private static bool ContainsCore(string haystack, string needle)
    {
        // Use a 24-char core fragment so minor whitespace edits don't break the match.
        var trimmed = (needle ?? "").Trim();
        if (trimmed.Length == 0) return false;
        var core = trimmed.Length > 24 ? trimmed.Substring(0, 24) : trimmed;
        return haystack.Contains(core, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the 1-based line number of the closing <c>---</c> of the YAML
    /// frontmatter, or 0 if the post has no frontmatter. Used by Plan to
    /// suppress edits that would otherwise rewrite a YAML key's value line.
    /// </summary>
    internal static int FindFrontmatterCloseLine(string postContent)
    {
        if (string.IsNullOrEmpty(postContent)) return 0;
        var normalised = postContent.Replace("\r\n", "\n");
        if (!normalised.StartsWith("---", StringComparison.Ordinal)) return 0;
        var lines = normalised.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i] == "---") return i + 1; // 1-based
        }
        return 0;
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
