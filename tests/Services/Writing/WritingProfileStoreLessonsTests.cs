using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingProfileStoreLessonsAndReferencesTests : IDisposable
{
    private readonly string _home;
    private readonly WritingPathResolver _paths;
    private readonly WritingProfileStore _sut;

    public WritingProfileStoreLessonsAndReferencesTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "writing-lessons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _paths = new WritingPathResolver(_home);
        _sut = new WritingProfileStore(_paths);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task AppendLesson_writesHeaderOnce_andOneBulletPerCall()
    {
        await _sut.EnsureGlobalLayoutAsync();

        await _sut.AppendLessonAsync("Hook", "Don't open with 'In this post we…'", "/p/post-a.md");
        await _sut.AppendLessonAsync("Tone", "Avoid forced enthusiasm", "/p/post-b.md");

        var body = await File.ReadAllTextAsync(_paths.GlobalLessonsPath);
        body.Should().StartWith("# Writing lessons");
        body.Should().Contain("**[Hook]**").And.Contain("post-a.md");
        body.Should().Contain("**[Tone]**").And.Contain("post-b.md");
        // Header appears exactly once.
        var headerOccurrences = body.Split("# Writing lessons", StringSplitOptions.None).Length - 1;
        headerOccurrences.Should().Be(1);
    }

    [Fact]
    public async Task LoadReferenceSamples_ignoresReadme_andSortsByFilename()
    {
        await _sut.EnsureGlobalLayoutAsync();
        var dir = _paths.GlobalReferenceChannelDir("blog");
        await File.WriteAllTextAsync(Path.Combine(dir, "README.md"), "skip me");
        await File.WriteAllTextAsync(Path.Combine(dir, "post-02.md"), "second");
        await File.WriteAllTextAsync(Path.Combine(dir, "post-01.md"), "first");

        var samples = await _sut.LoadReferenceSamplesAsync("blog");

        samples.Should().HaveCount(2);
        samples[0].Id.Should().Be("post-01");
        samples[0].Content.Should().Be("first");
        samples[1].Id.Should().Be("post-02");
    }

    [Fact]
    public async Task LoadReferenceSamples_returnsEmpty_whenChannelFolderMissing()
    {
        var samples = await _sut.LoadReferenceSamplesAsync("nonexistent");
        samples.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadChannelRubric_returnsContent_whenPresent()
    {
        await _sut.EnsureGlobalLayoutAsync();
        var rubric = await _sut.LoadChannelRubricAsync("blog");
        rubric.Should().NotBeNullOrWhiteSpace();
        rubric!.Should().Contain("blog");
    }

    [Fact]
    public async Task LoadChannelRubric_returnsNull_whenMissing()
    {
        var rubric = await _sut.LoadChannelRubricAsync("nope-no-rubric-here");
        rubric.Should().BeNull();
    }
}
