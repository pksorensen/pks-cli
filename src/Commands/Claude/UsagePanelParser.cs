using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PKS.Commands.Claude;

/// <summary>
/// Kind of a parsed usage-limit block from Claude Code's interactive `/usage` panel.
/// </summary>
public enum LimitKind
{
    Session,
    Week
}

/// <summary>
/// One parsed block from the `/usage` panel: a header ("Current session" / "Current week (...)"),
/// its used-percent bar, and its "Resets ..." line, resolved to an absolute UTC instant.
/// </summary>
/// <param name="Model">Null for "Current session" and for "Current week (all models)"; otherwise the model label (e.g. "Fable").</param>
public record ParsedBlock(
    LimitKind Kind,
    string? Model,
    int UsedPct,
    DateTimeOffset ResetsAt);

/// <summary>
/// A single limit entry ready for JSON serialization / pretty rendering — the parsed
/// usage plus computed pace/burn math relative to <c>now</c>.
/// </summary>
public record LimitEntry(
    [property: JsonPropertyName("usedPct")] int UsedPct,
    [property: JsonPropertyName("resetsAt")] string ResetsAt,
    [property: JsonPropertyName("resetsInSeconds")] long ResetsInSeconds,
    [property: JsonPropertyName("elapsedPct")] double ElapsedPct,
    [property: JsonPropertyName("burnRatio")] double BurnRatio,
    [property: JsonPropertyName("usedAtReset")] double UsedAtReset,
    [property: JsonPropertyName("pace")] string Pace,
    [property: JsonPropertyName("model")] string? Model = null);

/// <summary>
/// The full structured report emitted by `pks claude limits` / `pks claude session-usage`.
/// </summary>
public record LimitsReport(
    [property: JsonPropertyName("capturedAt")] string CapturedAt,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("session")] LimitEntry? Session,
    [property: JsonPropertyName("week")] LimitEntry? Week,
    [property: JsonPropertyName("weekByModel")] IReadOnlyList<LimitEntry> WeekByModel,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Pure, side-effect-free parser for Claude Code's interactive `/usage` panel (as captured
/// verbatim via `tmux capture-pane -p`), plus the reset-time resolver and pace/burn math.
/// No tmux, no process spawning, no I/O — everything here is unit-testable in isolation.
/// </summary>
public static class UsagePanelParser
{
    private const double Eps = 1e-6;

    private static readonly string[] MonthAbbrevs =
    [
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    ];

    private static readonly Regex SessionHeader = new(@"^\s*Current session\s*$", RegexOptions.Compiled);
    private static readonly Regex WeekHeader = new(@"^\s*Current week \((?<label>[^)]+)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex PercentLine = new(@"(?<pct>\d{1,3})%\s+used", RegexOptions.Compiled);
    private static readonly Regex ResetLine = new(@"^\s*Resets\s+(?<when>.+?)\s*\(UTC\)\s*$", RegexOptions.Compiled);

    private static readonly Regex TimeOnly = new(
        @"^(?<h>\d{1,2}):(?<m>\d{2})(?<ap>am|pm)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthDayTime = new(
        @"^(?<mon>[A-Za-z]{3}) (?<day>\d{1,2}), (?<h>\d{1,2}):(?<m>\d{2})(?<ap>am|pm)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse the raw captured `/usage` pane into an ordered list of blocks. An empty list
    /// means parse-failure — the caller should fall back to the `--llm` path.
    /// </summary>
    public static List<ParsedBlock> TryParse(string raw, DateTimeOffset now)
    {
        var blocks = new List<ParsedBlock>();
        var lines = raw.Replace("\r", "").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            LimitKind kind;
            string? model;

            if (SessionHeader.IsMatch(lines[i]))
            {
                kind = LimitKind.Session;
                model = null;
            }
            else
            {
                var weekMatch = WeekHeader.Match(lines[i]);
                if (weekMatch.Success)
                {
                    kind = LimitKind.Week;
                    var label = weekMatch.Groups["label"].Value.Trim();
                    model = label.Equals("all models", StringComparison.OrdinalIgnoreCase) ? null : label;
                }
                else
                {
                    continue;
                }
            }

            int? pct = null;
            DateTimeOffset? reset = null;

            var end = Math.Min(i + 7, lines.Length);
            for (var j = i + 1; j < end; j++)
            {
                if (pct == null)
                {
                    var pctMatch = PercentLine.Match(lines[j]);
                    if (pctMatch.Success)
                    {
                        pct = int.Parse(pctMatch.Groups["pct"].Value, CultureInfo.InvariantCulture);
                    }
                }

                if (reset == null)
                {
                    var resetMatch = ResetLine.Match(lines[j]);
                    if (resetMatch.Success)
                    {
                        var when = resetMatch.Groups["when"].Value;
                        var resolved = ResolveReset(kind, when, now);
                        if (resolved != null)
                        {
                            reset = resolved;
                        }
                    }
                }

                if (pct != null && reset != null)
                {
                    break;
                }

                // Stop early if we hit the next header — malformed/short block.
                if (SessionHeader.IsMatch(lines[j]) || WeekHeader.IsMatch(lines[j]))
                {
                    break;
                }
            }

            if (pct != null && reset != null)
            {
                blocks.Add(new ParsedBlock(kind, model, pct.Value, reset.Value));
            }
        }

        return blocks;
    }

    /// <summary>
    /// Resolve a panel "Resets ..." string (already stripped of the trailing "(UTC)") to an
    /// absolute UTC instant relative to <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset? ResolveReset(LimitKind kind, string? when, DateTimeOffset now)
    {
        // A model-authored --llm sink entry can omit resetsAt, leaving this null; guard before
        // Trim() so one bad per-model entry doesn't NRE and discard the whole (valid) report.
        if (string.IsNullOrWhiteSpace(when)) return null;
        when = when.Trim();

        if (kind == LimitKind.Session)
        {
            var m = TimeOnly.Match(when);
            if (!m.Success)
            {
                return null;
            }

            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            var ap = m.Groups["ap"].Value;
            var h24 = To24h(h, ap);

            var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, h24, min, 0, TimeSpan.Zero);
            if (candidate <= now)
            {
                candidate = candidate.AddDays(1);
            }

            return candidate;
        }
        else
        {
            var m = MonthDayTime.Match(when);
            if (!m.Success)
            {
                return null;
            }

            var month = ParseMonthAbbrev(m.Groups["mon"].Value);
            if (month == null)
            {
                return null;
            }

            var day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
            var ap = m.Groups["ap"].Value;
            var h24 = To24h(h, ap);

            var candidate = new DateTimeOffset(now.Year, month.Value, day, h24, min, 0, TimeSpan.Zero);
            if (candidate < now.AddDays(-1))
            {
                candidate = candidate.AddYears(1);
            }

            return candidate;
        }
    }

