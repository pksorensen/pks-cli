using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

/// Indexes ~/.claude/plans/*.md and cross-references each plan to the session
/// that produced it. Match strategy (per plan):
///   1. exact      — plan body hash equals an ExitPlanMode toolInput.plan hash
///                   from one of the sessions just ingested.
///   2. probable   — plan file mtime within 5 minutes of a session's last
///                   assistant turn AND same cwd.
///   3. unresolved — neither of the above matched.
public interface IPlanFileIndexer
{
    Task<PlanIndex> BuildIndexAsync(
        IReadOnlyList<PlanEvent> planEvents,
        IReadOnlyList<SessionMetadata> sessions,
        CancellationToken ct = default);
}

public sealed class PlanFileIndexer : IPlanFileIndexer
{
    private const int ProbableMatchWindowMinutes = 5;

    private readonly IBrainPathResolver _paths;

    public PlanFileIndexer(IBrainPathResolver paths)
    {
        _paths = paths;
    }

    public async Task<PlanIndex> BuildIndexAsync(
        IReadOnlyList<PlanEvent> planEvents,
        IReadOnlyList<SessionMetadata> sessions,
        CancellationToken ct = default)
    {
        var index = new PlanIndex();
        var dir = _paths.ClaudePlansRoot;
        if (!Directory.Exists(dir)) return index;

        // Build lookup: planHash → PlanEvent
        var byHash = new Dictionary<string, PlanEvent>(StringComparer.Ordinal);
        foreach (var ev in planEvents)
        {
            byHash.TryAdd(ev.PlanHash, ev);
        }

        foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            string body;
            try { body = await File.ReadAllTextAsync(path, ct); }
            catch { continue; }

            var hash = ShortHash(body.Trim());
            var firstHeading = ExtractFirstHeading(body);

            var entry = new PlanEntry
            {
                FilePath = path,
                FileName = info.Name,
                FileMtimeUtc = info.LastWriteTimeUtc,
                FileBytes = info.Length,
                BodyHash = hash,
                FirstHeading = firstHeading,
            };

            if (byHash.TryGetValue(hash, out var matched))
            {
                entry.MatchKind = "exact";
                entry.MatchedSessionId = matched.SessionId;
                entry.MatchedProjectSlug = matched.ProjectSlug;
                entry.MatchedToolUseId = matched.ToolUseId;
                entry.MatchReason = "body-hash";
            }
            else
            {
                // Probable: mtime ± window matches a session's last assistant turn.
                var probable = sessions
                    .Where(s => s.LastTimestampUtc.HasValue)
                    .Select(s => (s, gap: Math.Abs((info.LastWriteTimeUtc - s.LastTimestampUtc!.Value).TotalMinutes)))
                    .Where(x => x.gap <= ProbableMatchWindowMinutes)
                    .OrderBy(x => x.gap)
                    .Select(x => x.s)
                    .FirstOrDefault();
                if (probable is not null)
                {
                    entry.MatchKind = "probable";
                    entry.MatchedSessionId = probable.SessionId;
                    entry.MatchedProjectSlug = probable.ProjectSlug;
                    entry.MatchReason = $"mtime±{ProbableMatchWindowMinutes}min";
                }
            }

            index.Entries.Add(entry);
        }

        index.Entries = index.Entries
            .OrderByDescending(e => e.FileMtimeUtc)
            .ToList();
        return index;
    }

    private static string ShortHash(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string? ExtractFirstHeading(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal))
                return line.Substring(2).Trim();
        }
        return null;
    }
}
