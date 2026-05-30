using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class NaturalnessApplierTests
{
    private readonly NaturalnessApplier _sut = new();

    private static (NaturalnessCandidatesFile cands, NaturalnessPicksFile picks) MakePair(
        string original, string aText, string chosen = "A")
    {
        var cands = new NaturalnessCandidatesFile
        {
            Post = "/p.md",
            Candidates = new()
            {
                new NaturalnessCandidate
                {
                    Id = "c1", Line = 2, Original = original, Issue = "issue",
                    Alternatives = new()
                    {
                        new() { Label = "A", Text = aText, Authorlikeness = 0.7, Rationale = "r" },
                        new() { Label = "B", Text = "alt B", Authorlikeness = 0.5, Rationale = "r" },
                        new() { Label = "C", Text = "alt C", Authorlikeness = 0.4, Rationale = "r" },
                    },
                },
            },
        };
        var picks = new NaturalnessPicksFile
        {
            Post = "/p.md",
            Picks = new() { new() { CandidateId = "c1", Chosen = chosen } },
        };
        return (cands, picks);
    }

    [Fact]
    public void Plan_producesUnifiedDiff_andEdit()
    {
        var content = "line 1\nDet er en directed acyclic graph: edges nedad.\nline 3\n";
        var (cands, picks) = MakePair(
            "Det er en directed acyclic graph: edges nedad.",
            "Det er en DAG — alle edges peger nedad.");
        var plan = _sut.Plan(content, cands, picks);
        plan.Edits.Should().ContainSingle();
        plan.UnifiedDiff.Should().Contain("-Det er en directed acyclic graph");
        plan.UnifiedDiff.Should().Contain("+Det er en DAG");
    }

    [Fact]
    public void Apply_replacesLine_andReportsApplied()
    {
        var content = "line 1\nDet er en directed acyclic graph: edges nedad.\nline 3\n";
        var (cands, picks) = MakePair(
            "Det er en directed acyclic graph: edges nedad.",
            "Det er en DAG — alle edges peger nedad.");
        var plan = _sut.Plan(content, cands, picks);
        var result = _sut.Apply(content, plan);
        result.Applied.Should().ContainSingle();
        result.Skipped.Should().BeEmpty();
        result.NewContent.Should().Contain("Det er en DAG");
        result.NewContent.Should().NotContain("directed acyclic graph");
    }

    [Fact]
    public void Apply_skipsPick_alreadyApplied()
    {
        var content = "line 1\nfoo bar baz\nline 3\n";
        var (cands, picks) = MakePair("foo bar baz", "REPLACEMENT");
        picks.Picks[0].Applied = true;
        var plan = _sut.Plan(content, cands, picks);
        plan.Edits.Should().BeEmpty();
    }

    [Fact]
    public void Apply_skipsPick_skip()
    {
        var content = "line 1\nfoo bar baz\nline 3\n";
        var (cands, picks) = MakePair("foo bar baz", "REPLACEMENT", chosen: "skip");
        var plan = _sut.Plan(content, cands, picks);
        plan.Edits.Should().BeEmpty();
    }

    [Fact]
    public void Apply_warns_whenLineDriftedBeyondWindow()
    {
        // Pretend the post was edited and the target line is now far away.
        var content = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"unrelated line {i}")) + "\n";
        var (cands, picks) = MakePair("totally-missing original sentence", "rewrite", chosen: "A");
        var plan = _sut.Plan(content, cands, picks);
        var result = _sut.Apply(content, plan);
        result.Skipped.Should().ContainSingle();
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_resolves_chosenWithSourceSuffix_AOpus()
    {
        var content = "line 1\nfoo bar baz qux quux\nline 3\n";
        var cands = new NaturalnessCandidatesFile
        {
            Post = "/p.md",
            Candidates = new()
            {
                new NaturalnessCandidate
                {
                    Id = "c1", Line = 2, Original = "foo bar baz qux quux", Issue = "i",
                    Alternatives = new()
                    {
                        new() { Label = "A", Source = "opus",  Text = "OPUS-A",  Authorlikeness = 0.6, Rationale = "r" },
                        new() { Label = "A", Source = "gpt5",  Text = "GPT5-A",  Authorlikeness = 0.7, Rationale = "r" },
                        new() { Label = "B", Source = "opus",  Text = "OPUS-B",  Authorlikeness = 0.5, Rationale = "r" },
                    },
                },
            },
        };
        var picks = new NaturalnessPicksFile
        {
            Post = "/p.md",
            Picks = new() { new() { CandidateId = "c1", Chosen = "A-gpt5" } },
        };
        var plan = _sut.Plan(content, cands, picks);
        plan.Edits.Should().ContainSingle();
        plan.Edits[0].Replacement.Should().Be("GPT5-A");
        plan.Edits[0].AcceptedFromCritic.Should().Be("gpt5");
    }

    [Fact]
    public void Plan_backCompat_chosenWithoutSuffix_matchesByLabel()
    {
        var content = "line 1\nfoo bar baz qux quux\nline 3\n";
        var cands = new NaturalnessCandidatesFile
        {
            Post = "/p.md",
            Candidates = new()
            {
                new NaturalnessCandidate
                {
                    Id = "c1", Line = 2, Original = "foo bar baz qux quux", Issue = "i",
                    Alternatives = new()
                    {
                        new() { Label = "A", Text = "PLAIN-A", Authorlikeness = 0.6, Rationale = "r" },
                        new() { Label = "B", Text = "PLAIN-B", Authorlikeness = 0.5, Rationale = "r" },
                        new() { Label = "C", Text = "PLAIN-C", Authorlikeness = 0.4, Rationale = "r" },
                    },
                },
            },
        };
        var picks = new NaturalnessPicksFile
        {
            Post = "/p.md",
            Picks = new() { new() { CandidateId = "c1", Chosen = "A" } },
        };
        var plan = _sut.Plan(content, cands, picks);
        plan.Edits.Should().ContainSingle();
        plan.Edits[0].Replacement.Should().Be("PLAIN-A");
        plan.Edits[0].AcceptedFromCritic.Should().BeNull();
    }

    [Fact]
    public void Apply_usesCustomText_whenChosenIsOther()
    {
        var content = "line 1\nfoo bar baz\nline 3\n";
        var (cands, picks) = MakePair("foo bar baz", "(unused)", chosen: "other");
        picks.Picks[0].CustomText = "FREEFORM REWRITE";
        var plan = _sut.Plan(content, cands, picks);
        var result = _sut.Apply(content, plan);
        result.Applied.Should().ContainSingle();
        result.NewContent.Should().Contain("FREEFORM REWRITE");
    }
}