    /// <summary>12h clock + am/pm to 24h hour.</summary>
    private static int To24h(int h, string ap)
    {
        var isPm = ap.Equals("pm", StringComparison.OrdinalIgnoreCase);
        if (isPm)
        {
            return h == 12 ? 12 : h + 12;
        }

        return h == 12 ? 0 : h;
    }

    /// <summary>Fixed 3-letter month abbreviation table — locale-independent (index+1).</summary>
    public static int? ParseMonthAbbrev(string mon)
    {
        for (var i = 0; i < MonthAbbrevs.Length; i++)
        {
            if (string.Equals(MonthAbbrevs[i], mon, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>
    /// Compute pace/burn math for a single parsed block against the window length implied
    /// by its <see cref="LimitKind"/> (session = 5h, week = 7d).
    /// </summary>
    public static LimitEntry ComputePace(ParsedBlock block, DateTimeOffset now)
    {
        var windowLength = block.Kind == LimitKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var windowStart = block.ResetsAt - windowLength;

        var rawElapsedFraction = (now - windowStart).Ticks / (double)windowLength.Ticks;
        var elapsedFraction = Math.Clamp(rawElapsedFraction, 0.0, 1.0);
        var elapsedPct = elapsedFraction * 100.0;

        var burnRatio = block.UsedPct / Math.Max(elapsedPct, Eps);
        var resetsInSeconds = Math.Max(0L, (long)(block.ResetsAt - now).TotalSeconds);

        var usedAtReset = block.UsedPct / Math.Max(elapsedFraction, Eps);
        var usedAtResetDisplay = Math.Min(usedAtReset, 999.0);

        var pace = burnRatio < 0.90 ? "ahead"
            : burnRatio > 1.10 ? "behind"
            : "on-track";

        return new LimitEntry(
            UsedPct: block.UsedPct,
            ResetsAt: block.ResetsAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ResetsInSeconds: resetsInSeconds,
            ElapsedPct: Math.Round(elapsedPct, 1),
            BurnRatio: Math.Round(burnRatio, 3),
            UsedAtReset: Math.Round(usedAtResetDisplay, 1),
            Pace: pace,
            Model: block.Model);
    }

    /// <summary>
    /// Build the full <see cref="LimitsReport"/> from parsed blocks: the single "Current
    /// session" block, the "Current week (all models)" block, and every per-model week block.
    /// </summary>
    public static LimitsReport BuildReport(IReadOnlyList<ParsedBlock> blocks, DateTimeOffset capturedAt, DateTimeOffset now, string source)
    {
        var sessionBlock = blocks.FirstOrDefault(b => b.Kind == LimitKind.Session);
        var weekAllBlock = blocks.FirstOrDefault(b => b.Kind == LimitKind.Week && b.Model == null);
        var weekByModelBlocks = blocks.Where(b => b.Kind == LimitKind.Week && b.Model != null).ToList();

        return new LimitsReport(
            CapturedAt: capturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            Source: source,
            Session: sessionBlock == null ? null : ComputePace(sessionBlock, now),
            Week: weekAllBlock == null ? null : ComputePace(weekAllBlock, now),
            WeekByModel: weekByModelBlocks.Select(b => ComputePace(b, now)).ToList());
    }
}
