using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class NaturalnessCandidatesSchemaTests
{
    private const int LineCount = 400;

    private static string ValidJson(int line = 47) => $$"""
    {
      "post": "/abs/post.md",
      "critic_model": "claude-opus-4-7",
      "extracted_at": "2026-05-28T10:00:00Z",
      "candidates": [
        {
          "id": "c1",
          "line": {{line}},
          "original": "Det er en directed acyclic graph: alle edges peger nedad.",
          "issue": "compound-noun feel midt i dansk prosa",
          "alternatives": [
            { "label": "A", "text": "rewrite A", "rationale": "split sentence",   "authorlikeness": 0.65 },
            { "label": "B", "text": "rewrite B", "rationale": "reorder for emphasis", "authorlikeness": 0.40 },
            { "label": "C", "text": "rewrite C", "rationale": "use Danish term",   "authorlikeness": 0.70 }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void Validate_acceptsWellFormedReply()
    {
        var r = NaturalnessCandidatesSchema.Validate(ValidJson(), LineCount);
        r.Ok.Should().BeTrue();
        r.Errors.Should().BeEmpty();
        r.Parsed.Should().NotBeNull();
        r.Parsed!.Candidates.Should().ContainSingle()
            .Which.Alternatives.Should().HaveCount(3);
    }

    [Fact]
    public void Validate_acceptsReply_evenWhenWrappedInFencedJsonAndProse()
    {
        var wrapped = "Sure!\n\n```json\n" + ValidJson() + "\n```\n\nDone.";
        NaturalnessCandidatesSchema.Validate(wrapped, LineCount).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_returnsOutOfRange_whenLineExceedsSource()
    {
        var r = NaturalnessCandidatesSchema.Validate(ValidJson(line: 9999), LineCount);
        r.Ok.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Field == "candidates[0].line" && e.Code == "out_of_range");
    }

    [Fact]
    public void Validate_rejects_whenAlternativesCountIsNotThree()
    {
        var bad = """
        {
          "post": "/p.md",
          "candidates": [
            {
              "id": "c1", "line": 1, "original": "x", "issue": "y",
              "alternatives": [
                { "label": "A", "text": "a", "rationale": "r", "authorlikeness": 0.5 },
                { "label": "B", "text": "b", "rationale": "r", "authorlikeness": 0.5 }
              ]
            }
          ]
        }
        """;
        var r = NaturalnessCandidatesSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Field == "candidates[0].alternatives" && e.Code == "wrong_count");
    }

    [Fact]
    public void Validate_rejects_whenAuthorlikenessOutOfRange()
    {
        var bad = ValidJson().Replace("0.65", "1.5");
        var r = NaturalnessCandidatesSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e =>
            e.Field == "candidates[0].alternatives[0].authorlikeness" && e.Code == "out_of_range");
    }

    [Fact]
    public void Validate_rejects_whenMoreThanFifteenCandidates()
    {
        var alts = """
            "alternatives": [
              { "label": "A", "text": "a", "rationale": "r", "authorlikeness": 0.5 },
              { "label": "B", "text": "b", "rationale": "r", "authorlikeness": 0.5 },
              { "label": "C", "text": "c", "rationale": "r", "authorlikeness": 0.5 }
            ]
            """;
        var cands = string.Join(",", Enumerable.Range(1, 16).Select(i =>
            $$"""{ "id": "c{{i}}", "line": 1, "original": "x", "issue": "y", {{alts}} }"""));
        var bad = $$"""{ "post": "/p.md", "candidates": [ {{cands}} ] }""";
        var r = NaturalnessCandidatesSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Field == "candidates" && e.Code == "too_many");
    }

    [Fact]
    public void Validate_rejects_whenLabelDuplicated()
    {
        var bad = ValidJson().Replace("\"label\": \"B\"", "\"label\": \"A\"");
        var r = NaturalnessCandidatesSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Code == "duplicate");
    }

    [Fact]
    public void Validate_rejects_whenNoJson()
    {
        var r = NaturalnessCandidatesSchema.Validate("nope", LineCount);
        r.Errors.Should().Contain(e => e.Code == "no_json");
    }
}
