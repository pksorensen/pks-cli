using System.Collections.Generic;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

/// Deterministic tests for the critic's prompt builder and response parser.
/// The actual LLM round-trip is covered by an integration smoke test, not here.
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingCriticTests
{
    [Fact]
    public void BuildSystemPrompt_includesProfile_rubric_andReferences()
    {
        var req = new CritiqueRequest
        {
            SourcePath = "/tmp/x.md",
            Content = "hej",
            Channel = "blog",
            Profile = "MY-PROFILE-MARKER",
            ChannelRubric = "BLOG-RUBRIC-MARKER",
            References = new[]
            {
                new ReferenceSample { Id = "p1", Content = "REF-SAMPLE-MARKER" },
            },
            Anglicisms = new[]
            {
                new AnglicismEntry { English = "deploye", DanishAlternatives = { "udrulle" } },
            },
        };

        var sys = WritingCritic.BuildSystemPrompt(req);

        sys.Should().Contain("MY-PROFILE-MARKER");
        sys.Should().Contain("BLOG-RUBRIC-MARKER");
        sys.Should().Contain("REF-SAMPLE-MARKER");
        sys.Should().Contain("deploye → udrulle");
        sys.Should().Contain("Naturalness").And.Contain("Tone")
            .And.Contain("Terminology").And.Contain("Hook").And.Contain("Value");
        sys.Should().Contain("JSON");
    }

    [Fact]
    public void BuildUserPrompt_numbersLines()
    {
        var req = new CritiqueRequest
        {
            SourcePath = "/tmp/x.md", Channel = "blog",
            Content = "first\nsecond\nthird",
        };
        var user = WritingCritic.BuildUserPrompt(req);
        user.Should().Contain("   1  first");
        user.Should().Contain("   2  second");
        user.Should().Contain("   3  third");
    }

    [Fact]
    public void TryParseResponse_parsesValidJson_evenWhenFencedAndPrefaced()
    {
        var response = """
            Sure, here is my critique:

            ```json
            {
              "dimensions": {
                "Naturalness": 3,
                "Tone": 4,
                "Terminology": 2,
                "Hook": 4,
                "Value": 5
              },
              "findings": [
                {
                  "dimension": "Terminology",
                  "line": 7,
                  "match": "vi skal deploye",
                  "message": "Anglicism",
                  "suggestions": ["vi skal udrulle"]
                }
              ],
              "notes": "Tighten the opening."
            }
            ```

            Hope that helps!
            """;

        var ok = WritingCritic.TryParseResponse(response, out var dims, out var findings, out var notes, out var err);

        ok.Should().BeTrue(err);
        dims["Naturalness"].Should().Be(3);
        dims["Tone"].Should().Be(4);
        findings.Should().ContainSingle();
        findings[0].RuleId.Should().Be("Critic.Terminology");
        findings[0].Line.Should().Be(7);
        findings[0].Suggestions.Should().ContainSingle().Which.Should().Be("vi skal udrulle");
        notes.Should().Be("Tighten the opening.");
    }

    [Fact]
    public void TryParseResponse_clampsOutOfRangeScores()
    {
        var response = """
            { "dimensions": { "Naturalness": 9, "Tone": -3 }, "findings": [] }
            """;
        var ok = WritingCritic.TryParseResponse(response, out var dims, out _, out _, out _);
        ok.Should().BeTrue();
        dims["Naturalness"].Should().Be(5);
        dims["Tone"].Should().Be(1);
    }

    [Fact]
    public void TryParseResponse_returnsFalse_onMalformedJson()
    {
        var ok = WritingCritic.TryParseResponse("hi I cant be parsed", out _, out _, out _, out var err);
        ok.Should().BeFalse();
        err.Should().NotBeNull();
    }
}
