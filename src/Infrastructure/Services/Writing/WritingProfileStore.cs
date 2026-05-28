using System.Text.Json;
using PKS.Infrastructure.Services.Writing.Models;

namespace PKS.Infrastructure.Services.Writing;

public sealed class WritingProfileStore : IWritingProfileStore
{
    private readonly IWritingPathResolver _paths;
    private readonly SemaphoreSlim _anglicismLock = new(1, 1);
    private readonly SemaphoreSlim _allowlistLock = new(1, 1);
    private readonly SemaphoreSlim _calquesLock = new(1, 1);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WritingProfileStore(IWritingPathResolver paths)
    {
        _paths = paths;
    }

    public async Task EnsureGlobalLayoutAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.GlobalRoot);
        Directory.CreateDirectory(_paths.GlobalChannelsDir);
        Directory.CreateDirectory(_paths.GlobalValeDir);
        Directory.CreateDirectory(_paths.GlobalValeBinDir);
        Directory.CreateDirectory(_paths.GlobalValeStylesDir);
        Directory.CreateDirectory(_paths.GlobalReferenceDir);
        Directory.CreateDirectory(_paths.GlobalReferenceChannelDir("blog"));
        Directory.CreateDirectory(_paths.GlobalReferenceChannelDir("linkedin"));
        Directory.CreateDirectory(_paths.GlobalReferenceChannelDir("adr"));

        await SeedIfMissingAsync(_paths.GlobalProfilePath, DefaultSeeds.Profile, ct);
        await SeedIfMissingAsync(_paths.GlobalAnglicismsPath, DefaultSeeds.Anglicisms, ct);
        await SeedIfMissingAsync(_paths.GlobalCalquesPath, DefaultSeeds.Calques, ct);
        await SeedIfMissingAsync(_paths.GlobalAllowlistPath, DefaultSeeds.Allowlist, ct);
        await SeedIfMissingAsync(_paths.GlobalChannelRubricPath("blog"), DefaultSeeds.BlogRubric, ct);
        await SeedIfMissingAsync(_paths.GlobalChannelRubricPath("linkedin"), DefaultSeeds.LinkedInRubric, ct);
        await SeedIfMissingAsync(_paths.GlobalChannelRubricPath("adr"), DefaultSeeds.AdrRubric, ct);
        await SeedIfMissingAsync(_paths.GlobalValeConfigPath, DefaultSeeds.ValeIni, ct);
        await SeedIfMissingAsync(Path.Combine(_paths.GlobalReferenceDir, "README.md"), DefaultSeeds.ReferenceReadme, ct);
        await SeedIfMissingAsync(_paths.GlobalAuthoringPromptPath, DefaultSeeds.AuthoringPrompt, ct);
    }

    public async Task EnsureProjectLayoutAsync(string? projectRoot, CancellationToken ct = default)
    {
        if (projectRoot is null) return;
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(Path.Combine(projectRoot, "overrides"));
        Directory.CreateDirectory(_paths.ProjectReportsDir(projectRoot));

        var channelPath = _paths.ProjectChannelConfigPath(projectRoot);
        if (!File.Exists(channelPath))
        {
            var json = JsonSerializer.Serialize(new ChannelConfig(), JsonOptions);
            await File.WriteAllTextAsync(channelPath, json + "\n", ct);
        }

        await EnsureGitignoreAsync(projectRoot, ct);
    }

    public async Task<string?> LoadProfileAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.GlobalProfilePath)) return null;
        return await File.ReadAllTextAsync(_paths.GlobalProfilePath, ct);
    }

    public async Task<IReadOnlyList<AnglicismEntry>> LoadAnglicismsAsync(
        string? projectRoot, CancellationToken ct = default)
    {
        var merged = new Dictionary<string, AnglicismEntry>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_paths.GlobalAnglicismsPath))
        {
            var globalText = await File.ReadAllTextAsync(_paths.GlobalAnglicismsPath, ct);
            foreach (var e in AnglicismListParser.Parse(globalText))
                merged[e.English] = e;
        }

        if (projectRoot is not null)
        {
            var overridePath = _paths.ProjectOverridesAnglicismsPath(projectRoot);
            if (File.Exists(overridePath))
            {
                var text = await File.ReadAllTextAsync(overridePath, ct);
                foreach (var e in AnglicismListParser.Parse(text))
                    merged[e.English] = e; // project wins
            }
        }

        return merged.Values.OrderBy(e => e.English, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<CalqueEntry>> LoadCalquesAsync(
        string? projectRoot, CancellationToken ct = default)
    {
        // Same merge semantics as anglicisms: project overrides win on collision.
        var merged = new Dictionary<string, CalqueEntry>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_paths.GlobalCalquesPath))
        {
            var text = await File.ReadAllTextAsync(_paths.GlobalCalquesPath, ct);
            foreach (var e in AnglicismListParser.ParseCalques(text))
                merged[e.LiteralDanish] = e;
        }

        if (projectRoot is not null)
        {
            var overridePath = Path.Combine(projectRoot, "overrides", "calques.txt");
            if (File.Exists(overridePath))
            {
                var text = await File.ReadAllTextAsync(overridePath, ct);
                foreach (var e in AnglicismListParser.ParseCalques(text))
                    merged[e.LiteralDanish] = e;
            }
        }

        return merged.Values.OrderBy(e => e.LiteralDanish, StringComparer.Ordinal).ToList();
    }

    public async Task AddCalqueAsync(CalqueEntry entry, CancellationToken ct = default)
    {
        await _calquesLock.WaitAsync(ct);
        try
        {
            var existing = File.Exists(_paths.GlobalCalquesPath)
                ? AnglicismListParser.ParseCalques(await File.ReadAllTextAsync(_paths.GlobalCalquesPath, ct))
                : new List<CalqueEntry>();

            var map = existing.ToDictionary(e => e.LiteralDanish, e => e, StringComparer.OrdinalIgnoreCase);
            map[entry.LiteralDanish] = entry;

            await AtomicWriteAsync(_paths.GlobalCalquesPath,
                AnglicismListParser.RenderCalques(map.Values), ct);
        }
        finally
        {
            _calquesLock.Release();
        }
    }

    public async Task<IReadOnlySet<string>> LoadAllowlistAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_paths.GlobalAllowlistPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var text = await File.ReadAllTextAsync(_paths.GlobalAllowlistPath, ct);
        return new HashSet<string>(AnglicismListParser.ParseAllowlist(text), StringComparer.OrdinalIgnoreCase);
    }

    public async Task AddAnglicismAsync(AnglicismEntry entry, CancellationToken ct = default)
    {
        await _anglicismLock.WaitAsync(ct);
        try
        {
            var existing = File.Exists(_paths.GlobalAnglicismsPath)
                ? AnglicismListParser.Parse(await File.ReadAllTextAsync(_paths.GlobalAnglicismsPath, ct))
                : new List<AnglicismEntry>();

            var map = existing.ToDictionary(e => e.English, e => e, StringComparer.OrdinalIgnoreCase);
            map[entry.English] = entry;

            await AtomicWriteAsync(_paths.GlobalAnglicismsPath,
                AnglicismListParser.Render(map.Values), ct);
        }
        finally
        {
            _anglicismLock.Release();
        }
    }

    public async Task AddAllowedTermAsync(string term, CancellationToken ct = default)
    {
        await _allowlistLock.WaitAsync(ct);
        try
        {
            var terms = File.Exists(_paths.GlobalAllowlistPath)
                ? AnglicismListParser.ParseAllowlist(await File.ReadAllTextAsync(_paths.GlobalAllowlistPath, ct))
                : new List<string>();

            var set = new HashSet<string>(terms, StringComparer.OrdinalIgnoreCase) { term };
            await AtomicWriteAsync(_paths.GlobalAllowlistPath,
                AnglicismListParser.RenderAllowlist(set), ct);
        }
        finally
        {
            _allowlistLock.Release();
        }
    }

    public async Task AppendLessonAsync(string dimension, string lesson, string sourcePath, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_paths.GlobalRoot);
        if (!File.Exists(_paths.GlobalLessonsPath))
        {
            await File.WriteAllTextAsync(_paths.GlobalLessonsPath,
                "# Writing lessons\n\nAppended by `pks writing learn`. One bullet per accepted lesson.\n\n",
                ct);
        }
        var rel = Path.GetFileName(sourcePath);
        var line = $"- **[{dimension}]** _{DateTime.UtcNow:yyyy-MM-dd}_ ({rel}): {lesson.Trim()}\n";
        await File.AppendAllTextAsync(_paths.GlobalLessonsPath, line, ct);
    }

    public async Task<ChannelConfig> LoadChannelConfigAsync(
        string? projectRoot, CancellationToken ct = default)
    {
        if (projectRoot is null) return new ChannelConfig();
        var path = _paths.ProjectChannelConfigPath(projectRoot);
        if (!File.Exists(path)) return new ChannelConfig();
        var json = await File.ReadAllTextAsync(path, ct);
        try
        {
            return JsonSerializer.Deserialize<ChannelConfig>(json, JsonOptions) ?? new ChannelConfig();
        }
        catch (JsonException)
        {
            return new ChannelConfig();
        }
    }

    public async Task<string?> LoadChannelRubricAsync(string channel, CancellationToken ct = default)
    {
        var path = _paths.GlobalChannelRubricPath(channel);
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public async Task<IReadOnlyList<ReferenceSample>> LoadReferenceSamplesAsync(
        string channel, CancellationToken ct = default)
    {
        var dir = _paths.GlobalReferenceChannelDir(channel);
        if (!Directory.Exists(dir)) return Array.Empty<ReferenceSample>();

        var samples = new List<ReferenceSample>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                     .Where(p => !Path.GetFileName(p).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            samples.Add(new ReferenceSample
            {
                Id = Path.GetFileNameWithoutExtension(file),
                Content = await File.ReadAllTextAsync(file, ct),
            });
        }
        return samples;
    }

    public async Task<WritingReport?> LoadReportAsync(
        string sourceFilePath, CancellationToken ct = default)
    {
        var sidecar = _paths.ReportSidecarJsonPath(sourceFilePath);
        if (!File.Exists(sidecar)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(sidecar, ct);
            return JsonSerializer.Deserialize<WritingReport>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveReportAsync(
        string sourceFilePath, WritingReport report, CancellationToken ct = default)
    {
        report.GeneratedUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(report, JsonOptions);

        // Sidecar JSON + Markdown next to the source.
        await AtomicWriteAsync(_paths.ReportSidecarJsonPath(sourceFilePath), json, ct);
        await AtomicWriteAsync(_paths.ReportSidecarMarkdownPath(sourceFilePath),
            WritingReportRenderer.RenderMarkdown(report), ct);

        // Cache copy under the project layer (if cwd happens to be in a repo).
        var projectRoot = _paths.ResolveProjectRoot(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!);
        if (projectRoot is not null)
        {
            Directory.CreateDirectory(_paths.ProjectReportsDir(projectRoot));
            await AtomicWriteAsync(_paths.ProjectReportCachePath(projectRoot, sourceFilePath), json, ct);
        }
    }

    public Task DeleteReportSidecarsAsync(string sourceFilePath, CancellationToken ct = default)
    {
        TryDelete(_paths.ReportSidecarMarkdownPath(sourceFilePath));
        TryDelete(_paths.ReportSidecarJsonPath(sourceFilePath));
        TryDelete(_paths.LearnSidecarMarkdownPath(sourceFilePath));
        TryDelete(_paths.LearnSidecarJsonPath(sourceFilePath));

        // Also drop the project cache entry if present.
        var projectRoot = _paths.ResolveProjectRoot(
            Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))!);
        if (projectRoot is not null)
            TryDelete(_paths.ProjectReportCachePath(projectRoot, sourceFilePath));

        // Tidy up an empty _review dir so the post folder stays clean.
        var reviewDir = _paths.ReviewDir(sourceFilePath);
        if (Directory.Exists(reviewDir) && !Directory.EnumerateFileSystemEntries(reviewDir).Any())
        {
            try { Directory.Delete(reviewDir); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;

        static void TryDelete(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static async Task SeedIfMissingAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, ct);
    }

    private static async Task AtomicWriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task EnsureGitignoreAsync(string writingProjectRoot, CancellationToken ct)
    {
        // writingProjectRoot ends in ".pks/writing". Walk up two levels to find the repo root.
        var pksDir = Path.GetDirectoryName(writingProjectRoot);
        if (pksDir is null) return;
        var repoRoot = Path.GetDirectoryName(pksDir);
        if (repoRoot is null) return;

        var gitignore = Path.Combine(repoRoot, ".gitignore");
        const string entry = ".pks/writing/";

        string existing = File.Exists(gitignore)
            ? await File.ReadAllTextAsync(gitignore, ct)
            : string.Empty;

        var hasEntry = existing
            .Split('\n')
            .Any(line => line.Trim().Equals(entry, StringComparison.Ordinal) ||
                         line.Trim().Equals(".pks/writing", StringComparison.Ordinal));
        var hasReviewEntry = existing
            .Split('\n')
            .Any(line => line.Trim().Equals("_review/", StringComparison.Ordinal) ||
                         line.Trim().Equals("_review", StringComparison.Ordinal) ||
                         line.Trim().Equals("**/_review/", StringComparison.Ordinal));

        if (hasEntry && hasReviewEntry) return;

        var sb = new System.Text.StringBuilder();
        sb.Append(existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n");
        sb.AppendLine("# pks writing — local report cache + overrides (see `pks writing init`)");
        if (!hasEntry)        sb.AppendLine(entry);
        if (!hasReviewEntry)  sb.AppendLine("**/_review/");
        await File.AppendAllTextAsync(gitignore, sb.ToString(), ct);
    }
}
