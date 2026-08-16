using FluentAssertions;
using PKS.Infrastructure.Services.Runner;
using Xunit;

namespace PKS.CLI.Tests.Services.Runner;

/// <summary>
/// Unit tests for <see cref="ClaudeRuntimeCheck"/>. The behavior under test is a measurement, not a
/// guess: on 2026-08-16 a clean <c>node:20</c> container installed claude 2.1.197 from npm while the
/// registry's latest was 2.1.233, because 2.1.198+ declares <c>engines.node &gt;= 22</c>.
/// </summary>
public class ClaudeRuntimeCheckTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData("v20.19.4", 20)]
    [InlineData("v22.11.0\n", 22)]
    [InlineData("  v24.0.1  ", 24)]
    [InlineData("20.19.4", 20)]
    public void ParseNodeMajor_ReadsTheMajor(string output, int expected)
    {
        ClaudeRuntimeCheck.ParseNodeMajor(output).Should().Be(expected);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bash: node: command not found")]
    public void ParseNodeMajor_UnrecognizableOutput_IsNull(string? output)
    {
        ClaudeRuntimeCheck.ParseNodeMajor(output).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void PinsClaudeToAnOldRelease_OnlyBelowTheMinimum()
    {
        ClaudeRuntimeCheck.PinsClaudeToAnOldRelease(20).Should().BeTrue();
        ClaudeRuntimeCheck.PinsClaudeToAnOldRelease(18).Should().BeTrue();
        ClaudeRuntimeCheck.PinsClaudeToAnOldRelease(22).Should().BeFalse();
        ClaudeRuntimeCheck.PinsClaudeToAnOldRelease(24).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Speed", "Fast")]
    public void PinsClaudeToAnOldRelease_UnknownVersion_DoesNotWarn()
    {
        // A failed probe must not produce a confident warning about the wrong thing.
        ClaudeRuntimeCheck.PinsClaudeToAnOldRelease(null).Should().BeFalse();
    }
}
