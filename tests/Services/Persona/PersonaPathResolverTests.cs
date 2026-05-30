using System;
using System.IO;
using FluentAssertions;
using PKS.Infrastructure.Services.Persona;
using Xunit;

namespace PKS.CLI.Tests.Services.Persona;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class PersonaPathResolverTests : IDisposable
{
    private readonly string _home;
    private readonly PersonaPathResolver _sut = new();

    public PersonaPathResolverTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "persona-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void ScoresSidecar_default_isSharedFile()
    {
        var src = Path.Combine(_home, "blog-posts", "foo", "da.md");
        var reviewDir = Path.Combine(_home, "blog-posts", "foo", "_review");
        _sut.ScoresSidecarPath(src, "da")
            .Should().Be(Path.Combine(reviewDir, "da.PERSONA-SCORES.json"));
        // null/whitespace modelTag collapses to the shared file
        _sut.ScoresSidecarPath(src, "da", null)
            .Should().Be(Path.Combine(reviewDir, "da.PERSONA-SCORES.json"));
        _sut.ScoresSidecarPath(src, "da", "  ")
            .Should().Be(Path.Combine(reviewDir, "da.PERSONA-SCORES.json"));
    }

    [Fact]
    public void ScoresSidecar_perModel_scopesFilenameByModelSlug()
    {
        var src = Path.Combine(_home, "blog-posts", "x", "da.md");
        _sut.ScoresSidecarPath(src, "da", "gpt-5.5")
            .Should().EndWith(Path.Combine("_review", "da.PERSONA-SCORES.gpt-5-5.json"));
        _sut.ScoresSidecarPath(src, "da", "claude-opus-4-8")
            .Should().EndWith(Path.Combine("_review", "da.PERSONA-SCORES.claude-opus-4-8.json"));
        // gpt-5.5 and opus land in distinct files so they never upsert over each other
        _sut.ScoresSidecarPath(src, "da", "gpt-5.5")
            .Should().NotBe(_sut.ScoresSidecarPath(src, "da", "claude-opus-4-8"));
    }

    [Theory]
    [InlineData("gpt-5.5", "gpt-5-5")]
    [InlineData("claude-opus-4-8", "claude-opus-4-8")]
    [InlineData("GPT-5.5", "gpt-5-5")]
    [InlineData("  weird//model..id  ", "weird-model-id")]
    public void ModelTagSlug_isFilenameSafeAndStable(string model, string expected)
    {
        PersonaPathResolver.ModelTagSlug(model).Should().Be(expected);
    }
}
