using System;
using System.IO;

namespace PKS.Infrastructure.Services.Agent.Tools;

public static class PathSandbox
{
    public static string Resolve(string cwd, string requested)
    {
        if (string.IsNullOrEmpty(cwd)) throw new ArgumentException("cwd required", nameof(cwd));
        if (requested is null) throw new ArgumentNullException(nameof(requested));

        var cwdFull = Path.GetFullPath(cwd);
        var combined = Path.IsPathRooted(requested) ? requested : Path.Combine(cwdFull, requested);
        var full = Path.GetFullPath(combined);

        var prefix = cwdFull.EndsWith(Path.DirectorySeparatorChar)
            ? cwdFull
            : cwdFull + Path.DirectorySeparatorChar;

        if (full != cwdFull && !full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("path escapes sandbox");
        }
        return full;
    }
}
