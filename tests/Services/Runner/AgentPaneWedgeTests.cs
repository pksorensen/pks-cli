using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="AgentPaneWedge"/>, the check that stops the runner from reporting
/// completed/success for an agent that spent the whole job parked on a prompt. Pane text below is
/// transcribed from real job panes, so a claude release that reworks a screen breaks a test here
/// rather than silently turning a wedged job back into a "success".
/// </summary>
public class AgentPaneWedgeTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Detect_NotLoggedIn_IsAWedge()
    {
        // Observed 2026-08-16, session fle0ymrg: an auto-created (empty) credentials volume.
        // Claude launches, answers the seeded prompt with this, and idles at the REPL.
        const string pane = """
            ❯ Projekt: museliving

              Opgave: SEO-ugerapport
              ⎿  Not logged in · Please run /login

            ❯
            """;

        AgentPaneWedge.Detect(pane).Should().Contain("not logged in");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData("Select login method:\n  1. Claude account with subscription", "login-method")]
    [InlineData("Browser didn't open?\nPaste code here if prompted >", "OAuth code")]
    [InlineData("Bypass Permissions mode\n  1. No, exit\n  2. Yes, I accept", "bypass-permissions")]
    [InlineData("Learn the moves\n  1. Take the tour\n  2. Skip for now", "onboarding tour")]
    [InlineData("Choose the text style that looks best with your terminal", "theme picker")]
    [InlineData("Detected a custom API key in your environment", "custom-API-key")]
    [InlineData("Security notes\nPress Enter to continue", "Press-Enter")]
    public void Detect_InteractiveGates_AreWedges(string pane, string expected)
    {
        AgentPaneWedge.Detect(pane).Should().Contain(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Detect_WorkingOrFinishedPane_IsNotAWedge()
    {
        const string pane = """
            ● Wrote the weekly report to reports/2026-W33.md and committed it.

            ❯
              ⏵⏵ bypass permissions on (shift+tab to cycle)
            """;

        // "bypass permissions on" in the status line must NOT read as the bypass-permissions
        // dialog — that footer is present for the entire life of every job.
        AgentPaneWedge.Detect(pane).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void Detect_EmptyPane_IsNotAWedge()
    {
        AgentPaneWedge.Detect(null).Should().BeNull();
        AgentPaneWedge.Detect("   \n  ").Should().BeNull();
    }
}
