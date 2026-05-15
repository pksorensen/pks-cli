using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainAdrPipeline : IBrainAdrPipeline
{
    /// Tags that are architectural by convention — most projects' ADR catalog draws
    /// from this set. Users can extend via --include-tag or replace via --tags.
    private static readonly string[] DefaultArchitecturalTags =
    [
        "architecture", "refactor", "migration", "auth", "authentication", "monorepo",
        "infra", "infrastructure", "deployment", "ci-cd", "devops", "data-model",
        "api-design", "api-routes", "server-actions", "rsc", "protocol", "design-system",
        "security", "observability", "telemetry", "routing", "state-management",
        "plugin-system", "persistence", "multi-tenant", "session-management",
    ];

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

    public BrainAdrPipeline(
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

    public Task<BrainAdrPlan> PlanAsync(BrainAdrOptions options, CancellationToken ct = default)
    {
        var (synthesisDir, adrDir, _) = ResolveDirs();
        var allowed = ResolveAllowedTags(options);
        var (allClusters, eligible) = LoadClusters(synthesisDir, allowed, options.MinClusterSize);
        var toRender = options.MaxAdrs is { } cap ? eligible.Take(cap).ToList() : eligible;
        var aiCalls = options.NoAi ? 0 : toRender.Count;
        var (estimate, basis) = EstimateCost(aiCalls, options.Model);
        var duration = EstimateDuration(aiCalls, options.Model, options.MaxParallelism);

        return Task.FromResult(new BrainAdrPlan
        {
            AdrDir = adrDir,
            ClustersDetected = allClusters.Count,
            ClustersEligible = eligible.Count,
            ClustersToRender = toRender.Count,
            EstimatedCostUsd = estimate,
            EstimateBasis = basis,
            EstimatedDuration = duration,
            Candidates = toRender.Take(15).Select(c => $"{c.Tag} ({c.SessionCount})").ToList(),
            AllowedTags = allowed.OrderBy(t => t, StringComparer.Ordinal).ToList(),
        });
    }

    public async Task<BrainAdrRun> RunAsync(BrainAdrOptions options, IBrainAdrProgress progress, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var run = new BrainAdrRun
        {
            RunId = startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            StartedAtUtc = startedAt,
            FinishedAtUtc = startedAt,
        };

        var (synthesisDir, adrDir, extractsDir) = ResolveDirs();
        Directory.CreateDirectory(adrDir);
        var allowed = ResolveAllowedTags(options);

        var (allClusters, eligible) = LoadClusters(synthesisDir, allowed, options.MinClusterSize);
        if (allClusters.Count == 0)
        {
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }
        var toRender = options.MaxAdrs is { } cap ? eligible.Take(cap).ToList() : eligible;
        progress.Discovered(options.NoAi ? 0 : toRender.Count);

        // Always rewrite the deterministic index, even with --no-ai.
        await WriteIndexAsync(adrDir, allClusters, eligible, toRender, allowed, ct);
        run.IndexWritten = true;

        if (options.NoAi || options.DryRun || toRender.Count == 0)
        {
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }

        var skill = await _skillReader.ReadAsync("brain-adr", null, ct);
        var allExtracts = await _extractReader.ReadAllAsync(extractsDir, ct);
        var extractById = allExtracts.ToDictionary(e => e.SessionId, e => e, StringComparer.Ordinal);

        long totalIn = 0, totalOut = 0, totalCacheRead = 0, totalCacheCreate = 0;
        double totalCost = 0;
        var written = new ConcurrentBag<string>();

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
                        var clean = ExtractBetweenMarkers(result.ResponseText, "<<<BEGIN-ADR>>>", "<<<END-ADR>>>");
                        if (clean.Length == 0) clean = result.ResponseText.TrimEnd();
                        var outPath = Path.Combine(adrDir, SafeFileName(cluster.Tag) + ".md");
                        await File.WriteAllTextAsync(outPath, clean + "\n", innerCt);
                        written.Add(cluster.Tag);
                        progress.Finished(new AdrFinishedInfo(cluster.Tag, true, result.Model,
                            result.InputTokens, result.OutputTokens,
                            result.CacheReadInputTokens, result.CacheCreationInputTokens,
                            result.CostUsd, result.Duration, null));
                    }
                    else
                    {
                        Interlocked.Increment(ref _failed);
                        progress.Finished(new AdrFinishedInfo(cluster.Tag, false, result.Model,
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
                    progress.Finished(new AdrFinishedInfo(cluster.Tag, false, null,
                        0, 0, 0, 0, 0, TimeSpan.Zero, ex.Message));
                }
            });

        // Re-write index now that pages exist for proper links.
        await WriteIndexAsync(adrDir, allClusters, eligible, toRender, allowed, ct);

        run.AdrsWritten = written.Count;
        run.Failed = _failed;
        run.TotalInputTokens = totalIn;
        run.TotalOutputTokens = totalOut;
        run.TotalCacheReadTokens = totalCacheRead;
        run.TotalCacheCreationTokens = totalCacheCreate;
        run.TotalCostUsd = totalCost;
        run.Tags = written.OrderBy(t => t, StringComparer.Ordinal).ToList();
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

    private (string SynthesisDir, string AdrDir, string ExtractsDir) ResolveDirs()
    {
        var cwd = Directory.GetCurrentDirectory();
        var projectRoot = _paths.ResolveProjectRoot(cwd)
            ?? throw new InvalidOperationException(
                "pks brain adr must be run inside a git repository — outputs go to ./.pks/brain/adr/.");
        return (Path.Combine(projectRoot, "synthesis"),
                Path.Combine(projectRoot, "adr"),
                Path.Combine(projectRoot, "extracts"));
    }

    private static HashSet<string> ResolveAllowedTags(BrainAdrOptions options)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.Tags is { Count: > 0 })
        {
            foreach (var t in options.Tags) set.Add(t.Trim());
        }
        else
        {
            foreach (var t in DefaultArchitecturalTags) set.Add(t);
        }
        foreach (var t in options.IncludeTags) set.Add(t.Trim());
        return set;
    }

    private static (List<ClusterRecord> All, List<ClusterRecord> Eligible) LoadClusters(
        string synthesisDir, HashSet<string> allowedTags, int minSize)
    {
        var clustersPath = Path.Combine(synthesisDir, "clusters.json");
        if (!File.Exists(clustersPath)) return (new(), new());
        try
        {
            var json = File.ReadAllText(clustersPath);
            var all = JsonSerializer.Deserialize<List<ClusterRecord>>(json, ContextJson) ?? new();
            var eligible = all
                .Where(c => c.SessionCount >= minSize && allowedTags.Contains(c.Tag))
                .OrderByDescending(c => c.SessionCount)
                .ThenBy(c => c.Tag, StringComparer.Ordinal)
                .ToList();
            return (all, eligible);
        }
        catch (JsonException) { return (new(), new()); }
    }

    private static async Task WriteIndexAsync(string adrDir,
        List<ClusterRecord> all, List<ClusterRecord> eligible, List<ClusterRecord> renderedPlan,
        HashSet<string> allowedTags, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ADR Index");
        sb.AppendLine();
        sb.AppendLine($"_Architectural decisions extracted from session history. " +
                      $"{eligible.Count} eligible cluster(s) of {all.Count} total. " +
                      $"Updated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC._");
        sb.AppendLine();
        sb.AppendLine($"**Architectural tags considered**: " +
                      string.Join(", ", allowedTags.OrderBy(t => t, StringComparer.Ordinal).Select(t => $"`{t}`")));
        sb.AppendLine();
        sb.AppendLine("## Decisions");
        sb.AppendLine();
        if (eligible.Count == 0)
        {
            sb.AppendLine("_No qualifying clusters yet. Lower `--min-cluster-size`, run more `pks brain extract`s, or add tags with `--include-tag`._");
        }
        else
        {
            foreach (var c in eligible)
            {
                var fname = SafeFileName(c.Tag) + ".md";
                var hasPage = File.Exists(Path.Combine(adrDir, fname));
                var marker = hasPage ? "" : " _(not yet rendered)_";
                var link = hasPage ? $"[{c.ThemeName}]({fname})" : c.ThemeName;
                sb.AppendLine($"- {link} — `{c.Tag}` · {c.SessionCount} session(s){marker}");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(adrDir, "index.md"), sb.ToString(), ct);
    }

    private static string SafeFileName(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var clean = new string(chars).Trim('-');
        return clean.Length == 0 ? "unnamed" : clean.ToLowerInvariant();
    }

    private static (double TotalUsd, string Basis) EstimateCost(int aiCalls, string? model)
    {
        if (aiCalls == 0) return (0, "no AI calls (--no-ai)");
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        var perCall = resolvedModel switch
        {
            var s when s.Contains("haiku")  => 0.06,
            var s when s.Contains("sonnet") => 0.25,
            var s when s.Contains("opus")   => 1.20,
            _ => 0.20,
        };
        return (perCall * aiCalls, $"heuristic ${perCall:0.##}/call for {resolvedModel}");
    }

    private static TimeSpan? EstimateDuration(int aiCalls, string? model, int parallel)
    {
        if (aiCalls == 0 || parallel <= 0) return null;
        var resolvedModel = (model ?? "haiku").ToLowerInvariant();
        double perCallSec = resolvedModel switch
        {
            var s when s.Contains("haiku")  => 45,
            var s when s.Contains("sonnet") => 100,
            var s when s.Contains("opus")   => 200,
            _ => 60,
        };
        return TimeSpan.FromSeconds(perCallSec * aiCalls / Math.Max(1, parallel));
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
        do { current = target; newValue = current + value; }
        while (Interlocked.CompareExchange(ref target, newValue, current) != current);
        return newValue;
    }
}
