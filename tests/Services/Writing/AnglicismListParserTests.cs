using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class AnglicismListParserTests
{
    [Fact]
    public void Parse_handlesUnicodeArrow_commaAlternatives_andOptionalNote()
    {
        var input = """
            # comment line
            deploye → udrulle, idriftsætte | verb form leaks through

            feature -> funktion
            """;

        var entries = AnglicismListParser.Parse(input);

        entries.Should().HaveCount(2);

        entries[0].English.Should().Be("deploye");
        entries[0].DanishAlternatives.Should().Equal("udrulle", "idriftsætte");
        entries[0].Note.Should().Be("verb form leaks through");

        entries[1].English.Should().Be("feature");
        entries[1].DanishAlternatives.Should().Equal("funktion");
        entries[1].Note.Should().BeNull();
    }

    [Fact]
    public void RenderThenParse_isRoundtripSafe()
    {
        var original = new[]
        {
            new AnglicismEntry { English = "tracke", DanishAlternatives = { "spore", "følge" } },
            new AnglicismEntry { English = "setuppe", DanishAlternatives = { "opsætte" }, Note = "n" },
        };

        var roundtripped = AnglicismListParser.Parse(AnglicismListParser.Render(original));

        // Render sorts alphabetically for deterministic file output, so we
        // assert set-equality on the parsed structure rather than ordering.
        roundtripped.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void ParseAllowlist_skipsCommentsAndBlanks_andDeduplicates()
    {
        var input = """
            # tech terms
            AppHost
            Aspire

            AppHost
            """;

        var terms = AnglicismListParser.ParseAllowlist(input);
        terms.Should().BeEquivalentTo("AppHost", "Aspire");
    }
}
