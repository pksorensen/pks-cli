using FluentAssertions;
using PKS.Commands.Claude;
using Xunit;

namespace PKS.CLI.Tests.Commands.Claude;

/// <summary>
/// Unit tests for <see cref="UsagePanelParser"/>: the deterministic `/usage` panel parser,
/// the reset-time rollover resolver, and the pace/burn math. All pure — no tmux, no process
/// spawning. The fixture is a verbatim copy of a real captured `/usage` panel.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class UsagePanelParserTests
{
    private static readonly DateTimeOffset FixtureNow = new(2026, 7, 15, 16, 0, 0, TimeSpan.Zero);

    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "Claude", "usage-panel-capture.txt");
        File.Exists(path).Should().BeTrue($"the fixture should have been copied to {path} by the test csproj's CopyToOutputDirectory item");
        return File.ReadAllText(path);
    }

    // ------------------------------------------------------------------
    // 9.1 Parser tests (fixture-driven)
    // ------------------------------------------------------------------

    [Fact]
    public void TryParse_Fixture_ReturnsThreeBlocks()
    {
        var blocks = UsagePanelParser.TryParse(LoadFixture(), FixtureNow);

        blocks.Should().HaveCount(3);
    }

    [Fact]
    public void TryParse_Fixture_SessionBlock_IsCorrect()
    {
        var blocks = UsagePanelParser.TryParse(LoadFixture(), FixtureNow);

        var session = blocks.Single(b => b.Kind == LimitKind.Session);
        session.Model.Should().BeNull();
        session.UsedPct.Should().Be(3);
        session.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 15, 19, 19, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_Fixture_WeekAllModelsBlock_IsCorrect()
    {
        var blocks = UsagePanelParser.TryParse(LoadFixture(), FixtureNow);

        var weekAll = blocks.Single(b => b.Kind == LimitKind.Week && b.Model == null);
        weekAll.UsedPct.Should().Be(26);
        weekAll.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 20, 3, 59, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_Fixture_WeekFableBlock_IsCorrect()
    {
        var blocks = UsagePanelParser.TryParse(LoadFixture(), FixtureNow);

        var weekFable = blocks.Single(b => b.Kind == LimitKind.Week && b.Model == "Fable");
        weekFable.UsedPct.Should().Be(28);
        weekFable.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 20, 3, 59, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_IgnoresContributingSection()
    {
        // The fixture's "What's contributing..." section has its own 3%/1%/12%/19%/87%
        // numbers (Skills, Subagents) that are NOT under a "Current ..." header. Only the
        // three genuine Current-session/Current-week blocks should ever be produced.
        var blocks = UsagePanelParser.TryParse(LoadFixture(), FixtureNow);

        blocks.Should().HaveCount(3);
        blocks.Should().OnlyContain(b => b.Kind == LimitKind.Session || b.Kind == LimitKind.Week);
    }

    [Fact]
    public void TryParse_Garbage_ReturnsEmpty()
    {
        var blocks = UsagePanelParser.TryParse("this is not a usage panel at all\njust some random text\n", FixtureNow);

        blocks.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // 9.2 Reset-rollover tests
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveReset_SessionTimeOnly_NotYetPassed_ResolvesToday()
    {
        var now = new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Session, "7:19pm", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 15, 19, 19, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_SessionTimeOnly_AlreadyPassed_ResolvesTomorrow()
    {
        var now = new DateTimeOffset(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Session, "7:19pm", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 16, 19, 19, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_SessionTimeOnly_MidnightEdge_NotYetPassed()
    {
        var now = new DateTimeOffset(2026, 7, 14, 23, 59, 30, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Session, "12:00am", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_SessionTimeOnly_NoonEdge()
    {
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Session, "12:30pm", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 15, 12, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_Week_SameYear()
    {
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Week, "Jul 20, 3:59am", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 20, 3, 59, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_Week_YearRollover()
    {
        var now = new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Week, "Jan 2, 3:59am", now);

        resolved.Should().Be(new DateTimeOffset(2027, 1, 2, 3, 59, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ResolveReset_Week_GraceWindow_DoesNotBumpAYearForRecentPast()
    {
        // now is a few minutes after the reset already passed today — should NOT be bumped
        // a whole year forward (the 1-day grace window absorbs capture latency).
        var now = new DateTimeOffset(2026, 7, 15, 4, 10, 0, TimeSpan.Zero);

        var resolved = UsagePanelParser.ResolveReset(LimitKind.Week, "Jul 15, 3:59am", now);

        resolved.Should().Be(new DateTimeOffset(2026, 7, 15, 3, 59, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Jul")]
    [InlineData("jul")]
    [InlineData("JUL")]
    public void ParseMonthAbbrev_IsLocaleIndependent(string mon)
    {
        UsagePanelParser.ParseMonthAbbrev(mon).Should().Be(7);
    }

    // ------------------------------------------------------------------
    // 9.3 Pace-math tests
    // ------------------------------------------------------------------

    private static ParsedBlock BlockForElapsedFraction(LimitKind kind, int usedPct, double elapsedFraction, DateTimeOffset now)
    {
        var windowLength = kind == LimitKind.Session ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
        var resetsAt = now + TimeSpan.FromTicks((long)(windowLength.Ticks * (1 - elapsedFraction)));
        return new ParsedBlock(kind, null, usedPct, resetsAt);
    }

    [Fact]
    public void ComputePace_SessionAhead_MatchesFixtureDerivation()
    {
        var now = new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero);
        var block = new ParsedBlock(LimitKind.Session, null, 3, new DateTimeOffset(2026, 7, 15, 19, 19, 0, TimeSpan.Zero));

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.ElapsedPct.Should().BeApproximately(33.7, 0.1);
        entry.BurnRatio.Should().BeApproximately(0.089, 0.01);
        entry.Pace.Should().Be("ahead");
        entry.ResetsInSeconds.Should().Be(11940);
        entry.UsedAtReset.Should().BeApproximately(8.9, 0.1);
    }

    [Fact]
    public void ComputePace_WeekAheadAllModels_MatchesFixtureDerivation()
    {
        var now = new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero);
        var block = new ParsedBlock(LimitKind.Week, null, 26, new DateTimeOffset(2026, 7, 20, 3, 59, 0, TimeSpan.Zero));

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.ElapsedPct.Should().BeApproximately(35.7, 0.1);
        entry.BurnRatio.Should().BeApproximately(0.728, 0.01);
        entry.Pace.Should().Be("ahead");
        entry.ResetsInSeconds.Should().Be(388740);
        entry.UsedAtReset.Should().BeApproximately(72.8, 0.1);
    }

    [Fact]
    public void ComputePace_WeekAheadFable_MatchesFixtureDerivation()
    {
        var now = new DateTimeOffset(2026, 7, 15, 16, 0, 0, TimeSpan.Zero);
        var block = new ParsedBlock(LimitKind.Week, "Fable", 28, new DateTimeOffset(2026, 7, 20, 3, 59, 0, TimeSpan.Zero));

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.BurnRatio.Should().BeApproximately(0.784, 0.01);
        entry.UsedAtReset.Should().BeApproximately(78.4, 0.1);
        entry.Pace.Should().Be("ahead");
        entry.Model.Should().Be("Fable");
    }

    [Fact]
    public void ComputePace_OnTrackBoundary_ExactParity()
    {
        var now = DateTimeOffset.UtcNow;
        var block = BlockForElapsedFraction(LimitKind.Week, 50, 0.50, now);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.BurnRatio.Should().BeApproximately(1.0, 0.01);
        entry.Pace.Should().Be("on-track");
    }

    [Fact]
    public void ComputePace_JustBelowDeadband_IsAhead()
    {
        var now = DateTimeOffset.UtcNow;
        var block = BlockForElapsedFraction(LimitKind.Week, 89, 1.00, now);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.BurnRatio.Should().BeApproximately(0.89, 0.01);
        entry.Pace.Should().Be("ahead");
    }

    [Fact]
    public void ComputePace_JustAboveDeadband_IsBehind()
    {
        var now = DateTimeOffset.UtcNow;
        var block = BlockForElapsedFraction(LimitKind.Week, 111, 1.00, now);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.BurnRatio.Should().BeApproximately(1.11, 0.01);
        entry.Pace.Should().Be("behind");
    }

    [Fact]
    public void ComputePace_Behind_UsedAtResetCappedForDisplay()
    {
        var now = DateTimeOffset.UtcNow;
        // elapsedFraction so tiny that the naive projection would blow past 999.
        var block = BlockForElapsedFraction(LimitKind.Week, 100, 0.001, now);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.Pace.Should().Be("behind");
        entry.UsedAtReset.Should().BeLessOrEqualTo(999.0);
    }

    [Fact]
    public void ComputePace_TypicalBehindScenario()
    {
        var now = DateTimeOffset.UtcNow;
        var block = BlockForElapsedFraction(LimitKind.Week, 80, 0.30, now);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.BurnRatio.Should().BeApproximately(2.67, 0.05);
        entry.Pace.Should().Be("behind");
        entry.UsedAtReset.Should().BeLessOrEqualTo(999.0);
    }

    [Fact]
    public void ComputePace_ClockSkew_NowBeforeWindowStart_ClampsAndStaysFinite()
    {
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        // resetsAt far enough in the future that windowStart (resetsAt - 5h) is after `now`.
        var resetsAt = now + TimeSpan.FromHours(10);
        var block = new ParsedBlock(LimitKind.Session, null, 1, resetsAt);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.ElapsedPct.Should().Be(0);
        double.IsFinite(entry.BurnRatio).Should().BeTrue();
        entry.BurnRatio.Should().BeGreaterThan(1.10);
        entry.Pace.Should().Be("behind");
    }

    [Fact]
    public void ComputePace_ResetsInSeconds_NeverNegative_WhenNowIsPastReset()
    {
        var now = new DateTimeOffset(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);
        var resetsAt = now - TimeSpan.FromHours(1);
        var block = new ParsedBlock(LimitKind.Session, null, 50, resetsAt);

        var entry = UsagePanelParser.ComputePace(block, now);

        entry.ResetsInSeconds.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // On-the-hour reset times (regression)
    //
    // Claude Code omits the minutes when a limit resets exactly on the hour, so a real
    // panel reads "Resets 4pm (UTC)" / "Resets Jul 20, 4am (UTC)". The reset-time regexes
    // originally required a mandatory ":mm", so every block failed to resolve, TryParse
    // returned empty, and UsageTmuxDriver polled until its 60s budget expired — reported
    // to the user as "Timed out capturing /usage from Claude Code inside tmux."
    // Fixture captured live from Claude Code v2.1.214.
    // ------------------------------------------------------------------

    private static readonly DateTimeOffset OnTheHourNow = new(2026, 7, 18, 13, 30, 0, TimeSpan.Zero);

    private static string LoadOnTheHourFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "Claude", "usage-panel-capture-onthehour.txt");
        File.Exists(path).Should().BeTrue($"the fixture should have been copied to {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TryParse_OnTheHourResets_ReturnsAllThreeBlocks()
    {
        var blocks = UsagePanelParser.TryParse(LoadOnTheHourFixture(), OnTheHourNow);

        blocks.Should().HaveCount(3, "a minutes-less reset time must still parse");
    }

    [Fact]
    public void TryParse_OnTheHourResets_SessionBlock_ResolvesToTopOfHour()
    {
        var blocks = UsagePanelParser.TryParse(LoadOnTheHourFixture(), OnTheHourNow);

        var session = blocks.Single(b => b.Kind == LimitKind.Session);
        session.Model.Should().BeNull();
        session.UsedPct.Should().Be(58);
        // "Resets 4pm (UTC)" with now = 13:30 UTC -> the same day at 16:00.
        session.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 18, 16, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TryParse_OnTheHourResets_WeeklyBlocks_ResolveToTopOfHour()
    {
        var blocks = UsagePanelParser.TryParse(LoadOnTheHourFixture(), OnTheHourNow);

        var weekAll = blocks.Single(b => b.Kind == LimitKind.Week && b.Model == null);
        weekAll.UsedPct.Should().Be(60);
        weekAll.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 20, 4, 0, 0, TimeSpan.Zero));

        var weekModel = blocks.Single(b => b.Kind == LimitKind.Week && b.Model != null);
        weekModel.Model.Should().Be("Fable");
        weekModel.UsedPct.Should().Be(69);
        weekModel.ResetsAt.Should().Be(new DateTimeOffset(2026, 7, 20, 4, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("4pm", 16, 0)]
    [InlineData("4:30pm", 16, 30)]
    [InlineData("12am", 0, 0)]
    [InlineData("12pm", 12, 0)]
    public void TryParse_SessionResetVariants_AllResolve(string when, int expectedHour, int expectedMinute)
    {
        var pane = $"   Current session\n   ####   7% used\n   Resets {when} (UTC)\n";
        var now = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero).AddMinutes(-1);

        var session = UsagePanelParser.TryParse(pane, now).Single(b => b.Kind == LimitKind.Session);

        session.ResetsAt.Hour.Should().Be(expectedHour);
        session.ResetsAt.Minute.Should().Be(expectedMinute);
    }
}
