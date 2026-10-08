namespace PKS.Infrastructure.Services;

/// <summary>
/// Decides, before a remote directory is listed, whether anything under it can survive the
/// <c>--include</c>/<c>--exclude</c> globs. Listing is the expensive part of a sync — one REST round
/// trip per directory — so a tree the globs rule out must never be walked, only to have every file
/// in it filtered afterwards.
///
/// The answer is conservative: it says "skip" only when no file below the directory can match, so
/// the file-level matcher stays the one that decides what is transferred. Anything this class does
/// not understand (brace or bracket syntax) is treated as "might match" and the directory is walked.
/// </summary>
public sealed class SyncTreePruner
{
    private readonly string[][] _includes;
    private readonly string[][] _excludedTrees;

    public SyncTreePruner(IEnumerable<string> include, IEnumerable<string> exclude)
    {
        _includes = include.Select(Split).ToArray();

        // Only an exclude ending in `/**` removes a whole tree; `**/subscription.json` or
        // `slots/*` leave sibling files and deeper levels alive, so they stay file-level filters.
        _excludedTrees = exclude
            .Select(Split)
            .Where(p => p.Length >= 2 && p[^1] == "**")
            .Select(p => p[..^1])
            .ToArray();
    }

    /// <summary>True when the globs can neither rule a directory in nor out — nothing to prune.</summary>
    public bool IsEmpty => _includes.Length == 0 && _excludedTrees.Length == 0;

    /// <param name="relativeDirectory">Share-relative path, '/'-separated, no leading slash.</param>
    public bool ShouldDescend(string relativeDirectory)
    {
        var dir = Split(relativeDirectory);
        if (dir.Length == 0)
            return true;

        if (_excludedTrees.Any(p => MatchesWhole(p, 0, dir, 0)))
            return false;

        return _includes.Length == 0 || _includes.Any(p => CanMatchBelow(p, 0, dir, 0));
    }

    private static string[] Split(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether some file path starting with <paramref name="dir"/> could match the pattern —
    /// i.e. the directory is a prefix of a path the pattern accepts.
    /// </summary>
    private static bool CanMatchBelow(string[] pattern, int pi, string[] dir, int di)
    {
        if (di == dir.Length)
            return pi < pattern.Length; // a file still has to come after the directory

        if (pi == pattern.Length)
            return false;

        if (pattern[pi] == "**")
            return true; // `**` absorbs the rest of the directory and anything below it

        return SegmentMatches(pattern[pi], dir[di]) && CanMatchBelow(pattern, pi + 1, dir, di + 1);
    }

    /// <summary>Whether the pattern matches the whole directory path, `**` spanning zero or more segments.</summary>
    private static bool MatchesWhole(string[] pattern, int pi, string[] dir, int di)
    {
        if (pi == pattern.Length)
            return di == dir.Length;

        if (pattern[pi] == "**")
        {
            for (var skip = di; skip <= dir.Length; skip++)
                if (MatchesWhole(pattern, pi + 1, dir, skip))
                    return true;
            return false;
        }

        return di < dir.Length
            && SegmentMatches(pattern[pi], dir[di], forExclude: true)
            && MatchesWhole(pattern, pi + 1, dir, di + 1);
    }

    /// <summary>
    /// One path segment against one glob segment (`*` and `?`), case-insensitively like the file
    /// matcher. Unsupported syntax answers "match" for includes (walk it) and "no match" for
    /// excludes (do not prune it) — both err towards walking.
    /// </summary>
    private static bool SegmentMatches(string glob, string segment, bool forExclude = false)
    {
        if (glob.IndexOfAny(['[', ']', '{', '}']) >= 0)
            return !forExclude;

        return Wildcard(glob.AsSpan(), segment.AsSpan());
    }

    private static bool Wildcard(ReadOnlySpan<char> glob, ReadOnlySpan<char> text)
    {
        int g = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (g < glob.Length && (glob[g] == '?' || char.ToUpperInvariant(glob[g]) == char.ToUpperInvariant(text[t])))
            {
                g++;
                t++;
            }
            else if (g < glob.Length && glob[g] == '*')
            {
                star = g++;
                mark = t;
            }
            else if (star >= 0)
            {
                g = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (g < glob.Length && glob[g] == '*')
            g++;
        return g == glob.Length;
    }
}
