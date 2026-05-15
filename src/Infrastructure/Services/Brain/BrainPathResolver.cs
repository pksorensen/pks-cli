namespace PKS.Infrastructure.Services.Brain;

public sealed class BrainPathResolver : IBrainPathResolver
{
    private readonly string _home;

    public BrainPathResolver()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// Test seam: inject a fake home directory.
    public BrainPathResolver(string home)
    {
        _home = home;
    }

    public string ClaudeProjectsRoot
    {
        get
        {
            var primary = Path.Combine(_home, ".claude", "projects");
            if (Directory.Exists(primary)) return primary;
            var fallback = Path.Combine(_home, ".config", "claude", "projects");
            return Directory.Exists(fallback) ? fallback : primary;
        }
    }

    public string ClaudePlansRoot => Path.Combine(_home, ".claude", "plans");

    public string GlobalRoot => Path.Combine(_home, ".pks-cli", "brain");

    public string GlobalProjectDir(string slug) =>
        Path.Combine(GlobalRoot, "projects", slug);

    public string GlobalSessionFile(string slug, string sessionId) =>
        Path.Combine(GlobalProjectDir(slug), "sessions", sessionId + ".json");

    public string GlobalFirehose(BrainFirehose firehose) =>
        firehose switch
        {
            BrainFirehose.Prompts => Path.Combine(GlobalRoot, "prompts.jsonl"),
            BrainFirehose.Tools => Path.Combine(GlobalRoot, "tools.jsonl"),
            BrainFirehose.Files => Path.Combine(GlobalRoot, "files.jsonl"),
            BrainFirehose.Errors => Path.Combine(GlobalRoot, "errors.jsonl"),
            _ => throw new ArgumentOutOfRangeException(nameof(firehose), firehose, null),
        };

    public string GlobalIndexPath => Path.Combine(GlobalRoot, "index.json");

    public string GlobalIngestRunsPath =>
        Path.Combine(GlobalRoot, "meta", "ingest-runs.json");

    public string GlobalPlansIndexPath => Path.Combine(GlobalRoot, "plans.json");

    public string? ResolveProjectRoot(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        var normalized = Normalize(cwd);
        if (normalized is null) return null;

        var dir = new DirectoryInfo(normalized);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Path.Combine(dir.FullName, ".pks", "brain");
            }
            dir = dir.Parent;
        }
        return null;
    }

    public string EncodeSlug(string realPath)
    {
        if (string.IsNullOrWhiteSpace(realPath))
            throw new ArgumentException("realPath required", nameof(realPath));

        // Claude Code's convention: replace path separators with '-'. The leading
        // separator becomes a leading '-' as well. Mirrors ClaudeStatsCommand:39-100.
        var s = realPath
            .Replace(Path.DirectorySeparatorChar, '-')
            .Replace('/', '-');
        if (!s.StartsWith('-')) s = "-" + s;
        return s;
    }

    public string DecodeSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("slug required", nameof(slug));
        // Reverse of EncodeSlug — lossy when a path component itself contained '-',
        // but a useful best-effort hint. Callers should verify on disk.
        return "/" + slug.TrimStart('-').Replace('-', '/');
    }

    public string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            // ResolveLinkTarget follows symlinks; GetFullPath canonicalizes the rest.
            var full = Path.GetFullPath(path);
            if (Directory.Exists(full))
            {
                var info = new DirectoryInfo(full);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is DirectoryInfo d) return d.FullName;
            }
            return full;
        }
        catch
        {
            return path;
        }
    }
}
