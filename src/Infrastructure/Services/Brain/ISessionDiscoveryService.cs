namespace PKS.Infrastructure.Services.Brain;

/// Walks ~/.claude/projects/*/ and yields the (slug, jsonl-path) pairs the ingest
/// pipeline should process. Honors the same fallback (~/.config/claude/projects/)
/// as ClaudeUsageCommand.cs:31-46.
public interface ISessionDiscoveryService
{
    IEnumerable<DiscoveredSession> Enumerate(string? projectFilter = null);
}

public sealed record DiscoveredSession(string ProjectSlug, string JsonlPath);

public sealed class SessionDiscoveryService : ISessionDiscoveryService
{
    private readonly IBrainPathResolver _paths;

    public SessionDiscoveryService(IBrainPathResolver paths)
    {
        _paths = paths;
    }

    public IEnumerable<DiscoveredSession> Enumerate(string? projectFilter = null)
    {
        var root = _paths.ClaudeProjectsRoot;
        if (!Directory.Exists(root)) yield break;

        foreach (var projDir in Directory.EnumerateDirectories(root))
        {
            var slug = Path.GetFileName(projDir);
            if (string.IsNullOrEmpty(slug)) continue;
            if (projectFilter is { Length: > 0 } &&
                !slug.Contains(projectFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            // Recurse: subagent transcripts live under <projDir>/<sessionId>/subagents/*.jsonl.
            // Matches ClaudeUsageCommand's AllDirectories scan so brain ingests the same set.
            foreach (var jsonl in Directory.EnumerateFiles(projDir, "*.jsonl", SearchOption.AllDirectories))
            {
                yield return new DiscoveredSession(slug, jsonl);
            }
        }
    }
}
