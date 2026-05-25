using System.Collections.Generic;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingLearnPlannerTests
{
    private static WritingFinding Term(string match, int line) => new()
    {
        RuleId = "Writing.Anglicisms",
        Severity = WritingSeverity.Warning,
        Line = line, Column = 1,
        Match = match,
        Message = $"'{match}' reads as an anglicism in Danish.",
        Suggestions = new() { "udrulle" },
    };

    private static WritingFinding Critic(string dimension, string match, int line, string msg) => new()
    {
        RuleId = $"Critic.{dimension}",
        Severity = WritingSeverity.Suggestion,
        Line = line, Column = 1, Match = match, Message = msg, Suggestions = new(),
    };

    [Fact]
    public void Plan_proposesAllowlist_whenTermFlaggedAtLeastThreeTimes()
    {
        var report = new WritingReport
        {
            SourcePath = "/p/x.md",
            Findings = { Term("release", 1), Term("release", 5), Term("release", 9) },
        };
        var proposal = WritingLearnPlanner.Plan(report, new HashSet<string>());
        var act = proposal.Actions.Should().ContainSingle().Subject;
        act.Kind.Should().Be(LearnActionKind.Allowlist);
        act.Term.Should().Be("release");
        act.Accept.Should().BeTrue();
        act.EvidenceLines.Should().Equal(1, 5, 9);
    }

    [Fact]
    public void Plan_proposesAnglicism_butDoesNotAcceptByDefault_whenTermFlaggedTwice()
    {
        var report = new WritingReport
        {
            SourcePath = "/p/x.md",
            Findings = { Term("setup", 1), Term("setup", 4) },
        };
        var proposal = WritingLearnPlanner.Plan(report, new HashSet<string>());
        var act = proposal.Actions.Should().ContainSingle().Subject;
        act.Kind.Should().Be(LearnActionKind.Anglicism);
        act.Accept.Should().BeFalse();
        act.Term.Should().Be("setup");
        act.DanishAlternatives.Should().Contain("udrulle");
    }

    [Fact]
    public void Plan_skipsTermsAlreadyOnAllowlist()
    {
        var report = new WritingReport
        {
            SourcePath = "/p/x.md",
            Findings = { Term("release", 1), Term("release", 5), Term("release", 9) },
        };
        var allow = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "release" };

        var proposal = WritingLearnPlanner.Plan(report, allow);
        proposal.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Plan_dedupesLessons_byDimensionAndMessage_acrossLines()
    {
        var msg = "Add a signature phrase to break up the explanation.";
        var report = new WritingReport
        {
            SourcePath = "/p/x.md",
            Findings = {
                Critic("Tone", "abc", 10, msg),
                Critic("Tone", "def", 22, msg),
                Critic("Tone", "ghi", 31, msg),
            },
        };
        var proposal = WritingLearnPlanner.Plan(report, new HashSet<string>());
        var act = proposal.Actions.Should().ContainSingle().Subject;
        act.Kind.Should().Be(LearnActionKind.Lesson);
        act.Dimension.Should().Be("Tone");
        act.EvidenceLines.Should().Equal(10, 22, 31);
    }

    [Fact]
    public void Plan_treatsCriticTerminologyFindings_asTermsToAllowlistOrAdd()
    {
        var report = new WritingReport
        {
            SourcePath = "/p/x.md",
            Findings = {
                new WritingFinding {
                    RuleId = "Critic.Terminology", Severity = WritingSeverity.Suggestion,
                    Line = 4, Column = 1, Match = "bullet-points", Message = "English term left untranslated",
                    Suggestions = { "punktopstilling" },
                },
            },
        };
        var proposal = WritingLearnPlanner.Plan(report, new HashSet<string>());
        proposal.Actions.Should().ContainSingle()
            .Which.Kind.Should().Be(LearnActionKind.Anglicism);
    }
}
