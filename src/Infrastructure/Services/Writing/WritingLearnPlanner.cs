using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Pure function that turns a `WritingReport` into a `LearnProposal` an agent
/// can review. Deterministic — no LLM calls. Heuristics:
///   - Same term flagged ≥ 3 times → propose Allowlist (likely intentional).
///   - Same term flagged 1-2 times → propose Anglicism (likely typo / slip).
///   - Non-terminology finding (Tone/Hook/Naturalness/Value) → propose Lesson,
///     dedup by (dimension, message).
public static class WritingLearnPlanner
{
    private const int AllowlistThreshold = 3;

    public static LearnProposal Plan(WritingReport report,
        IReadOnlySet<string> currentAllowlist,
        IReadOnlySet<string>? currentAnglicisms = null)
    {
        currentAnglicisms ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var proposal = new LearnProposal
        {
            SourcePath = report.SourcePath,
            Channel = report.Channel,
            DimensionScores = new Dictionary<string, int>(report.DimensionScores),
        };

        // ── Terminology findings: group by lowercased term ────────────────────
        var termGroups = report.Findings
            .Where(f => IsTerminology(f))
            .Where(f => !string.IsNullOrWhiteSpace(f.Match))
            .GroupBy(f => NormalizeTerm(f.Match), StringComparer.OrdinalIgnoreCase);

        foreach (var g in termGroups)
        {
            var term = g.First().Match.Trim();
            if (currentAllowlist.Contains(term)) continue;     // already silenced
            if (currentAnglicisms.Contains(term)) continue;     // already flagged

            var lines = g.Select(f => f.Line).Distinct().OrderBy(l => l).ToList();
            var occurrences = g.Count();

            // Pick a representative entry for suggestions/note.
            var rep = g.First();

            if (occurrences >= AllowlistThreshold)
            {
                proposal.Actions.Add(new LearnAction
                {
                    Kind = LearnActionKind.Allowlist,
                    Accept = true, // strong signal: repeated use = intentional
                    Term = term,
                    EvidenceLines = lines,
                    Rationale = $"'{term}' was flagged {occurrences}× across {lines.Count} lines — repeated use suggests it is an intentional tech term, not a slip.",
                });
            }
            else
            {
                proposal.Actions.Add(new LearnAction
                {
                    Kind = LearnActionKind.Anglicism,
                    Accept = false, // weaker signal: agent should look before accepting
                    Term = term,
                    DanishAlternatives = rep.Suggestions.ToList(),
                    Note = rep.Message,
                    EvidenceLines = lines,
                    Rationale = $"'{term}' flagged {occurrences}× — could be an anglicism worth adding to the list, or a one-off tech term. Agent decides.",
                });
            }
        }

        // ── Non-terminology findings: one lesson per (dimension, message) ─────
        var lessonGroups = report.Findings
            .Where(f => !IsTerminology(f))
            .Where(f => !string.IsNullOrWhiteSpace(f.Message))
            .GroupBy(f => (DimensionOf(f), CanonicalMessage(f.Message)));

        foreach (var g in lessonGroups)
        {
            var (dimension, _) = g.Key;
            var rep = g.First();
            var lines = g.Select(f => f.Line).Distinct().OrderBy(l => l).ToList();

            proposal.Actions.Add(new LearnAction
            {
                Kind = LearnActionKind.Lesson,
                Accept = true,
                Dimension = dimension,
                Lesson = rep.Message,
                EvidenceLines = lines,
                Rationale = $"{dimension} feedback recurring at line{(lines.Count == 1 ? "" : "s")} {string.Join(",", lines)} — worth logging.",
            });
        }

        return proposal;
    }

    private static bool IsTerminology(WritingFinding f) =>
        !f.RuleId.StartsWith("Critic.", StringComparison.Ordinal) ||
        f.RuleId.Equals("Critic.Terminology", StringComparison.Ordinal);

    private static string DimensionOf(WritingFinding f) =>
        f.RuleId.StartsWith("Critic.", StringComparison.Ordinal)
            ? f.RuleId.Substring("Critic.".Length)
            : "Terminology";

    private static string NormalizeTerm(string s) => s.Trim().ToLowerInvariant();

    /// Reduce message variants to a single dedup key. Strips quoted matches
    /// like 'release' so two findings on different terms with the same template
    /// don't get over-merged.
    private static string CanonicalMessage(string message) =>
        message.Length > 100 ? message[..100] : message;
}
