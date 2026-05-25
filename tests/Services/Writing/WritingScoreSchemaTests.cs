using System.Linq;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingScoreSchemaTests
{
    private const int LineCount = 400;

    private static string Valid() => """
        {
          "dimensions": {
            "Naturalness": 3, "Tone": 4, "Terminology": 4, "Hook": 5, "Value": 4
          },
          "findings": [
            { "dimension": "Tone", "line": 12, "match": "x", "message": "y", "suggestions": ["z"] }
          ],
          "notes": "Tighten the opening."
        }
        """;

    [Fact]
    public void Validate_acceptsWellFormedReply()
    {
        var r = WritingScoreSchema.Validate(Valid(), LineCount);
        r.Ok.Should().BeTrue();
        r.Errors.Should().BeEmpty();
        r.Dimensions.Should().HaveCount(5);
        r.Findings.Should().ContainSingle().Which.Line.Should().Be(12);
        r.Notes.Should().Be("Tighten the opening.");
    }

    [Fact]
    public void Validate_acceptsReply_evenWhenWrappedInFencedJsonAndProse()
    {
        var wrapped = "Sure! Here's the assessment:\n\n```json\n" + Valid() + "\n```\n\nLet me know.";
        WritingScoreSchema.Validate(wrapped, LineCount).Ok.Should().BeTrue();
    }

    [Fact]
    public void Validate_returnsFieldLevelError_whenDimensionMissing()
    {
        var missing = """
            { "dimensions": { "Naturalness": 3, "Tone": 4, "Terminology": 4, "Hook": 5 },
              "findings": [], "notes": "ok" }
            """;
        var r = WritingScoreSchema.Validate(missing, LineCount);
        r.Ok.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Field == "dimensions.Value" && e.Code == "missing");
    }

    [Fact]
    public void Validate_returnsOutOfRange_whenDimensionScoreOutsideOneToFive()
    {
        var bad = """
            { "dimensions": { "Naturalness": 9, "Tone": 4, "Terminology": 4, "Hook": 5, "Value": 4 },
              "findings": [], "notes": "ok" }
            """;
        var r = WritingScoreSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Field == "dimensions.Naturalness" && e.Code == "out_of_range");
    }

    [Fact]
    public void Validate_returnsOutOfRange_whenFindingLineExceedsSource()
    {
        var bad = $$"""
            { "dimensions": { "Naturalness": 3, "Tone": 4, "Terminology": 4, "Hook": 5, "Value": 4 },
              "findings": [ { "dimension": "Tone", "line": 5000, "match": "x", "message": "y", "suggestions": [] } ],
              "notes": "ok" }
            """;
        var r = WritingScoreSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Field == "findings[0].line" && e.Code == "out_of_range");
    }

    [Fact]
    public void Validate_returnsUnknownEnum_whenFindingDimensionInvalid()
    {
        var bad = """
            { "dimensions": { "Naturalness": 3, "Tone": 4, "Terminology": 4, "Hook": 5, "Value": 4 },
              "findings": [ { "dimension": "Vibe", "line": 1, "match": "x", "message": "y", "suggestions": [] } ],
              "notes": "ok" }
            """;
        var r = WritingScoreSchema.Validate(bad, LineCount);
        r.Errors.Should().Contain(e => e.Field == "findings[0].dimension" && e.Code == "unknown_enum");
    }

    [Fact]
    public void Validate_returnsNoJson_onEmptyOrJunkInput()
    {
        WritingScoreSchema.Validate("",       LineCount).Errors.Should().Contain(e => e.Code == "no_json");
        WritingScoreSchema.Validate("hi mom", LineCount).Errors.Should().Contain(e => e.Code == "no_json");
    }

    [Fact]
    public void Validate_returnsParseError_onMalformedJson()
    {
        // Has both braces (so ExtractJson returns something), but the inside is broken.
        WritingScoreSchema.Validate("{not: \"json}", LineCount)
            .Errors.Should().Contain(e => e.Code == "parse_error");
    }
}
