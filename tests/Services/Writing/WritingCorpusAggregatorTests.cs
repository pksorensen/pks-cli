using System.Collections.Generic;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingCorpusAggregatorTests
{
    private static LearnProposal PostProposal(string post, params (LearnActionKind kind, string term, List<string>? alts)[] actions)
    {
        var p = new LearnProposal { SourcePath = post };
        foreach (var (kind, term, alts) in actions)
        {
            p.Actions.Add(new LearnAction
            {
                Kind = kind, Term = term, Accept = true,
                DanishAlternatives = alts ?? new(),
            });
        }
        return p;
    }

    [Fact]
    public void IsVerbForm_recognisesDanglishSuffixes_butKeepsKnownNouns()
    {
        WritingCorpusAggregator.IsVerbForm("shippe").Should().BeTrue();
        WritingCorpusAggregator.IsVerbForm("committe").Should().BeTrue();
        WritingCorpusAggregator.IsVerbForm("deploye").Should().BeTrue();
        WritingCorpusAggregator.IsVerbForm("draftet").Should().BeTrue();
        WritingCorpusAggregator.IsVerbForm("optimerede").Should().BeTrue();
        // Tech nouns that happen to end in -e must not be classified as verbs.
        WritingCorpusAggregator.IsVerbForm("feature").Should().BeFalse();
        WritingCorpusAggregator.IsVerbForm("queue").Should().BeFalse();
        WritingCorpusAggregator.IsVerbForm("release").Should().BeFalse();
        WritingCorpusAggregator.IsVerbForm("flow").Should().BeFalse();
    }

    [Fact]
    public void Aggregate_proposesAllowlist_forNounRecurringAcrossPosts()
    {
        var input = new[]
        {
            PostProposal("/p/post-1.md", (LearnActionKind.Anglicism, "feature", null)),
            PostProposal("/p/post-2.md", (LearnActionKind.Anglicism, "feature", null)),
            PostProposal("/p/post-3.md", (LearnActionKind.Anglicism, "feature", null)),
        };
        var result = WritingCorpusAggregator.Aggregate(input);

        var a = result.Actions.Should().ContainSingle().Subject;
        a.Kind.Should().Be(LearnActionKind.Allowlist);
        a.Term.Should().Be("feature");
        a.Accept.Should().BeTrue();
        a.Rationale.Should().Contain("3 posts");
    }

    [Fact]
    public void Aggregate_proposesAnglicism_forVerbFormRecurringAcrossPosts_andCarriesAlternatives()
    {
        var input = new[]
        {
            PostProposal("/p/a.md", (LearnActionKind.Anglicism, "shippe", new() { "udgive" })),
            PostProposal("/p/b.md", (LearnActionKind.Anglicism, "shippe", new() { "sende" })),
        };
        var result = WritingCorpusAggregator.Aggregate(input);

        var a = result.Actions.Should().ContainSingle().Subject;
        a.Kind.Should().Be(LearnActionKind.Anglicism);
        a.Term.Should().Be("shippe");
        a.DanishAlternatives.Should().BeEquivalentTo(new[] { "udgive", "sende" });
    }

    [Fact]
    public void Aggregate_dedupesByPost_notByActionCount()
    {
        // Same post shouldn't count twice if it has multiple actions for the term.
        var input = new[]
        {
            PostProposal("/p/a.md",
                (LearnActionKind.Anglicism, "feature", null),
                (LearnActionKind.Anglicism, "feature", null)),
        };
        var result = WritingCorpusAggregator.Aggregate(input,
            new WritingCorpusAggregator.Options { MinPosts = 2 });

        result.Actions.Should().BeEmpty("'feature' was only in one post even though it had two actions");
    }

    [Fact]
    public void Aggregate_skipsOneOffs_belowMinPosts()
    {
        var input = new[] { PostProposal("/p/a.md", (LearnActionKind.Anglicism, "random", null)) };
        var result = WritingCorpusAggregator.Aggregate(input);
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_ignoresLessonActions()
    {
        var lesson = new LearnProposal { SourcePath = "/p/a.md" };
        lesson.Actions.Add(new LearnAction { Kind = LearnActionKind.Lesson, Dimension = "Tone", Lesson = "x", Accept = true });

        var result = WritingCorpusAggregator.Aggregate(new[] { lesson });
        result.Actions.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_sortsActionsByPostCountDescending()
    {
        var input = new[]
        {
            PostProposal("/p/1.md", (LearnActionKind.Anglicism, "feature", null)),
            PostProposal("/p/2.md", (LearnActionKind.Anglicism, "feature", null)),
            PostProposal("/p/3.md", (LearnActionKind.Anglicism, "feature", null)),
            PostProposal("/p/1.md", (LearnActionKind.Anglicism, "tracke", null)),
            PostProposal("/p/2.md", (LearnActionKind.Anglicism, "tracke", null)),
        };
        var result = WritingCorpusAggregator.Aggregate(input);

        result.Actions[0].Term.Should().Be("feature", "more posts → earlier in list");
        result.Actions[1].Term.Should().Be("tracke");
    }
}
