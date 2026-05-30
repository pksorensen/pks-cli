using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class NaturalnessMerger : INaturalnessMerger
{
    private readonly IWritingPathResolver _paths;
    private readonly INaturalnessPicksStore _store;

    public NaturalnessMerger(IWritingPathResolver paths, INaturalnessPicksStore store)
    {
        _paths = paths;
        _store = store;
    }

    public NaturalnessCandidatesFile Merge(
        string sourceFilePath,
        IReadOnlyDictionary<string, NaturalnessCandidatesFile> perCritic,
        int maxAlternativesPerLine = INaturalnessMerger.DefaultMaxAlternativesPerLine,
        IList<string>? debugLog = null)
    {
        if (maxAlternativesPerLine < 1) maxAlternativesPerLine = 1;

        var critics = perCritic.Keys
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // Group all candidates by (line, original-sentence) across critics. A
        // single critic may legitimately produce *multiple* candidates at the
        // same source line — same paragraph, different sentences. Merging
        // those into one entry leads to "2 issues + 6 interleaved alts" UX
        // where the user can only pick a fix for one of the sentences, so we
        // key by the original text too. Cross-critic overlap still merges
        // when both critics quote the same original.
        var byKey = new SortedDictionary<(int Line, string Original), List<(string Critic, NaturalnessCandidate Cand)>>(
            Comparer<(int Line, string Original)>.Create((a, b) =>
            {
                var c = a.Line.CompareTo(b.Line);
                return c != 0 ? c : string.CompareOrdinal(a.Original, b.Original);
            }));
        foreach (var critic in critics)
        {
            var file = perCritic[critic];
            foreach (var cand in file.Candidates)
            {
                var key = (cand.Line, NormalizeOriginal(cand.Original));
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new();
                    byKey[key] = list;
                }
                list.Add((critic, cand));
            }
        }

        // Track per-line ordinals so distinct sentences on the same line get
        // unique IDs (merged-l26-1, merged-l26-2). Single-sentence lines keep
        // the legacy merged-l<N> ID for back-compat with existing picks.
        var perLineCount = new Dictionary<int, int>();
        foreach (var key in byKey.Keys)
        {
            perLineCount[key.Line] = perLineCount.GetValueOrDefault(key.Line) + 1;
        }
        var perLineOrdinal = new Dictionary<int, int>();

        var merged = new List<NaturalnessCandidate>();
        foreach (var ((line, _), group) in byKey)
        {
            // critics_flagging: sorted distinct
            var flagging = group.Select(g => g.Critic)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            // Pick original: prefer the longest; warn if they differ
            string? original = null;
            foreach (var (_, cand) in group)
            {
                if (original is null)
                {
                    original = cand.Original;
                    continue;
                }
                if (!string.Equals(original, cand.Original, StringComparison.Ordinal))
                {
                    debugLog?.Add($"line {line}: critics disagree on original text; using longer of {original.Length}/{cand.Original.Length} chars");
                    if (cand.Original.Length > original.Length) original = cand.Original;
                }
            }

            // Issues: every critic's wording, source-tagged
            var issues = new List<NaturalnessIssue>();
            foreach (var (critic, cand) in group)
            {
                if (!string.IsNullOrWhiteSpace(cand.Issue))
                    issues.Add(new NaturalnessIssue { Source = critic, Text = cand.Issue });
            }

            // Alternatives: each tagged with source, sorted by source then label.
            var alts = new List<NaturalnessAlternative>();
            foreach (var (critic, cand) in group)
            {
                foreach (var alt in cand.Alternatives)
                {
                    alts.Add(new NaturalnessAlternative
                    {
                        Label = alt.Label,
                        Text = alt.Text,
                        Rationale = alt.Rationale,
                        Authorlikeness = alt.Authorlikeness,
                        Source = critic,
                    });
                }
            }

            // Cap: drop lowest authorlikeness first
            if (alts.Count > maxAlternativesPerLine)
            {
                alts = alts
                    .OrderByDescending(a => a.Authorlikeness)
                    .Take(maxAlternativesPerLine)
                    .ToList();
            }

            alts = alts
                .OrderBy(a => a.Source ?? "", StringComparer.Ordinal)
                .ThenBy(a => a.Label, StringComparer.Ordinal)
                .ToList();

            // Issues: deterministic order — source asc
            issues = issues
                .OrderBy(i => i.Source, StringComparer.Ordinal)
                .ThenBy(i => i.Text, StringComparer.Ordinal)
                .ToList();

            // Keep single-critic Issue field populated (back-compat) when the
            // line has exactly one issue source.
            var singleIssue = issues.Count == 1 ? issues[0].Text : "";

            // ID stability: single-sentence lines keep the legacy id; multi-
            // sentence lines get a numeric suffix in deterministic key order.
            string id;
            if (perLineCount[line] == 1)
            {
                id = $"merged-l{line}";
            }
            else
            {
                var ord = perLineOrdinal.GetValueOrDefault(line) + 1;
                perLineOrdinal[line] = ord;
                id = $"merged-l{line}-{ord}";
            }

            merged.Add(new NaturalnessCandidate
            {
                Id = id,
                Line = line,
                Original = original ?? "",
                Issue = singleIssue,
                Issues = issues,
                CriticsFlagging = flagging,
                Alternatives = alts,
            });
        }

        return new NaturalnessCandidatesFile
        {
            Post = sourceFilePath,
            Critics = critics,
            MergedAt = DateTime.UtcNow,
            ExtractedAt = DateTime.UtcNow,
            Candidates = merged,
        };
    }

    /// <summary>
    /// Normalises an original-sentence string for grouping. Trims surrounding
    /// whitespace so cosmetic variation doesn't split a cross-critic merge,
    /// but preserves casing and inner punctuation.
    /// </summary>
    private static string NormalizeOriginal(string? s) => (s ?? string.Empty).Trim();

    public async Task<string> MergeAsync(
        string sourceFilePath,
        int maxAlternativesPerLine = INaturalnessMerger.DefaultMaxAlternativesPerLine,
        CancellationToken ct = default)
    {
        var canonicalPath = _paths.NaturalnessCandidatesSidecarPath(sourceFilePath);
        var perCritic = await _store.LoadAllPerCriticCandidatesAsync(sourceFilePath, ct);

        if (perCritic.Count == 0)
        {
            // Nothing to merge. Leave existing canonical untouched.
            return canonicalPath;
        }

        var merged = Merge(sourceFilePath, perCritic, maxAlternativesPerLine);
        await _store.SaveCandidatesAsync(sourceFilePath, merged, ct);
        return canonicalPath;
    }
}
