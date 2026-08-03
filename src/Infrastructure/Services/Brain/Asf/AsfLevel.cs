namespace PKS.Infrastructure.Services.Brain.Asf;

/// The three ASF sensitivity levels. Spec: docs/specs/asf/02-levels.md.
///
/// Ordered: Metrics &lt; Prompts &lt; Full. The ordering is load-bearing — the server
/// uses it to decide whether an arriving event enriches a stored one, and upload
/// tokens carry a maximum level that is compared with it.
public static class AsfLevel
{
    /// Everything, secrets masked. CLI: --level all.
    public const string Full = "full";

    /// Human and assistant text; tool names but no arguments, outputs or paths.
    public const string Prompts = "prompts";

    /// Counts, timings, tokens, cost, tool names. No content.
    public const string Metrics = "metrics";

    /// Rank for comparison. Unknown values rank -1 so they never satisfy a
    /// "level is at least X" check.
    public static int Rank(string? level) => level switch
    {
        Metrics => 0,
        Prompts => 1,
        Full => 2,
        _ => -1,
    };

    public static bool IsValid(string? level) => Rank(level) >= 0;

    /// True when `level` is at least as permissive as `minimum`.
    public static bool AtLeast(string? level, string minimum) => Rank(level) >= Rank(minimum);

    /// Accepts the CLI spellings: `all` is the user-facing name of `full`.
    public static string Parse(string? input)
    {
        var value = input?.Trim().ToLowerInvariant();

        return value switch
        {
            "all" or Full => Full,
            "prompts" or "prompts-only" or "promptsonly" => Prompts,
            "metrics" or "stats" or "telemetry" => Metrics,
            null or "" => Metrics,
            _ => throw new ArgumentException(
                $"Unknown ASF level '{input}'. Expected all|prompts|metrics.", nameof(input)),
        };
    }
}
