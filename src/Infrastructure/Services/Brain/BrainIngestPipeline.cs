using System.Collections.Concurrent;
using PKS.Infrastructure.Services.Brain.Asf;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainIngestPipeline : IBrainIngestPipeline
{
    private readonly IReadOnlyList<IAgentSessionSource> _sources;
    private readonly ISecretMasker _masker;
    private readonly AsfSessionProjector _projector;
    private readonly IBrainIndexStore _store;
    private readonly IBrainPathResolver _paths;
    private readonly IPricingService _pricing;
    private readonly IPlanFileIndexer _plans;

    public BrainIngestPipeline(
        IEnumerable<IAgentSessionSource> sources,
        ISecretMasker masker,
        AsfSessionProjector projector,
        IBrainIndexStore store,
        IBrainPathResolver paths,
        IPricingService pricing,
        IPlanFileIndexer plans)
    {
        _sources = sources.ToList();
        _masker = masker;
        _projector = projector;
        _store = store;
        _paths = paths;
        _pricing = pricing;
        _plans = plans;
    }

    public async Task<IngestRun> RunAsync(IngestOptions options, IIngestProgress progress, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var runId = startedAt.ToString("yyyyMMdd-HHmmss-fff");

        await _store.EnsureGlobalLayoutAsync(ct);

        // 1) Discover across every installed tool. A source that isn't installed
        //    contributes nothing and is not an error — most machines run a subset.
        var all = new List<(IAgentSessionSource Source, DiscoveredAgentSession Session)>();
        foreach (var source in _sources)
        {
            if (options.SourceFilter is { Length: > 0 } wanted &&
                !string.Equals(source.Kind, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            if (!source.IsAvailable) continue;

            foreach (var discovered in source.Discover(options.ProjectFilter))
                all.Add((source, discovered));
        }
        progress.Discovered(all.Count);

        var ingestLog = await _store.LoadIngestRunLogAsync(ct);
        var cursors = ingestLog.SessionCursors;

        var eligible = new List<(IAgentSessionSource Source, DiscoveredAgentSession Session)>(all.Count);
        var skippedByCursor = 0;
        foreach (var item in all)
        {
            var d = item.Session;
            if (options.SinceUtc is { } since && d.LastModifiedUtc < since) continue;

            if (!options.Force && LookupCursor(cursors, d) is { } cur &&
                cur.SourcePath == d.SourcePath &&
                cur.SourceMtimeUtc == d.LastModifiedUtc &&
                cur.Bytes == d.Bytes)
            {
                skippedByCursor++;
                continue;
            }

            eligible.Add(item);
        }
        if (options.Limit is { } cap && eligible.Count > cap)
            eligible = eligible.Take(cap).ToList();
        progress.Filtered(eligible.Count, skippedByCursor);

        // 2) Track touched project slugs. Per-project rollups are derived from
        //    disk (not from in-memory accumulators), so we only need the set.
        var touchedSlugs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var run = new IngestRun
        {
            RunId = runId,
            StartedAtUtc = startedAt,
            FinishedAtUtc = startedAt,
            FilesScanned = all.Count,
            FilesSkippedUpToDate = skippedByCursor,
        };

        var planEventsBag = new ConcurrentBag<PlanEvent>();
        var sessionMatchRefs = new ConcurrentBag<SessionMetadata>();

        long promptsTotal = 0, toolsTotal = 0, filesTotal = 0, errorsTotal = 0;
        int filesIngested = 0, filesFailed = 0;
        var cursorWrites = new ConcurrentDictionary<string, SessionCursor>(StringComparer.Ordinal);

        // 3) Parallel ingest
        await Parallel.ForEachAsync(
            eligible,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = ct,
            },
            async (item, innerCt) =>
            {
                var (source, d) = item;
                progress.Started(d.SourcePath);
                try
                {
                    // The ASF stream is the single source of truth: the same events
                    // that `brain export` uploads are what the local index is built
                    // from, so the two can never disagree about a day's totals.
                    var events = new List<AsfEvent>();
                    await foreach (var e in source.ReadAsync(d, _masker, innerCt))
                        events.Add(e);

                    var parsed = _projector.Project(d, events);

                    // Attribute cost per model
                    double sessionCost = 0;
                    foreach (var t in parsed.Metadata.TokensByModel)
                    {
                        var p = await _pricing.GetPricingAsync(t.Model, innerCt);
                        if (p is not null)
                        {
                            t.EstimatedCostUsd = _pricing.EstimateCost(p,
                                t.InputTokens, t.OutputTokens, t.CacheReadInputTokens, t.CacheCreationInputTokens);
                            sessionCost += t.EstimatedCostUsd;
                        }
                    }
                    parsed.Metadata.EstimatedCostUsd = sessionCost;

                    // Normalize cwd → realCwd
                    parsed.Metadata.RealCwd = _paths.Normalize(parsed.Metadata.Cwd);

                    await _store.WriteSessionMetadataAsync(parsed.Metadata, innerCt);

                    if (parsed.Prompts.Count > 0)
                        await _store.AppendFirehoseAsync(BrainFirehose.Prompts, parsed.Prompts, innerCt);
                    if (parsed.ToolCalls.Count > 0)
                        await _store.AppendFirehoseAsync(BrainFirehose.Tools, parsed.ToolCalls, innerCt);
                    if (parsed.FileOps.Count > 0)
                        await _store.AppendFirehoseAsync(BrainFirehose.Files, parsed.FileOps, innerCt);
                    if (parsed.Errors.Count > 0)
                        await _store.AppendFirehoseAsync(BrainFirehose.Errors, parsed.Errors, innerCt);

                    Interlocked.Add(ref promptsTotal, parsed.Prompts.Count);
                    Interlocked.Add(ref toolsTotal, parsed.ToolCalls.Count);
                    Interlocked.Add(ref filesTotal, parsed.FileOps.Count);
                    Interlocked.Add(ref errorsTotal, parsed.Errors.Count);

                    touchedSlugs.TryAdd(d.ProjectSlug, 0);

                    // Plan-matching feed
                    foreach (var ev in parsed.PlanEvents) planEventsBag.Add(ev);
                    sessionMatchRefs.Add(parsed.Metadata);

                    cursorWrites[d.CursorKey] = new SessionCursor
                    {
                        SessionId = parsed.Metadata.SessionId,
                        SourceKind = d.SourceKind,
                        SourcePath = d.SourcePath,
                        SourceMtimeUtc = d.LastModifiedUtc,
                        Bytes = d.Bytes,
                        LineCount = parsed.Metadata.LineCount,
                    };
                    Interlocked.Increment(ref filesIngested);
                    progress.Finished(d.SourcePath, ingested: true, error: false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    Interlocked.Increment(ref filesFailed);
                    progress.Finished(d.SourcePath, ingested: false, error: true);
                }
            });

        // 4) Write per-project rollups. We derive from disk (the per-session JSONs
        //    we just wrote) instead of the in-memory state — that way a partial
        //    rerun still produces a fully correct rollup that reflects every
        //    session ever ingested for this project, not just the ones touched
        //    in this run.
        foreach (var slug in touchedSlugs.Keys)
        {
            var rollup = await _store.BuildProjectRollupFromDiskAsync(slug, ct);
            await _store.WriteProjectRollupAsync(rollup, ct);
        }

        // 5) Update cursors + ingest run log
        run.FilesIngested = filesIngested;
        run.FilesFailed = filesFailed;
        run.FinishedAtUtc = DateTime.UtcNow;
        run.PromptsAppended = promptsTotal;
        run.ToolCallsAppended = toolsTotal;
        run.FileOpsAppended = filesTotal;
        run.ErrorsAppended = errorsTotal;

        foreach (var (k, v) in cursorWrites)
        {
            // Drop the pre-multi-source key for the same session, so a Claude
            // session ingested before this change doesn't keep a stale duplicate
            // cursor that would make the log grow by one entry per session.
            if (v.SourceKind == AsfSource.Claude) ingestLog.SessionCursors.Remove(v.SessionId);
            ingestLog.SessionCursors[k] = v;
        }
        ingestLog.Runs.Add(run);
        // Keep only the last 50 runs in the log to stop it growing without bound.
        if (ingestLog.Runs.Count > 50)
            ingestLog.Runs = ingestLog.Runs.OrderByDescending(r => r.StartedAtUtc).Take(50).ToList();
        await _store.SaveIngestRunLogAsync(ingestLog, ct);

        // 6) Master index
        var idx = await _store.LoadIndexAsync(ct);
        idx.ProjectCount = touchedSlugs.Count > idx.ProjectCount ? touchedSlugs.Count : idx.ProjectCount;
        // SessionCount/etc are running totals — recompute from cursors so they
        // reflect what's actually been ingested over all runs.
        idx.SessionCount = ingestLog.SessionCursors.Count;
        idx.PromptCount += promptsTotal;
        idx.ToolCallCount += toolsTotal;
        idx.FileOpCount += filesTotal;
        idx.ErrorCount += errorsTotal;
        idx.LastIngestRunId = run.RunId;
        idx.LastIngestAt = run.FinishedAtUtc;
        idx.LastIngestDuration = run.FinishedAtUtc - run.StartedAtUtc;
        await _store.SaveIndexAsync(idx, ct);

        // 7) Plan-file cross-reference
        var planIndex = await _plans.BuildIndexAsync(
            planEventsBag.ToArray(),
            sessionMatchRefs.ToArray(),
            ct);
        await _store.SavePlanIndexAsync(planIndex, ct);

        return run;
    }

    /// Cursors are keyed by "&lt;source&gt;:&lt;native id&gt;". Logs written before the
    /// multi-source work used the bare session id, so a miss falls back to that
    /// key for Claude — otherwise the first run after upgrading would re-ingest
    /// every session ever seen.
    private static SessionCursor? LookupCursor(
        IReadOnlyDictionary<string, SessionCursor> cursors,
        DiscoveredAgentSession d)
    {
        if (cursors.TryGetValue(d.CursorKey, out var cursor)) return cursor;
        if (d.SourceKind != AsfSource.Claude) return null;

        return cursors.TryGetValue(d.NativeSessionId, out var legacy) ? legacy : null;
    }
}
