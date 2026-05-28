using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class NaturalnessPromptBuilderTests
{
    private readonly NaturalnessPromptBuilder _sut = new();

    [Fact]
    public async Task BuildAsync_includesProfileSection()
    {
        var bundle = await _sut.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = "/x/da.md",
            Content = "line one\nline two\n",
            Profile = "## profile body — terse, plain, technical",
        });
        bundle.System.Should().Contain("terse, plain, technical");
        bundle.System.Should().Contain("# Writer profile");
    }

    [Fact]
    public async Task BuildAsync_explainsEmptyPatterns_onFirstRun()
    {
        var bundle = await _sut.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = "/x/da.md", Content = "x\n",
        });
        bundle.System.Should().Contain("# Accepted patterns");
        bundle.System.Should().Contain("no accepted patterns yet");
    }

    [Fact]
    public async Task BuildAsync_injectsAcceptedPatterns_asFewShot()
    {
        var bundle = await _sut.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = "/x/da.md",
            Content = "x\n",
            Patterns = new[]
            {
                new NaturalnessPattern
                {
                    TriggerSummary = "long compound clause with foreign term",
                    AcceptedExample = "Det er en DAG — alle edges peger nedad.",
                    AcceptedCount = 3,
                },
            },
        });
        bundle.System.Should().Contain("long compound clause with foreign term");
        bundle.System.Should().Contain("DAG — alle edges peger nedad");
        bundle.System.Should().Contain("accepted 3×");
    }

    [Fact]
    public async Task BuildAsync_userPrompt_hasLineNumberedSource()
    {
        var bundle = await _sut.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = "/x/da.md",
            Content = "first\nsecond\nthird\n",
        });
        bundle.User.Should().Contain("   1  first");
        bundle.User.Should().Contain("   2  second");
        bundle.User.Should().Contain("   3  third");
    }

    [Fact]
    public async Task BuildAsync_schemaObject_includesPostAndCandidateKeys()
    {
        var bundle = await _sut.BuildAsync(new NaturalnessPromptRequest
        {
            SourcePath = "/x/da.md", Content = "x\n",
        });
        // Crude check via System.Text.Json round-trip
        var json = System.Text.Json.JsonSerializer.Serialize(bundle.Schema);
        json.Should().Contain("\"post\"");
        json.Should().Contain("\"candidates\"");
        json.Should().Contain("\"alternatives\"");
        json.Should().Contain("\"authorlikeness\"");
    }
}
