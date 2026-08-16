namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Why a job container can be stuck on a months-old Claude Code and never notice.
///
/// Claude Code's npm package declares <c>engines.node &gt;= 22</c> from 2.1.198 onward, so npm on a
/// Node 20 image resolves the newest release that still satisfies the engine — <b>2.1.197</b> —
/// and reports success. Measured 2026-08-16: a clean <c>node:20</c> container installs 2.1.197 while
/// <c>npm view … dist-tags.latest</c> says 2.1.233; the same install on <c>node:22</c> gets 2.1.233.
/// So the container is version-pinned by its base image, and <c>claude update</c> cannot move it,
/// no matter which user runs it. That matters beyond hygiene: vibecast's onboarding auto-answers are
/// string-matched against specific claude screens, so a pinned old build is what quietly stops them
/// from firing.
/// </summary>
public static class ClaudeRuntimeCheck
{
    /// <summary>First Node major that can install current Claude Code from npm.</summary>
    public const int MinimumNodeMajor = 22;

    /// <summary>
    /// Parses the major version out of <c>node --version</c> output (<c>v20.19.4</c>), tolerating
    /// surrounding whitespace and extra lines. Returns null when nothing recognizable is there —
    /// treated by callers as "cannot tell", never as "too old".
    /// </summary>
    public static int? ParseNodeMajor(string? nodeVersionOutput)
    {
        if (string.IsNullOrWhiteSpace(nodeVersionOutput)) return null;

        foreach (var rawLine in nodeVersionOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('v')) line = line[1..];

            var digits = new string(line.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits, out var major))
                return major;
        }

        return null;
    }

    /// <summary>
    /// True when this Node major pins npm to an outdated Claude Code. Unknown (null) is false:
    /// a failed probe should not produce a confident warning about the wrong thing.
    /// </summary>
    public static bool PinsClaudeToAnOldRelease(int? nodeMajor) =>
        nodeMajor is int major && major < MinimumNodeMajor;
}
