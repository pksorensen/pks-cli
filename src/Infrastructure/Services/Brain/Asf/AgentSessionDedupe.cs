namespace PKS.Infrastructure.Services.Brain.Asf;

/// One session, one copy — applied to the discovery list before anything reads a
/// byte of it.
///
/// Once rescued docker volumes are discoverable, the same session genuinely can
/// be found twice: a devcontainer's config volume was backed up, and the same
/// history also sits in `~/.claude/projects` because the repo is bind-mounted and
/// the transcript was written through it. Two copies, one session.
///
/// The receiving server already folds duplicate event ids, so a double *upload*
/// is harmless. The local index is not so lucky:
/// <see cref="BrainIngestPipeline"/> **appends** to the prompt/tool/file/error
/// firehoses, so ingesting both copies writes every prompt twice and every chart
/// downstream of them is wrong by however many copies existed. That is what this
/// class prevents, and it is why the fix has to happen at discovery rather than
/// at upload.
///
/// The keeper is the *richest* copy rather than the newest one, because a longer
/// file is a superset of a shorter one for append-only transcripts: a container
/// that ran for another hour after the host last saw the session holds the whole
/// session plus the extra hour, never the extra hour alone.
///
/// This trusts `CursorKey` to mean "the same session", so two *different* files
/// that derive the same key are indistinguishable here and one of them is lost.
/// The fix for that is always a unique id at the source — never a looser rule in
/// this class. Loosening it (say, only collapsing across differing origins) would
/// keep both files and leave them sharing one cursor, which makes them overwrite
/// each other's ingest state and re-append on *every* run instead of once.
/// The one known instance was Claude's `journal.jsonl`, now skipped at discovery.
public static class AgentSessionDedupe
{
    /// Collapses copies of one session down to one, keeping source order stable
    /// for everything that survives.
    ///
    /// <paramref name="dropped"/> is the number of copies discarded — surfaced in
    /// the run output, because "the guarantee held" is only believable when the
    /// number it acted on is visible.
    public static List<(TSource Source, DiscoveredAgentSession Session)> OnePerSession<TSource>(
        IEnumerable<(TSource Source, DiscoveredAgentSession Session)> discovered,
        out int dropped)
        where TSource : IAgentSessionSource
    {
        var best = new Dictionary<string, (TSource Source, DiscoveredAgentSession Session)>(StringComparer.Ordinal);
        var order = new List<string>();
        dropped = 0;

        foreach (var candidate in discovered)
        {
            var key = candidate.Session.CursorKey;

            if (!best.TryGetValue(key, out var incumbent))
            {
                best[key] = candidate;
                order.Add(key);

                continue;
            }

            dropped++;
            if (Prefer(candidate.Session, incumbent.Session)) best[key] = candidate;
        }

        return order.Select(k => best[k]).ToList();
    }

    /// True when <paramref name="candidate"/> should replace <paramref name="incumbent"/>.
    ///
    /// Bytes first (more history wins), then mtime (a same-size copy that was
    /// touched later is the live one), then the host copy — reading from
    /// `~/.claude` is cheaper and less likely to vanish mid-run than a rescued
    /// mount. The final path comparison is not a tie-break anyone cares about; it
    /// is there so two identical copies always resolve the same way and a chunk
    /// hash stays reproducible across runs.
    internal static bool Prefer(DiscoveredAgentSession candidate, DiscoveredAgentSession incumbent)
    {
        if (candidate.Bytes != incumbent.Bytes) return candidate.Bytes > incumbent.Bytes;
        if (candidate.LastModifiedUtc != incumbent.LastModifiedUtc)
            return candidate.LastModifiedUtc > incumbent.LastModifiedUtc;

        var candidateIsHost = candidate.Origin is null;
        var incumbentIsHost = incumbent.Origin is null;
        if (candidateIsHost != incumbentIsHost) return candidateIsHost;

        return string.CompareOrdinal(candidate.SourcePath, incumbent.SourcePath) < 0;
    }
}
