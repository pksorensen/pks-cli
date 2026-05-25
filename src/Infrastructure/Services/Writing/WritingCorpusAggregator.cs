using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

/// Cross-post aggregator. Reads every `*.LEARN.json` under a folder
/// (recursively, skipping `_corpus.LEARN.json`), pools terms across posts,
/// and produces ONE corpus-level `LearnProposal` ready for `pks writing apply`.
///
/// Heuristic — see [[plan]] /home/node/.claude/plans/lazy-dazzling-neumann.md:
///   - Term recurs in ≥ MinPosts distinct posts AND is a verb form
///     (suffix -e / -te / -et / -ede on an English-looking stem)
///       → propose **Anglicism** (danglish pattern: english stem + danish ending).
///   - Term recurs in ≥ MinPosts distinct posts AND is a noun form
///       → propose **Allowlist** (intentional tech-term reuse).
///   - One-off terms are skipped (per-post `learn` already covers them).
public static class WritingCorpusAggregator
{
    /// Tech-term nouns we never treat as verb forms even though they end in -e
    /// (release, feature, queue, …). Add here if the heuristic flags a noun.
    private static readonly HashSet<string> NounAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "release", "feature", "feedback", "performance", "queue",
        "scale", "scope", "issue", "flow",
    };

    public sealed class Options
    {
        public int MinPosts { get; init; } = 2;
        public string Channel { get; init; } = "blog";
    }

    public sealed class TermBucket
    {
        public string Term { get; set; } = "";
        public HashSet<string> Posts { get; } = new(StringComparer.Ordinal);
        public List<string> DanishAlternatives { get; } = new();
        public string? Note { get; set; }
    }

    /// Pure aggregation entry point — used by both the CLI command and tests.
    public static LearnProposal Aggregate(
        IEnumerable<LearnProposal> perPostProposals,
        Options? options = null)
    {
        options ??= new Options();
        var buckets = new Dictionary<string, TermBucket>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in perPostProposals)
        {
            // Group by post = SourcePath (one bucket entry per post per term).
            var postId = p.SourcePath;
            foreach (var a in p.Actions)
            {
                if (a.Kind == LearnActionKind.Lesson) continue;
                if (string.IsNullOrWhiteSpace(a.Term)) continue;

                if (!buckets.TryGetValue(a.Term, out var b))
                {
                    b = new TermBucket { Term = a.Term };
                    buckets[a.Term] = b;
                }
                b.Posts.Add(postId);
                foreach (var alt in a.DanishAlternatives)
                    if (!b.DanishAlternatives.Contains(alt))
                        b.DanishAlternatives.Add(alt);
                if (b.Note is null && !string.IsNullOrWhiteSpace(a.Note))
                    b.Note = a.Note;
            }
        }

        var actions = new List<LearnAction>();
        foreach (var b in buckets.Values
                     .Where(b => b.Posts.Count >= options.MinPosts)
                     .OrderByDescending(b => b.Posts.Count)
                     .ThenBy(b => b.Term, StringComparer.OrdinalIgnoreCase))
        {
            var isVerb = IsVerbForm(b.Term);
            if (isVerb)
            {
                actions.Add(new LearnAction
                {
                    Kind = LearnActionKind.Anglicism,
                    Accept = true,
                    Term = b.Term,
                    DanishAlternatives = b.DanishAlternatives,
                    Note = b.Note,
                    Rationale = $"Verb-form anglicism: '{b.Term}' has an English stem with a Danish suffix; appeared in {b.Posts.Count} posts across the corpus — consistent danglish pattern.",
                });
            }
            else
            {
                actions.Add(new LearnAction
                {
                    Kind = LearnActionKind.Allowlist,
                    Accept = true,
                    Term = b.Term,
                    Rationale = $"Tech-term noun: '{b.Term}' recurred in {b.Posts.Count} posts; consistent intentional use, not a slip.",
                });
            }
        }

        return new LearnProposal
        {
            SourcePath = "<corpus aggregate>",
            Channel = options.Channel,
            Actions = actions,
        };
    }

    /// Reads every `*.LEARN.json` under `root` (recursively), excluding the
    /// `_corpus.LEARN.json` output file itself. Robust to malformed files —
    /// skips them with a warning.
    public static async Task<(LearnProposal Proposal, List<string> Warnings)> LoadAndAggregateAsync(
        string root, Options? options = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var proposals = new List<LearnProposal>();

        foreach (var path in Directory.EnumerateFiles(root, "*.LEARN.json", SearchOption.AllDirectories)
                     .Where(p => !Path.GetFileName(p).Equals("_corpus.LEARN.json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                var p = JsonSerializer.Deserialize<LearnProposal>(json, WritingProfileStore.JsonOptions);
                if (p is not null) proposals.Add(p);
            }
            catch (JsonException jx)
            {
                warnings.Add($"skip {path}: {jx.Message}");
            }
        }

        return (Aggregate(proposals, options), warnings);
    }

    internal static bool IsVerbForm(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        if (NounAllowlist.Contains(term)) return false;
        var t = term.ToLowerInvariant();
        return t.EndsWith("ede", StringComparison.Ordinal)
            || t.EndsWith("et", StringComparison.Ordinal)
            || t.EndsWith("te", StringComparison.Ordinal)
            || t.EndsWith("e", StringComparison.Ordinal);
    }
}
