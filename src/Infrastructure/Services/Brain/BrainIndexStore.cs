using System.Text;
using System.Text.Json;
using PKS.Infrastructure.Services.Brain.Models;

namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainIndexStore : IBrainIndexStore
{
    private readonly IBrainPathResolver _paths;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _ingestLogLock = new(1, 1);
    private readonly SemaphoreSlim _planIndexLock = new(1, 1);
    private readonly Dictionary<BrainFirehose, SemaphoreSlim> _firehoseLocks = new()
    {
        [BrainFirehose.Prompts] = new(1, 1),
        [BrainFirehose.Tools]   = new(1, 1),
        [BrainFirehose.Files]   = new(1, 1),
        [BrainFirehose.Errors]  = new(1, 1),
    };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// Compact JSON for firehose lines — one row per line, no indentation,
    /// camelCase to match the read-side TS conventions in www-site.
    public static readonly JsonSerializerOptions FirehoseJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public BrainIndexStore(IBrainPathResolver paths)
    {
        _paths = paths;
    }

    public async Task EnsureGlobalLayoutAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.GlobalRoot);
        Directory.CreateDirectory(Path.Combine(_paths.GlobalRoot, "projects"));
        Directory.CreateDirectory(Path.Combine(_paths.GlobalRoot, "meta"));

        foreach (BrainFirehose firehose in Enum.GetValues<BrainFirehose>())
        {
            var path = _paths.GlobalFirehose(firehose);
            if (!File.Exists(path))
            {
                await using var _ = File.Create(path);
            }
        }

        if (!File.Exists(_paths.GlobalIndexPath))
        {
            await SaveIndexAsync(new BrainIndex(), ct);
        }

        if (!File.Exists(_paths.GlobalIngestRunsPath))
        {
            var json = JsonSerializer.Serialize(new IngestRunLog(), JsonOptions);
            await File.WriteAllTextAsync(_paths.GlobalIngestRunsPath, json, ct);
        }
    }

    public async Task EnsureProjectLayoutAsync(string? projectRoot, CancellationToken ct = default)
    {
        if (projectRoot is null) return;

        Directory.CreateDirectory(projectRoot);
        foreach (var sub in new[] { "extracts", "synthesis", "wiki", "adr", "feature-specs" })
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, sub));
        }

        var refsPath = Path.Combine(projectRoot, "refs.json");
        if (!File.Exists(refsPath))
        {
            await File.WriteAllTextAsync(refsPath, "{\n  \"version\": 1\n}\n", ct);
        }

        await EnsureGitignoreAsync(projectRoot, ct);
    }

    public async Task<BrainIndex> LoadIndexAsync(CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_paths.GlobalIndexPath))
                return new BrainIndex();

            var json = await File.ReadAllTextAsync(_paths.GlobalIndexPath, ct);
            try
            {
                return JsonSerializer.Deserialize<BrainIndex>(json, JsonOptions) ?? new BrainIndex();
            }
            catch (JsonException)
            {
                return new BrainIndex();
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task SaveIndexAsync(BrainIndex index, CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            index.UpdatedAt = DateTime.UtcNow;
            Directory.CreateDirectory(_paths.GlobalRoot);
            var json = JsonSerializer.Serialize(index, JsonOptions);
            var tmp = _paths.GlobalIndexPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            // Atomic rename — clobbers existing destination on .NET 10/Linux.
            File.Move(tmp, _paths.GlobalIndexPath, overwrite: true);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    public async Task AppendFirehoseAsync<T>(BrainFirehose firehose, IReadOnlyList<T> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        var path = _paths.GlobalFirehose(firehose);

        var sb = new StringBuilder(rows.Count * 256);
        foreach (var row in rows)
        {
            sb.Append(JsonSerializer.Serialize(row, FirehoseJsonOptions));
            sb.Append('\n');
        }

        var lock_ = _firehoseLocks[firehose];
        await lock_.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, sb.ToString(), ct);
        }
        finally
        {
            lock_.Release();
        }
    }

    public async Task WriteSessionMetadataAsync(SessionMetadata metadata, CancellationToken ct = default)
    {
        var path = _paths.GlobalSessionFile(metadata.ProjectSlug, metadata.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task WriteProjectRollupAsync(ProjectRollup rollup, CancellationToken ct = default)
    {
        var dir = _paths.GlobalProjectDir(rollup.Slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "project.json");
        var json = JsonSerializer.Serialize(rollup, JsonOptions);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public async Task<ProjectRollup> BuildProjectRollupFromDiskAsync(string slug, CancellationToken ct = default)
    {
        var sessionsDir = Path.Combine(_paths.GlobalProjectDir(slug), "sessions");
        var rollup = new ProjectRollup { Slug = slug };
        if (!Directory.Exists(sessionsDir)) return rollup;

        var cwdCounter = new Dictionary<string, int>(StringComparer.Ordinal);
        var branches = new HashSet<string>(StringComparer.Ordinal);
        var subagents = new HashSet<string>(StringComparer.Ordinal);
        var skills = new HashSet<string>(StringComparer.Ordinal);
        var tools = new Dictionary<string, long>(StringComparer.Ordinal);
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var errors = new Dictionary<string, long>(StringComparer.Ordinal);
        var tokens = new Dictionary<string, ModelTokenTotals>(StringComparer.Ordinal);
        double cost = 0;
        DateTime? first = null, last = null;
        int count = 0;
        string? realCwd = null;

        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            SessionMetadata? meta;
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                meta = JsonSerializer.Deserialize<SessionMetadata>(json, JsonOptions);
            }
            catch (JsonException) { continue; }
            if (meta is null) continue;

            count++;
            if (meta.Cwd is { Length: > 0 } c)
            {
                cwdCounter[c] = cwdCounter.GetValueOrDefault(c) + 1;
                realCwd ??= meta.RealCwd;
            }
            if (meta.FirstTimestampUtc is { } f && (first is null || f < first)) first = f;
            if (meta.LastTimestampUtc is { } l && (last is null || l > last)) last = l;
            foreach (var b in meta.GitBranches) branches.Add(b);
            foreach (var s in meta.Subagents) subagents.Add(s);
            foreach (var s in meta.Skills) skills.Add(s);
            foreach (var t in meta.TopTools) tools[t.Name] = tools.GetValueOrDefault(t.Name) + t.Count;
            foreach (var f2 in meta.TopFiles) files[f2.Name] = files.GetValueOrDefault(f2.Name) + f2.Count;
            foreach (var e in meta.TopErrors) errors[e.Name] = errors.GetValueOrDefault(e.Name) + e.Count;
            foreach (var tm in meta.TokensByModel)
            {
                if (!tokens.TryGetValue(tm.Model, out var bucket))
                {
                    bucket = new ModelTokenTotals { Model = tm.Model };
                    tokens[tm.Model] = bucket;
                }
                bucket.InputTokens += tm.InputTokens;
                bucket.OutputTokens += tm.OutputTokens;
                bucket.CacheReadInputTokens += tm.CacheReadInputTokens;
                bucket.CacheCreationInputTokens += tm.CacheCreationInputTokens;
                bucket.EstimatedCostUsd += tm.EstimatedCostUsd;
            }
            cost += meta.EstimatedCostUsd;
        }

        rollup.Cwd = cwdCounter.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        rollup.RealCwd = realCwd ?? _paths.Normalize(rollup.Cwd);
        rollup.SessionCount = count;
        rollup.FirstSessionUtc = first;
        rollup.LastSessionUtc = last;
        rollup.Branches = branches.OrderBy(s => s, StringComparer.Ordinal).ToList();
        rollup.Subagents = subagents.OrderBy(s => s, StringComparer.Ordinal).ToList();
        rollup.Skills = skills.OrderBy(s => s, StringComparer.Ordinal).ToList();
        rollup.TopTools = TopN(tools, 20);
        rollup.TopFiles = TopN(files, 20);
        rollup.TopErrors = TopN(errors, 20);
        rollup.TokensByModel = tokens.Values.OrderBy(m => m.Model, StringComparer.Ordinal).ToList();
        rollup.EstimatedCostUsd = cost;
        rollup.Kind = "unknown";
        return rollup;
    }

    private static List<TopName> TopN(Dictionary<string, long> dict, int n) =>
        dict.OrderByDescending(kv => kv.Value)
            .Take(n)
            .Select(kv => new TopName { Name = kv.Key, Count = kv.Value })
            .ToList();

    public async Task<IngestRunLog> LoadIngestRunLogAsync(CancellationToken ct = default)
    {
        await _ingestLogLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_paths.GlobalIngestRunsPath)) return new IngestRunLog();
            var json = await File.ReadAllTextAsync(_paths.GlobalIngestRunsPath, ct);
            try
            {
                return JsonSerializer.Deserialize<IngestRunLog>(json, JsonOptions) ?? new IngestRunLog();
            }
            catch (JsonException)
            {
                return new IngestRunLog();
            }
        }
        finally
        {
            _ingestLogLock.Release();
        }
    }

    public async Task SaveIngestRunLogAsync(IngestRunLog log, CancellationToken ct = default)
    {
        await _ingestLogLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.GlobalIngestRunsPath)!);
            var json = JsonSerializer.Serialize(log, JsonOptions);
            var tmp = _paths.GlobalIngestRunsPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _paths.GlobalIngestRunsPath, overwrite: true);
        }
        finally
        {
            _ingestLogLock.Release();
        }
    }

    public async Task SavePlanIndexAsync(PlanIndex index, CancellationToken ct = default)
    {
        await _planIndexLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_paths.GlobalRoot);
            var json = JsonSerializer.Serialize(index, JsonOptions);
            var tmp = _paths.GlobalPlansIndexPath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _paths.GlobalPlansIndexPath, overwrite: true);
        }
        finally
        {
            _planIndexLock.Release();
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task EnsureGitignoreAsync(string brainProjectRoot, CancellationToken ct)
    {
        // brainProjectRoot ends in ".pks/brain". Walk up two levels to find the repo root.
        var pksDir = Path.GetDirectoryName(brainProjectRoot);            // .pks
        if (pksDir is null) return;
        var repoRoot = Path.GetDirectoryName(pksDir);                    // <repo>
        if (repoRoot is null) return;

        var gitignore = Path.Combine(repoRoot, ".gitignore");
        const string entry = ".pks/brain/";

        string existing = File.Exists(gitignore)
            ? await File.ReadAllTextAsync(gitignore, ct)
            : string.Empty;

        var hasEntry = existing
            .Split('\n')
            .Any(line => line.Trim().Equals(entry, StringComparison.Ordinal) ||
                         line.Trim().Equals(".pks/brain", StringComparison.Ordinal));
        if (hasEntry) return;

        var suffix = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
        var addition = $"{suffix}# pks brain — local raw extracts (see `pks brain init`)\n{entry}\n";
        await File.AppendAllTextAsync(gitignore, addition, ct);
    }
}
