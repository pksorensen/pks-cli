namespace PKS.Infrastructure;

/// <summary>
/// Explains the two argv shapes that make Spectre fail with a bare "Invalid long option name":
/// a flag and its value glued into one token (zsh does not word-split an unquoted <c>$var</c>,
/// so <c>pks storage sync $flags</c> passes <c>"--dry-run --include x"</c> as one argument), and
/// a flag typed with a Unicode dash pasted from rendered markdown. Diagnostic only — the args
/// are never rewritten, so Spectre still rejects them with its usual error and exit code.
/// </summary>
public static class ArgvHints
{
    private static readonly char[] UnicodeDashes =
        ['‐', '‑', '‒', '–', '—', '―', '−'];

    public static IReadOnlyList<string> Find(IEnumerable<string> args)
    {
        var hints = new List<string>();
        foreach (var arg in args)
        {
            // Everything after a bare `--` is passed through untouched, so it is not ours to judge.
            if (arg == "--")
                break;

            if (arg.Length > 0 && UnicodeDashes.Contains(arg[0]))
            {
                hints.Add($"'{arg}' starts with a Unicode dash, not '-'. Retype the flag with plain hyphens.");
                continue;
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            // The option name ends at '=' or ':'; whitespace after that belongs to the value.
            var nameEnd = arg.IndexOfAny(['=', ':']);
            var name = nameEnd < 0 ? arg : arg[..nameEnd];

            if (name.Any(char.IsWhiteSpace))
                hints.Add($"'{arg}' is one argument holding a flag and its value. Pass them as separate " +
                          "arguments — zsh does not word-split an unquoted $var; use an array or ${=var}.");
            else if (name.IndexOfAny(UnicodeDashes) >= 0)
                hints.Add($"'{arg}' contains a Unicode dash. Retype the flag with plain hyphens.");
        }
        return hints;
    }
}
