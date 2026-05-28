using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class CalqueListParserTests
{
    [Fact]
    public void ParseCalques_handlesArrows_alternativeList_andWhyExplanation()
    {
        var input = """
            # comment
            barn → barne-Claude, underordnet, child | "barn" = menneskeunge, ikke en proces

            sky -> cloud
            """;

        var entries = AnglicismListParser.ParseCalques(input);

        entries.Should().HaveCount(2);
        entries[0].LiteralDanish.Should().Be("barn");
        entries[0].Alternatives.Should().Equal("barne-Claude", "underordnet", "child");
        entries[0].Why.Should().Be("\"barn\" = menneskeunge, ikke en proces");
        entries[1].LiteralDanish.Should().Be("sky");
        entries[1].Alternatives.Should().Equal("cloud");
        entries[1].Why.Should().BeNull();
    }

    [Fact]
    public void RenderThenParseCalques_isRoundtripSafe()
    {
        var original = new[]
        {
            new CalqueEntry { LiteralDanish = "tråd", Alternatives = { "thread", "arbejdstråd" }, Why = "stof" },
            new CalqueEntry { LiteralDanish = "barn", Alternatives = { "child" }, Why = "menneskeunge" },
        };
        var roundtripped = AnglicismListParser.ParseCalques(AnglicismListParser.RenderCalques(original));
        roundtripped.Should().BeEquivalentTo(original);
    }
}
