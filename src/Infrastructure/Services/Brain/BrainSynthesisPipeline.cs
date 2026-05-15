using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainSynthesisPipeline : IBrainSynthesisPipeline
{
    private readonly IBrainPathResolver _paths;
    private readonly IBrainSkillReader _skillReader;
    private readonly IExtractReader _extractReader;
    private readonly IClaudeRunner _claude;

    private static readonly JsonSerializerOptions ContextJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BrainSynthesisPipeline(
        IBrainPathResolver paths,
        IBrainSkillReader skillReader,
        IExtractReader extractReader,
        IClaudeRunner claude)
    {
        _paths = paths;
        _skillReader = skillReader;
        _extractReader = extractReader;
        _claude = claude;
    }

    public async Task<BrainSynthPlan> PlanAsync(BrainSynthOptions options, CancellationToken ct = default)
    {
        var (slug, synthesisDir, extractsDir) = ResolveDirs();
        var extracts = await _extractReader.ReadAllAsync(extractsDir, ct);
        var allClusters = BuildClusters(extracts, options.MinClusterSize);
        var clusters = allClusters;
        if (options.MaxClusters is { } cap && clusters.Count > cap)
            clusters = clusters.Take(cap).ToList();

        var aiCalls = options.NoAi ? 0 : clusters.Count + (extracts.Any(e => e.PromptObservations.Count > 0) ? 1 : 0);
        var (estimate, basis) = EstimateCost(aiCalls, options.Model, extractsDir);
        var duration = EstimateDuration(aiCalls, options.Model, options.MaxParallelism, extractsDir);

        return new BrainSynthPlan
        {
            ProjectSlug = slug,
            SynthesisDir = synthesisDir,
            ExtractsFound = extracts.Count,
            ClustersFound = allClusters.Count,
            ClustersAiSummarized = clusters.Count,
            TotalAiCalls = aiCalls,
            EstimatedCostUsd = estimate,
            EstimateBasis = basis,
            EstimatedDuration = duration,
            TopClusters = clusters.Take(10).Select(c => $"{c.Tag} ({c.SessionIds.Count})").ToList(),
        };
    }

    public async Task<BrainSynthRun> RunAsync(BrainSynthOptions options, IBrainSynthProgress progress, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var run = new BrainSynthRun
        {
            RunId = startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            StartedAtUtc = startedAt,
            FinishedAtUtc = startedAt,
        };

        var (slug, synthesisDir, extractsDir) = ResolveDirs();
        Directory.CreateDirectory(synthesisDir);
        var extracts = await _extractReader.ReadAllAsync(extractsDir, ct);
        run.ExtractsRead = extracts.Count;

        // ALL clusters meeting the min-size threshold go into clusters.json — that's
        // the deterministic backbone the wiki + future phases consume. --max-clusters
        // only restricts which clusters get an AI narrative below.
        var allClusters = BuildClusters(extracts, options.MinClusterSize);
        run.ClustersFound = allClusters.Count;
        await File.WriteAllTextAsync(
            Path.Combine(synthesisDir, "clusters.json"),
            JsonSerializer.Serialize(allClusters.Select(c => new ClusterRecord
            {
                Tag = c.Tag,
                ThemeName = TitleCase(c.Tag),
                SessionCount = c.SessionIds.Count,
                SessionIds = c.SessionIds,
                SessionTitles = c.SessionTitles,
                RelatedTags = c.RelatedTags,
                HotFiles = c.HotFiles,
            }).ToList(), ContextJson),
            ct);
        run.ClustersJsonWritten = true;

        var clusters = allClusters;
        if (options.MaxClusters is { } cap && clusters.Count > cap)
            clusters = clusters.Take(cap).ToList();

        var hasHabits = !options.NoAi && extracts.Any(e => e.PromptObservations.Count > 0);
        var totalCalls = options.NoAi ? 0 : clusters.Count + (hasHabits ? 1 : 0);
        progress.Discovered(totalCalls);

        if (options.NoAi || options.DryRun || totalCalls == 0)
        {
            // Even without AI, emit a deterministic themes-skeleton.md so the user
            // can see what would happen.
            await WriteSkeletonThemesAsync(synthesisDir, clusters, ct);
            run.ThemesWritten = clusters.Count > 0;
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }

        var clusterSkill = await _skillReader.ReadAsync("brain-synth-cluster", null, ct);
        var habitsSkill  = hasHabits ? await _skillReader.ReadAsync("brain-synth-habits",  null, ct) : null;

        long totalIn = 0, totalOut = 0, totalCacheRead = 0, totalCacheCreate = 0;
        double totalCost = 0;
        var clusterSections = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            clusters,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = ct,
            },
            async (cluster, innerCt) =>
            {
                var stage = $"theme:{cluster.Tag}";
                progress.Started(stage);
                // Cap large clusters: send at most 25 sessions per AI call to keep input
                // tokens bounded. The total `sessionCount` is still sent verbatim so the
                // AI knows the full size.
                const int MaxSessionsInAiContext = 25;
                var capped = cluster.Sessions.Take(MaxSessionsInAiContext).ToList();
                var ctxJson = JsonSerializer.Serialize(new
                {
                    tag = cluster.Tag,
                    sessionCount = cluster.SessionIds.Count,
                    sessionsInContext = capped.Count,
                    sessions = capped.Select(s => new
                    {
                        sessionId = s.SessionId,
                        title = s.Title,
                        whatWasWorkedOn = s.WhatWasWorkedOn,
                        userStory = s.UserStory,
                        tags = s.Tags,
                    }).ToList(),
                    commonFiles = cluster.HotFiles,
                    relatedTags = cluster.RelatedTags,
                }, ContextJson);

                var result = await _claude.RunAsync(new ClaudeRunRequest
                {
                    UserPrompt = ctxJson,
                    SystemPrompt = clusterSkill.Body,
                    Model = options.Model,
                    MaxBudgetUsd = options.MaxBudgetUsd,
                }, innerCt);
                AddTokens(result);

                if (result.Success && !string.IsNullOrWhiteSpace(result.ResponseText))
                {
                    var clean = ExtractBetweenMarkers(result.ResponseText, "<<<BEGIN-THEME>>>", "<<<END-THEME>>>");
                    if (clean.Length == 0) clean = result.ResponseText.TrimEnd();
                    clusterSections[cluster.Tag] = clean;
                    progress.Finished(new SynthFinishedInfo(stage, true, result.Model,
                        result.InputTokens, result.OutputTokens,
                        result.CacheReadInputTokens, result.CacheCreationInputTokens,
                        result.CostUsd, result.Duration, null));
                }
                else
                {
                    Interlocked.Increment(ref _failedRef);
                    progress.Finished(new SynthFinishedInfo(stage, false, result.Model,
                        result.InputTokens, result.OutputTokens,
                        result.CacheReadInputTokens, result.CacheCreationInputTokens,
                        result.CostUsd, result.Duration,
                        result.ErrorKind ?? result.Stderr.Trim().Split('\n').FirstOrDefault()));
                }
            });

        // Assemble themes.md in deterministic order (largest cluster first).
        var sb = new StringBuilder();
        sb.AppendLine($"# Themes — {slug}");
        sb.AppendLine();
        sb.AppendLine($"_Synthesised from {extracts.Count} session extract(s) into {clusterSections.Count}/{clusters.Count} AI-narrated cluster(s)._");
        sb.AppendLine();
        foreach (var cluster in clusters)
        {
            if (!clusterSections.TryGetValue(cluster.Tag, out var body)) continue;
            sb.AppendLine(body);
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(synthesisDir, "themes.md"), sb.ToString(), ct);
        run.ThemesWritten = clusterSections.Count > 0;
        run.ClustersWritten = clusterSections.Count;

        // Habits pass — single call.
        if (hasHabits && habitsSkill is not null)
        {
            progress.Started("habits");
            var habitsInput = JsonSerializer.Serialize(new
            {
                projectSlug = slug,
                sessionCount = extracts.Count,
                observations = extracts
                    .SelectMany(e => e.PromptObservations.Select(b => new
                    {
                        sessionId = e.SessionId,
                        sessionTitle = e.Title,
                        bullet = b,
                    })).ToList(),
            }, ContextJson);

            var habitsResult = await _claude.RunAsync(new ClaudeRunRequest
            {
                UserPrompt = habitsInput,
                SystemPrompt = habitsSkill.Body,
                Model = options.Model,
                MaxBudgetUsd = options.MaxBudgetUsd,
            }, ct);
            AddTokens(habitsResult);

            if (habitsResult.Success && !string.IsNullOrWhiteSpace(habitsResult.ResponseText))
            {
                var clean = ExtractBetweenMarkers(habitsResult.ResponseText, "<<<BEGIN-HABITS>>>", "<<<END-HABITS>>>");
                if (clean.Length == 0) clean = habitsResult.ResponseText.TrimEnd();
                await File.WriteAllTextAsync(Path.Combine(synthesisDir, "bad-habits.md"),
                    clean + "\n", ct);
                run.HabitsWritten = true;
                progress.Finished(new SynthFinishedInfo("habits", true, habitsResult.Model,
                    habitsResult.InputTokens, habitsResult.OutputTokens,
                    habitsResult.CacheReadInputTokens, habitsResult.CacheCreationInputTokens,
                    habitsResult.CostUsd, habitsResult.Duration, null));
            }
            else
            {
                Interlocked.Increment(ref _failedRef);
                progress.Finished(new SynthFinishedInfo("habits", false, habitsResult.Model,
                    habitsResult.InputTokens, habitsResult.OutputTokens,
                    habitsResult.CacheReadInputTokens, habitsResult.CacheCreationInputTokens,
                    habitsResult.CostUsd, habitsResult.Duration,
                    habitsResult.ErrorKind ?? "habits-call-failed"));
            }
        }

        run.Failed = _failedRef;
        run.TotalInputTokens = totalIn;
        run.TotalOutputTokens = totalOut;
        run.TotalCacheReadTokens = totalCacheRead;
        run.TotalCacheCreationTokens = totalCacheCreate;
        run.TotalCostUsd = totalCost;
        run.FinishedAtUtc = DateTime.UtcNow;
        return run;

        void AddTokens(ClaudeRunResult r)
        {
            Interlocked.Add(ref totalIn, r.InputTokens);
            Interlocked.Add(ref totalOut, r.OutputTokens);
            Interlocked.Add(ref totalCacheRead, r.CacheReadInputTokens);
            Interlocked.Add(ref totalCacheCreate, r.CacheCreationInputTokens);
            InterlockedAdd(ref totalCost, r.CostUsd);
        }
    }

    private int _failedRef;

    // ── clustering (deterministic) ─────────────────────────────────────────────

    private static List<Cluster> BuildClusters(List<ParsedExtract> extracts, int minSize)
    {
        // Inverted index: tag → sessions.
        var tagToSessions = new Dictionary<string, List<ParsedExtract>>(StringComparer.Ordinal);
        foreach (var e in extracts)
        {
            foreach (var t in e.Tags)
            {
                if (!tagToSessions.TryGetValue(t, out var list)) tagToSessions[t] = list = new();
                list.Add(e);
            }
        }

        var clusters = tagToSessions
            .Where(kv => kv.Value.Count >= minSize)
            .Select(kv =>
            {
                var sessions = kv.Value;
                // Related tags = tags co-occurring with the cluster tag, top 5 by count.
                var related = sessions
                    .SelectMany(s => s.Tags)
                    .Where(t => t != kv.Key)
                    .GroupBy(t => t)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => g.Key)
                    .ToList();
                // Hot files = files mentioned in the bottlenecks/whatworkedon paragraphs
                // (best we can do without re-reading the per-session metadata; Phase 4
                // can refine this if it pulls in the session JSONs).
                var hotFiles = new List<string>();
                return new Cluster
                {
                    Tag = kv.Key,
                    Sessions = sessions,
                    SessionIds = sessions.Select(s => s.SessionId).ToList(),
                    SessionTitles = sessions.Select(s => s.Title ?? s.SessionId).ToList(),
                    RelatedTags = related,
                    HotFiles = hotFiles,
                };
            })
            .OrderByDescending(c => c.SessionIds.Count)
            .ThenBy(c => c.Tag, StringComparer.Ordinal)
            .ToList();
        return clusters;
    }

    private static async Task WriteSkeletonThemesAsync(string dir, List<Cluster> clusters, CancellationToken ct)
    {
        if (clusters.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("# Themes — deterministic skeleton");
        sb.AppendLine();
        sb.AppendLine("_AI synthesis skipped (--no-ai). Re-run `pks brain synth` without --no-ai to get narrative summaries._");
        sb.AppendLine();
        foreach (var c in clusters)
        {
            sb.AppendLine($"## {TitleCase(c.Tag)}");
            sb.AppendLine();
            sb.AppendLine($"**Sessions** ({c.SessionIds.Count}):");
            foreach (var (id, title) in c.SessionIds.Zip(c.SessionTitles))
            {
                sb.AppendLine($"- `{Short(id)}` — {title}");
            }
            if (c.RelatedTags.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("**Related tags**: " + string.Join(", ", c.RelatedTags.Select(t => $"`{t}`")));
            }
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "themes.md"), sb.ToString(), ct);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private (string Slug, string SynthDir, string ExtractsDir) ResolveDirs()
    {
        var cwd = Directory.GetCurrentDirectory();
        var slug = _paths.EncodeSlug(_paths.Normalize(cwd) ?? cwd);
        var projectRoot = _paths.ResolveProjectRoot(cwd)
            ?? throw new InvalidOperationException(
                "pks brain synth must be run inside a git repository — outputs go to ./.pks/brain/synthesis/.");
        return (slug,
                Path.Combine(projectRoot, "synthesis"),
                Path.Combine(projectRoot, "extracts"));
    }

    private static string TitleCase(string slug)
    {
        var words = slug.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => w.Length switch
        {
            0 => "",
            1 => char.ToUpperInvariant(w[0]).ToString(),
            _ => char.ToUpperInvariant(w[0]) + w[1..]
        }));
    }

    private static string Short(string sessionId)
    {
        // Most subagent ids follow "agent-a<hex>" — taking the first 8 chars yields
        // "agent-a8" for everything which collides. Strip the common prefix first.
        if (sessionId.StartsWith("agent-a", StringComparison.Ordinal) && sessionId.Length > 7)
            return "agent-a" + Truncate(sessionId[7..], 8);
        return Truncate(sessionId, 12);
    }

    private static string Truncate(string s, int len) =>
        s.Length <= len ? s : s[..len];

    private static (double TotalUsd, string Basis) EstimateCost(int aiCalls, string? model, string extractsDir)
    {
        if (aiCalls == 0) return (0, "no AI calls (--no-ai)");
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        // Reuse the brain-extract sidecars' avg cost as a proxy — synth calls are
        // roughly similar context size.
        var priorCosts = new List<double>();
        if (Directory.Exists(extractsDir))
        {
            foreach (var sidecar in Directory.EnumerateFiles(extractsDir, "*.meta.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<ExtractMetadata>(
                        File.ReadAllText(sidecar), ContextJson);
                    if (meta is null) continue;
                    if (meta.Model is not null && !meta.Model.Contains(resolvedModel, StringComparison.OrdinalIgnoreCase)) continue;
                    if (meta.CostUsd > 0) priorCosts.Add(meta.CostUsd);
                }
                catch { /* skip */ }
            }
        }
        if (priorCosts.Count >= 3)
        {
            var avg = priorCosts.Average();
            return (avg * aiCalls, $"avg ${avg:0.####}/call from {priorCosts.Count} prior {resolvedModel} extract(s)");
        }
        var perCall = resolvedModel switch
        {
            var s when s.Contains("haiku")  => 0.05,
            var s when s.Contains("sonnet") => 0.20,
            var s when s.Contains("opus")   => 1.00,
            _ => 0.20,
        };
        return (perCall * aiCalls, $"heuristic ${perCall:0.##}/call for {resolvedModel} (no prior runs)");
    }

    private static TimeSpan? EstimateDuration(int aiCalls, string? model, int parallel, string extractsDir)
    {
        if (aiCalls == 0 || parallel <= 0) return null;
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        var priorDurations = new List<double>();
        if (Directory.Exists(extractsDir))
        {
            foreach (var sidecar in Directory.EnumerateFiles(extractsDir, "*.meta.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<ExtractMetadata>(
                        File.ReadAllText(sidecar), ContextJson);
                    if (meta is null || meta.DurationMs <= 0) continue;
                    if (meta.Model is not null && !meta.Model.Contains(resolvedModel, StringComparison.OrdinalIgnoreCase)) continue;
                    priorDurations.Add(meta.DurationMs / 1000.0);
                }
                catch { /* skip */ }
            }
        }
        double perCallSec = priorDurations.Count >= 3 ? priorDurations.Average() : resolvedModel switch
        {
            var s when s.Contains("haiku")  => 45,
            var s when s.Contains("sonnet") => 90,
            var s when s.Contains("opus")   => 180,
            _ => 60,
        };
        var totalSec = perCallSec * aiCalls / Math.Max(1, parallel);
        return TimeSpan.FromSeconds(totalSec);
    }

    /// Extract the body between `<<<BEGIN-...>>>` and `<<<END-...>>>` markers
    /// that the synth skills wrap their output with. Strips the AI's polite
    /// preamble/postamble so themes.md is pure markdown.
    /// Returns empty string if no markers are found.
    private static string ExtractBetweenMarkers(string body, string beginMarker, string endMarker)
    {
        var begin = body.IndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0) return string.Empty;
        var contentStart = begin + beginMarker.Length;
        var end = body.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
        if (end < 0) end = body.Length;
        return body[contentStart..end].Trim('\r', '\n', ' ', '\t');
    }

    private static double InterlockedAdd(ref double target, double value)
    {
        double current, newValue;
        do
        {
            current = target;
            newValue = current + value;
        } while (Interlocked.CompareExchange(ref target, newValue, current) != current);
        return newValue;
    }

    private sealed class Cluster
    {
        public required string Tag { get; set; }
        public required List<ParsedExtract> Sessions { get; set; }
        public required List<string> SessionIds { get; set; }
        public required List<string> SessionTitles { get; set; }
        public required List<string> RelatedTags { get; set; }
        public required List<string> HotFiles { get; set; }
    }
}
