using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainWikiPipeline : IBrainWikiPipeline
{
    private readonly IBrainPathResolver _paths;
    private readonly IBrainSkillReader _skillReader;
    private readonly IExtractReader _extractReader;
    private readonly IClaudeRunner _claude;

    private static readonly JsonSerializerOptions ContextJson = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BrainWikiPipeline(
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

    public async Task<BrainWikiPlan> PlanAsync(BrainWikiOptions options, CancellationToken ct = default)
    {
        var (synthesisDir, wikiDir, _) = ResolveDirs();
        var (allClusters, eligible) = LoadClusters(synthesisDir, options.MinClusterSize);
        var toRender = options.MaxClusters is { } cap ? eligible.Take(cap).ToList() : eligible;

        var aiCalls = options.NoAi ? 0 : toRender.Count;
        var (estimate, basis) = EstimateCost(aiCalls, options.Model, wikiDir);
        var duration = EstimateDuration(aiCalls, options.Model, options.MaxParallelism, wikiDir);

        return new BrainWikiPlan
        {
            SynthesisDir = synthesisDir,
            WikiDir = wikiDir,
            ClustersDetected = allClusters.Count,
            ClustersEligible = eligible.Count,
            ClustersToRender = toRender.Count,
            EstimatedCostUsd = estimate,
            EstimateBasis = basis,
            EstimatedDuration = duration,
            TopClusters = toRender.Take(10).Select(c => $"{c.Tag} ({c.SessionCount})").ToList(),
        };
    }

    public async Task<BrainWikiRun> RunAsync(BrainWikiOptions options, IBrainWikiProgress progress, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var run = new BrainWikiRun
        {
            RunId = startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            StartedAtUtc = startedAt,
            FinishedAtUtc = startedAt,
        };

        var (synthesisDir, wikiDir, extractsDir) = ResolveDirs();
        Directory.CreateDirectory(wikiDir);

        var (allClusters, eligible) = LoadClusters(synthesisDir, options.MinClusterSize);
        if (allClusters.Count == 0)
        {
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }

        var toRender = options.MaxClusters is { } cap ? eligible.Take(cap).ToList() : eligible;
        progress.Discovered(options.NoAi ? 0 : toRender.Count);

        // Always (re)write the wiki index — it's deterministic and cheap.
        await WriteIndexAsync(wikiDir, allClusters, eligible, toRender, ct);
        run.IndexWritten = true;

        if (options.NoAi || options.DryRun || toRender.Count == 0)
        {
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }

        var skill = await _skillReader.ReadAsync("brain-wiki-page", null, ct);

        // Pre-load all extracts once so each cluster can pick from them in O(1).
        var allExtracts = await _extractReader.ReadAllAsync(extractsDir, ct);
        var extractById = allExtracts.ToDictionary(e => e.SessionId, e => e, StringComparer.Ordinal);

        long totalIn = 0, totalOut = 0, totalCacheRead = 0, totalCacheCreate = 0;
        double totalCost = 0;
        var rendered = new ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            toRender,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = ct,
            },
            async (cluster, innerCt) =>
            {
                progress.Started(cluster.Tag);
                try
                {
                    // Build rich per-session context from the actual extracts (not just titles).
                    const int MaxSessionsInContext = 30;
                    var sessions = cluster.SessionIds
                        .Select(id => extractById.TryGetValue(id, out var e) ? e : null)
                        .Where(e => e is not null)
                        .Cast<ParsedExtract>()
                        .Take(MaxSessionsInContext)
                        .Select(e => new
                        {
                            sessionId = e.SessionId,
                            title = e.Title,
                            whatWasWorkedOn = e.WhatWasWorkedOn,
                            userStory = e.UserStory,
                            whatWorked = e.WhatWorked,
                            whatStruggled = e.WhatStruggled,
                            bottlenecks = e.Bottlenecks,
                            tags = e.Tags,
                        })
                        .ToList();

                    var ctxJson = JsonSerializer.Serialize(new
                    {
                        tag = cluster.Tag,
                        themeName = cluster.ThemeName,
                        sessionCount = cluster.SessionCount,
                        sessionsInContext = sessions.Count,
                        sessions,
                        relatedTags = cluster.RelatedTags,
                        hotFiles = cluster.HotFiles,
                    }, ContextJson);

                    var result = await _claude.RunAsync(new ClaudeRunRequest
                    {
                        UserPrompt = ctxJson,
                        SystemPrompt = skill.Body,
                        Model = options.Model,
                        MaxBudgetUsd = options.MaxBudgetUsd,
                    }, innerCt);
                    AddTokens(result);

                    if (result.Success && !string.IsNullOrWhiteSpace(result.ResponseText))
                    {
                        var clean = ExtractBetweenMarkers(result.ResponseText, "<<<BEGIN-WIKI>>>", "<<<END-WIKI>>>");
                        if (clean.Length == 0) clean = result.ResponseText.TrimEnd();
                        var outPath = Path.Combine(wikiDir, SafeFileName(cluster.Tag) + ".md");
                        await File.WriteAllTextAsync(outPath, clean + "\n", innerCt);
                        rendered.Add(cluster.Tag);
                        progress.Finished(new WikiFinishedInfo(cluster.Tag, true, result.Model,
                            result.InputTokens, result.OutputTokens,
                            result.CacheReadInputTokens, result.CacheCreationInputTokens,
                            result.CostUsd, result.Duration, null));
                    }
                    else
                    {
                        Interlocked.Increment(ref _failed);
                        progress.Finished(new WikiFinishedInfo(cluster.Tag, false, result.Model,
                            result.InputTokens, result.OutputTokens,
                            result.CacheReadInputTokens, result.CacheCreationInputTokens,
                            result.CostUsd, result.Duration,
                            result.ErrorKind ?? result.Stderr.Trim().Split('\n').FirstOrDefault()));
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    progress.Finished(new WikiFinishedInfo(cluster.Tag, false, null,
                        0, 0, 0, 0, 0, TimeSpan.Zero, ex.Message));
                }
            });

        // Re-write the index now that pages exist (linking them properly).
        await WriteIndexAsync(wikiDir, allClusters, eligible, toRender, ct);

        run.ClustersRendered = rendered.Count;
        run.Failed = _failed;
        run.TotalInputTokens = totalIn;
        run.TotalOutputTokens = totalOut;
        run.TotalCacheReadTokens = totalCacheRead;
        run.TotalCacheCreationTokens = totalCacheCreate;
        run.TotalCostUsd = totalCost;
        run.RenderedPages = rendered.OrderBy(t => t, StringComparer.Ordinal).ToList();
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

    private int _failed;

    // ── helpers ───────────────────────────────────────────────────────────────

    private (string SynthesisDir, string WikiDir, string ExtractsDir) ResolveDirs()
    {
        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd)
            ?? throw new InvalidOperationException(
                "pks brain wiki must be run inside a git repository — outputs go to ./.pks/brain/wiki/.");
        return (Path.Combine(projectRoot, "synthesis"),
                Path.Combine(projectRoot, "wiki"),
                Path.Combine(projectRoot, "extracts"));
    }

    private static (List<ClusterRecord> All, List<ClusterRecord> Eligible) LoadClusters(string synthesisDir, int minSize)
    {
        var clustersPath = Path.Combine(synthesisDir, "clusters.json");
        if (!File.Exists(clustersPath)) return (new(), new());
        try
        {
            var json = File.ReadAllText(clustersPath);
            var all = JsonSerializer.Deserialize<List<ClusterRecord>>(json, ContextJson) ?? new();
            var eligible = all.Where(c => c.SessionCount >= minSize)
                              .OrderByDescending(c => c.SessionCount)
                              .ThenBy(c => c.Tag, StringComparer.Ordinal)
                              .ToList();
            return (all, eligible);
        }
        catch (JsonException) { return (new(), new()); }
    }

    private static async Task WriteIndexAsync(string wikiDir,
        List<ClusterRecord> all, List<ClusterRecord> eligible, List<ClusterRecord> renderedPlan,
        CancellationToken ct)
    {
        var renderedSet = renderedPlan.Select(c => c.Tag).ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("# Wiki");
        sb.AppendLine();
        sb.AppendLine($"_Auto-generated index of {eligible.Count} cluster(s) (of {all.Count} detected). " +
                      $"Updated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC._");
        sb.AppendLine();
        sb.AppendLine("## By size");
        sb.AppendLine();
        foreach (var c in eligible)
        {
            var fname = SafeFileName(c.Tag) + ".md";
            var fullPath = Path.Combine(wikiDir, fname);
            var hasPage = File.Exists(fullPath);
            var marker = hasPage ? "" : " _(not yet rendered)_";
            var link = hasPage ? $"[{c.ThemeName}]({fname})" : c.ThemeName;
            sb.AppendLine($"- {link} — `{c.Tag}` · {c.SessionCount} session(s){marker}");
        }
        sb.AppendLine();
        if (all.Count - eligible.Count > 0)
        {
            sb.AppendLine($"_({all.Count - eligible.Count} clusters below the min-size threshold are not surfaced as pages — see `synthesis/clusters.json`.)_");
        }
        await File.WriteAllTextAsync(Path.Combine(wikiDir, "index.md"), sb.ToString(), ct);
    }

    private static string SafeFileName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var clean = new string(chars).Trim('-');
        return clean.Length == 0 ? "unnamed" : clean.ToLowerInvariant();
    }

    private static (double TotalUsd, string Basis) EstimateCost(int aiCalls, string? model, string wikiDir)
    {
        if (aiCalls == 0) return (0, "no AI calls (--no-ai)");
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        // Wiki pages tend to use more input than synth clusters (full extract content)
        // — bias the heuristic ~1.5×.
        var perCall = resolvedModel switch
        {
            var s when s.Contains("haiku")  => 0.08,
            var s when s.Contains("sonnet") => 0.30,
            var s when s.Contains("opus")   => 1.50,
            _ => 0.30,
        };
        return (perCall * aiCalls, $"heuristic ${perCall:0.##}/call for {resolvedModel}");
    }

    private static TimeSpan? EstimateDuration(int aiCalls, string? model, int parallel, string wikiDir)
    {
        if (aiCalls == 0 || parallel <= 0) return null;
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        double perCallSec = resolvedModel switch
        {
            var s when s.Contains("haiku")  => 50,
            var s when s.Contains("sonnet") => 100,
            var s when s.Contains("opus")   => 200,
            _ => 60,
        };
        var totalSec = perCallSec * aiCalls / Math.Max(1, parallel);
        return TimeSpan.FromSeconds(totalSec);
    }

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
}
