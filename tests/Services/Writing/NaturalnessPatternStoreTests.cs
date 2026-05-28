using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class NaturalnessPatternStoreTests : IDisposable
{
    private readonly string _home;
    private readonly NaturalnessPatternStore _sut;

    public NaturalnessPatternStoreTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "pks-naturalness-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        var paths = new WritingPathResolver(_home);
        Directory.CreateDirectory(paths.GlobalRoot);
        _sut = new NaturalnessPatternStore(paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadAll_returnsEmpty_whenStoreDoesNotExist()
    {
        (await _sut.LoadAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_appendsNewPattern()
    {
        await _sut.UpsertAsync(new NaturalnessPattern
        {
            TriggerSummary = "compound noun",
            AcceptedExample = "kort sætning",
            FirstSeenSource = "2026-05-28 / x.md:1",
        });
        var loaded = await _sut.LoadAllAsync();
        loaded.Should().ContainSingle()
            .Which.AcceptedExample.Should().Be("kort sætning");
    }

    [Fact]
    public async Task Upsert_bumpsCount_whenTriggerSummaryMatches()
    {
        await _sut.UpsertAsync(new NaturalnessPattern { TriggerSummary = "trigger X", AcceptedExample = "a" });
        await _sut.UpsertAsync(new NaturalnessPattern { TriggerSummary = "trigger X", AcceptedExample = "b" });
        await _sut.UpsertAsync(new NaturalnessPattern { TriggerSummary = "trigger X", AcceptedExample = "c" });

        var loaded = await _sut.LoadAllAsync();
        loaded.Should().ContainSingle()
            .Which.AcceptedCount.Should().Be(3);
    }

    [Fact]
    public async Task Upsert_keepsDistinctTriggers()
    {
        await _sut.UpsertAsync(new NaturalnessPattern { TriggerSummary = "X", AcceptedExample = "a" });
        await _sut.UpsertAsync(new NaturalnessPattern { TriggerSummary = "Y", AcceptedExample = "b" });
        (await _sut.LoadAllAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RenderMarkdown_returnsRawFile()
    {
        await _sut.UpsertAsync(new NaturalnessPattern
        {
            TriggerSummary = "compound noun",
            AcceptedExample = "kort dansk sætning",
        });
        var md = await _sut.RenderMarkdownAsync();
        md.Should().Contain("compound noun");
        md.Should().Contain("```pattern");
        md.Should().Contain("kort dansk sætning");
    }
}
