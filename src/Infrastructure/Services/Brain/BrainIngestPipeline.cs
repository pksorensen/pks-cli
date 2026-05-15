using System.Collections.Concurrent;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainIngestPipeline : IBrainIngestPipeline
{
    private readonly ISessionDiscoveryService _discovery;
    private readonly ISessionParser _parser;
    private readonly IBrainIndexStore _store;
    private readonly IBrainPathResolver _paths;
    private readonly IPricingService _pricing;
    private readonly IPlanFileIndexer _plans;

    public BrainIngestPipeline(
        ISessionDiscoveryService discovery,
        ISessionParser parser,
        IBrainIndexStore store,
        IBrainPathResolver paths,
        IPricingService pricing,
        IPlanFileIndexer plans)
    {
        _discovery = discovery;
        _parser = parser;
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

        // 1) Discover and filter
        var all = _discovery.Enumerate(options.ProjectFilter).ToList();
        progress.Discovered(all.Count);

        var ingestLog = await _store.LoadIngestRunLogAsync(ct);
        var cursors = ingestLog.SessionCursors;

        var eligible = new List<DiscoveredSession>(all.Count);
        var skippedByCursor = 0;
        foreach (var d in all)
        {
            var info = new FileInfo(d.JsonlPath);
            if (!info.Exists) continue;
            if (options.SinceUtc is { } since && info.LastWriteTimeUtc < since) continue;

            if (!options.Force)
            {
                var sessionId = Path.GetFileNameWithoutExtension(d.JsonlPath);
                if (cursors.TryGetValue(sessionId, out var cur) &&
                    cur.SourcePath == d.JsonlPath &&
                    cur.SourceMtimeUtc == info.LastWriteTimeUtc &&
                    cur.Bytes == info.Length)
                {
                    skippedByCursor++;
                    continue;
                }
            }
            eligible.Add(d);
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
            async (d, innerCt) =>
            {
                progress.Started(d.JsonlPath);
                try
                {
                    var parsed = await _parser.ParseAsync(d.JsonlPath, d.ProjectSlug, innerCt);

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

                    cursorWrites[parsed.Metadata.SessionId] = new SessionCursor
                    {
                        SessionId = parsed.Metadata.SessionId,
                        SourcePath = d.JsonlPath,
                        SourceMtimeUtc = parsed.Metadata.SourceMtimeUtc,
                        Bytes = parsed.Metadata.SourceBytes,
                        LineCount = parsed.Metadata.LineCount,
                    };
                    Interlocked.Increment(ref filesIngested);
                    progress.Finished(d.JsonlPath, ingested: true, error: false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    Interlocked.Increment(ref filesFailed);
                    progress.Finished(d.JsonlPath, ingested: false, error: true);
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

        foreach (var (k, v) in cursorWrites) ingestLog.SessionCursors[k] = v;
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

}
