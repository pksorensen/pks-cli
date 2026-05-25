using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingLinterTests
{
    private static readonly List<AnglicismEntry> SampleRules = new()
    {
        new() { English = "deploye",  DanishAlternatives = { "udrulle" } },
        new() { English = "feature",  DanishAlternatives = { "funktion" } },
        new() { English = "setuppe",  DanishAlternatives = { "opsætte" }, Note = "verb form" },
    };
    private static readonly HashSet<string> EmptyAllow = new();

    private readonly WritingLinter _sut = new();

    [Fact]
    public async Task Lint_flagsKnownAnglicism_withLineAndSuggestion()
    {
        var content = "Vi skal deploye den nye feature på fredag.\n";
        var findings = await _sut.LintAsync(content, SampleRules, EmptyAllow);

        findings.Should().HaveCount(2);

        var deploy = findings.Single(f => f.Match.Equals("deploye", System.StringComparison.OrdinalIgnoreCase));
        deploy.Line.Should().Be(1);
        deploy.Column.Should().BeGreaterThan(0);
        deploy.RuleId.Should().Be("Writing.Anglicisms");
        deploy.Suggestions.Should().Contain("udrulle");
    }

    [Fact]
    public async Task Lint_respectsWordBoundaries_andIgnoresPartialMatches()
    {
        // "featurelist" contains "feature" but should NOT match (no word boundary).
        var content = "Vi har en featurelist klar.";
        var findings = await _sut.LintAsync(content, SampleRules, EmptyAllow);
        findings.Should().BeEmpty();
    }

    [Fact]
    public async Task Lint_skipsTermsInFencedCodeBlocks()
    {
        var content =
            "Forklaring her.\n" +
            "```bash\n" +
            "deploye feature\n" +     // line 3 — inside fence, must NOT flag
            "```\n" +
            "Men `deploye` her i prosa skal heller ikke — det er inline-kode.\n" +
            "Derimod skal vi udrulle feature normalt.\n"; // line 6 — flags `feature`

        var findings = await _sut.LintAsync(content, SampleRules, EmptyAllow);

        findings.Should().OnlyContain(f => f.Line == 6);
        findings.Should().ContainSingle().Which.Match.Should().Be("feature");
    }

    [Fact]
    public async Task Lint_skipsAllowlistedTerms()
    {
        var rules = new List<AnglicismEntry>
        {
            new() { English = "agent", DanishAlternatives = { "agent" } },
        };
        var allow = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "agent" };

        var findings = await _sut.LintAsync("Vores agent virker.", rules, allow);
        findings.Should().BeEmpty();
    }
}
