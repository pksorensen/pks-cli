namespace PKS.Infrastructure.Services.Brain.Asf;

/// The day an event belongs to.
///
/// Europe/Copenhagen, not UTC — the same rule as `dayKey()` in
/// src/lib/analytics/day-key.ts on the server. Both ends must agree, or a chunk
/// filed under one day is rolled up under another and the daily totals stop
/// matching the chunk manifests.
///
/// UTC would be the obvious choice and it is the wrong one here: in summer,
/// anything logged between 00:00 and 02:00 local falls into the previous UTC day.
/// For a developer who works late that is a visible share of the data, and it
/// shows up as broken streaks and an "active days" count that disagrees with
/// what they remember doing.
public static class BrainDayKey
{
    public const string TimeZoneId = "Europe/Copenhagen";

    private static readonly TimeZoneInfo Zone = Resolve();

    public static DateOnly Of(DateTimeOffset ts) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ts, Zone).DateTime);

    public static string Key(DateTimeOffset ts) => Of(ts).ToString("yyyy-MM-dd");

    private static TimeZoneInfo Resolve()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows without ICU, or a container with no tzdata. One hour off is
            // far better than a crash in a nightly backup job, and the offset only
            // shifts events in the midnight hour.
            return TimeZoneInfo.CreateCustomTimeZone("cph-fallback", TimeSpan.FromHours(1), "CPH", "CPH");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("cph-fallback", TimeSpan.FromHours(1), "CPH", "CPH");
        }
    }
}
