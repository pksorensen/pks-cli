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

        // Group all candidates by source line across critics. Two critics
        // rarely quote the exact same span for the same issue — one flags
        // "short", the other "a much longer original text" — so keying by the
        // original text as well would split one issue into two entries. We key
        // by line alone and reconcile the original below (longest wins, with a
        // debug-log note when they disagree), so a line surfaces as a single
        // merged entry that carries every critic's issues and alternatives.
        var byKey = new SortedDictionary<int, List<(string Critic, NaturalnessCandidate Cand)>>();
        foreach (var critic in critics)
        {
            var file = perCritic[critic];
            foreach (var cand in file.Candidates)
            {
                var key = cand.Line;
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new();
                    byKey[key] = list;
                }
                list.Add((critic, cand));
            }
        }

        var merged = new List<NaturalnessCandidate>();
        foreach (var (line, group) in byKey)
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
                if (!string.Equals(NormalizeOriginal(original), NormalizeOriginal(cand.Original), StringComparison.Ordinal))
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

            // One merged entry per line keeps the stable legacy id.
            var id = $"merged-l{line}";

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
