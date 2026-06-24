using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain.Models;
using PKS.Infrastructure.Services.Foundry;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainExtractPipeline : IBrainExtractPipeline
{
    private readonly IBrainPathResolver _paths;
    private readonly IBrainSkillReader _skillReader;
    private readonly IBrainExtractContextBuilder _context;
    private readonly IExtractRunnerFactory _runners;
    private readonly IFoundryExtractEnv _foundryEnv;

    private static readonly JsonSerializerOptions ContextJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BrainExtractPipeline(
        IBrainPathResolver paths,
        IBrainSkillReader skillReader,
        IBrainExtractContextBuilder context,
        IExtractRunnerFactory runners,
        IFoundryExtractEnv foundryEnv)
    {
        _paths = paths;
        _skillReader = skillReader;
        _context = context;
        _runners = runners;
        _foundryEnv = foundryEnv;
    }

    public async Task<BrainExtractPlan> PlanAsync(BrainExtractOptions options, CancellationToken ct = default)
    {
        var (slug, extractsDir, eligible, skipped) = await EnumerateAsync(options, ct);
        var (estimate, basis) = EstimateCost(eligible.Count, options.Model, extractsDir);
        var duration = EstimateDuration(eligible.Count, options.Model, options.MaxParallelism, extractsDir);
        return new BrainExtractPlan
        {
            ProjectSlug = slug,
            ExtractsDir = extractsDir,
            Eligible = eligible.Count,
            SkippedByCursor = skipped,
            EstimatedCostUsd = estimate,
            EstimateBasis = basis,
            EstimatedDuration = duration,
            Preview = eligible.Take(10).Select(e => e.SessionId).ToList(),
        };
    }

    private static TimeSpan? EstimateDuration(int eligible, string? model, int parallel, string extractsDir)
    {
        if (eligible == 0 || parallel <= 0) return null;

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
        var totalSec = perCallSec * eligible / Math.Max(1, parallel);
        return TimeSpan.FromSeconds(totalSec);
    }

    public async Task<BrainExtractRun> RunAsync(BrainExtractOptions options, IBrainExtractProgress progress, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var run = new BrainExtractRun
        {
            RunId = startedAt.ToString("yyyyMMdd-HHmmss-fff"),
            StartedAtUtc = startedAt,
            FinishedAtUtc = startedAt,
        };

        var (slug, extractsDir, eligible, skipped) = await EnumerateAsync(options, ct);
        run.Eligible = eligible.Count;
        run.SkippedUpToDate = skipped;
        progress.Discovered(eligible.Count);

        if (options.DryRun || eligible.Count == 0)
        {
            run.FinishedAtUtc = DateTime.UtcNow;
            return run;
        }

        // 2) read skill once
        var skill = await _skillReader.ReadAsync(options.SkillPath, ct);
        var skillHash = ShortHash(skill.Body);

        // 2b) pick the summarizer backend, and (for the claude binary path) start a single
        // shared Foundry MSI token server for the whole run when --foundry is requested.
        var runner = _runners.Resolve(options.Agent);
        var isClaudeBinary = string.Equals(options.Agent, "claude", StringComparison.OrdinalIgnoreCase);
        FoundryEnvVars? foundryEnv = null;
        await using var foundrySession = options.UseFoundry && isClaudeBinary
            ? await _foundryEnv.StartAsync(ct)
            : null;
        if (options.UseFoundry && isClaudeBinary)
        {
            if (foundrySession is null)
            {
                // --foundry asked for but unavailable (not logged in). Abort cleanly rather
                // than silently billing the default Anthropic plan.
                run.FinishedAtUtc = DateTime.UtcNow;
                return run;
            }
            foundryEnv = foundrySession.EnvVars;
        }

        // 3) extract in parallel (capped) — aggregate token + cost totals as we go.
        var extracted = 0;
        var failed = 0;
        long totalIn = 0, totalOut = 0, totalCacheRead = 0, totalCacheCreate = 0;
        double totalCost = 0;
        var extractedIds = new System.Collections.Concurrent.ConcurrentBag<string>();

        await Parallel.ForEachAsync(
            eligible,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelism),
                CancellationToken = ct,
            },
            async (item, innerCt) =>
            {
                progress.Started(item.SessionId);
                try
                {
                    var ctx = await _context.BuildAsync(item.SessionId, slug, innerCt);
                    if (ctx is null)
                    {
                        Interlocked.Increment(ref failed);
                        progress.Finished(new ExtractFinishedInfo(
                            item.SessionId, false, null, 0, 0, 0, 0, 0, TimeSpan.Zero, "no-context"));
                        return;
                    }

                    var contextJson = JsonSerializer.Serialize(ctx, ContextJson);
                    var result = await runner.RunAsync(new ClaudeRunRequest
                    {
                        UserPrompt = contextJson,
                        SystemPrompt = skill.Body,
                        Model = options.Model,
                        MaxBudgetUsd = options.MaxBudgetUsd,
                        Foundry = foundryEnv,
                        UseFoundry = options.UseFoundry,
                    }, innerCt);

                    if (!result.Success || string.IsNullOrWhiteSpace(result.ResponseText))
                    {
                        Interlocked.Increment(ref failed);
                        // We may have spent tokens even on a failed extract — fold them
                        // into the aggregate so live cost stays accurate.
                        AddTokens(result);
                        var reason = result.ErrorKind ?? (string.IsNullOrWhiteSpace(result.Stderr)
                            ? $"exit={result.ExitCode}"
                            : result.Stderr.Trim().Split('\n')[0]);
                        progress.Finished(new ExtractFinishedInfo(
                            item.SessionId, false, result.Model,
                            result.InputTokens, result.OutputTokens,
                            result.CacheReadInputTokens, result.CacheCreationInputTokens,
                            result.CostUsd, result.Duration, reason));
                        return;
                    }

                    var outPath = Path.Combine(extractsDir, item.SessionId + ".md");
                    await File.WriteAllTextAsync(outPath, result.ResponseText, innerCt);

                    // Sidecar metadata: who/when/how-much per extract. Lets `pks brain
                    // status` aggregate and lets a future feature detect skill changes
                    // (via skillHash) and re-extract automatically.
                    var meta = new ExtractMetadata
                    {
                        SessionId = item.SessionId,
                        ExtractedAtUtc = DateTime.UtcNow,
                        Model = result.Model,
                        SkillHash = skillHash,
                        SkillSource = skill.Source,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                        CacheReadInputTokens = result.CacheReadInputTokens,
                        CacheCreationInputTokens = result.CacheCreationInputTokens,
                        CostUsd = result.CostUsd,
                        DurationMs = (long)result.Duration.TotalMilliseconds,
                    };
                    var metaPath = Path.Combine(extractsDir, item.SessionId + ".meta.json");
                    await File.WriteAllTextAsync(metaPath,
                        JsonSerializer.Serialize(meta, ContextJson), innerCt);

                    Interlocked.Increment(ref extracted);
                    extractedIds.Add(item.SessionId);
                    AddTokens(result);
                    progress.Finished(new ExtractFinishedInfo(
                        item.SessionId, true, result.Model,
                        result.InputTokens, result.OutputTokens,
                        result.CacheReadInputTokens, result.CacheCreationInputTokens,
                        result.CostUsd, result.Duration, null));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    progress.Finished(new ExtractFinishedInfo(
                        item.SessionId, false, null, 0, 0, 0, 0, 0, TimeSpan.Zero, ex.Message));
                }
            });

        run.Extracted = extracted;
        run.Failed = failed;
        run.ExtractedSessions = extractedIds.ToList();
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

    // ── enumeration + cost estimate ───────────────────────────────────────────

    private Task<(string Slug, string ExtractsDir, List<(string SessionId, FileInfo MetaFile)> Eligible, int Skipped)>
        EnumerateAsync(BrainExtractOptions options, CancellationToken ct)
    {
        var slug = options.ProjectSlug ?? _paths.EncodeSlug(_paths.Normalize(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory());
        var sessionsDir = Path.Combine(_paths.GlobalProjectDir(slug), "sessions");

        var projectRoot = _paths.ResolveProjectRoot(Directory.GetCurrentDirectory())
            ?? throw new InvalidOperationException(
                "pks brain extract must be run from inside a git repository — outputs go to ./.pks/brain/extracts/.");
        var extractsDir = Path.Combine(projectRoot, "extracts");
        Directory.CreateDirectory(extractsDir);

        var eligible = new List<(string SessionId, FileInfo MetaFile)>();
        var skipped = 0;
        if (Directory.Exists(sessionsDir))
        {
            foreach (var path in Directory.EnumerateFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                var sessionId = Path.GetFileNameWithoutExtension(path);
                var info = new FileInfo(path);
                if (options.SinceUtc is { } since && info.LastWriteTimeUtc < since) continue;
                var outFile = Path.Combine(extractsDir, sessionId + ".md");
                if (!options.Force && File.Exists(outFile) && File.GetLastWriteTimeUtc(outFile) >= info.LastWriteTimeUtc)
                {
                    skipped++;
                    continue;
                }
                eligible.Add((sessionId, info));
            }
        }
        eligible = eligible.OrderByDescending(x => x.MetaFile.LastWriteTimeUtc).ToList();
        if (options.Limit is { } lim && eligible.Count > lim)
            eligible = eligible.Take(lim).ToList();
        return Task.FromResult((slug, extractsDir, eligible, skipped));
    }

    /// Look at past sidecars in extractsDir; if we have ≥3 prior extracts for the
    /// same model, use their average. Else fall back to a hard-coded per-model
    /// heuristic. Returns the estimated total cost + a one-line explanation.
    private static (double TotalUsd, string Basis) EstimateCost(int eligible, string? model, string extractsDir)
    {
        if (eligible == 0) return (0, "no sessions eligible");

        var resolvedModel = model ?? "haiku";
        var modelKey = resolvedModel.ToLowerInvariant();

        // Try to find prior sidecars with this model.
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
                    if (meta.Model is not null && !meta.Model.Contains(modelKey, StringComparison.OrdinalIgnoreCase)) continue;
                    if (meta.CostUsd > 0) priorCosts.Add(meta.CostUsd);
                }
                catch { /* skip */ }
            }
        }

        if (priorCosts.Count >= 3)
        {
            var avg = priorCosts.Average();
            return (avg * eligible, $"avg ${avg:0.####}/call from {priorCosts.Count} prior {resolvedModel} extract(s)");
        }

        // Heuristic per model — broad upper bounds, conservative on purpose.
        var perCall = modelKey switch
        {
            var s when s.Contains("haiku")  => 0.05,
            var s when s.Contains("sonnet") => 0.20,
            var s when s.Contains("opus")   => 1.00,
            _ => 0.20,
        };
        return (perCall * eligible, $"heuristic ${perCall:0.##}/call for {resolvedModel} (no prior runs)");
    }

    private static string ShortHash(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// Lock-free Interlocked.Add for double (BCL only ships int/long).
    private static double InterlockedAdd(ref double target, double value)
    {
        double current;
        double newValue;
        do
        {
            current = target;
            newValue = current + value;
        } while (Interlocked.CompareExchange(ref target, newValue, current) != current);
        return newValue;
    }
}
