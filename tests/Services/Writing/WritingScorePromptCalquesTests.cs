using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingScorePromptCalquesTests
{
    [Fact]
    public void System_includesCalqueSection_andExplicitInstruction_evenWithEmptyList()
    {
        var sys = WritingScorePrompt.BuildSystem(new WritingScorePrompt.Request
        {
            SourcePath = "/tmp/x.md", Content = "x", Channel = "blog",
        });

        sys.Should().Contain("Calques (loan-translations)",
            "the prompt must always teach the critic about this class of error");
        sys.Should().Contain("barn",
            "the worked example is what makes the instruction stick");
    }

    [Fact]
    public void System_listsKnownCalques_whenProvided()
    {
        var sys = WritingScorePrompt.BuildSystem(new WritingScorePrompt.Request
        {
            SourcePath = "/tmp/x.md", Content = "x", Channel = "blog",
            Calques = new[]
            {
                new CalqueEntry { LiteralDanish = "barn",
                    Alternatives = { "barne-Claude", "child" },
                    Why = "menneskeunge" },
                new CalqueEntry { LiteralDanish = "sky",
                    Alternatives = { "cloud" }, Why = "himmel-sky" },
            },
        });

        sys.Should().Contain("- barn → barne-Claude, child  | menneskeunge");
        sys.Should().Contain("- sky → cloud  | himmel-sky");
    }

    [Fact]
    public void Build_metaCarriesCalqueCount()
    {
        var bundle = WritingScorePrompt.Build(new WritingScorePrompt.Request
        {
            SourcePath = "/tmp/x.md", Content = "x", Channel = "blog",
            Calques = new[] { new CalqueEntry { LiteralDanish = "barn" } },
        });

        // serializer-agnostic check — re-serialize meta to JSON and assert.
        var json = System.Text.Json.JsonSerializer.Serialize(bundle.Meta);
        json.Should().Contain("\"calquesIncluded\":1");
    }
}
