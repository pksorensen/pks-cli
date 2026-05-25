using System;
using System.IO;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingPathResolverTests : IDisposable
{
    private readonly string _home;
    private readonly WritingPathResolver _sut;

    public WritingPathResolverTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "writing-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _sut = new WritingPathResolver(_home);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void GlobalRoot_isUnderHomePksCli()
    {
        _sut.GlobalRoot.Should().Be(Path.Combine(_home, ".pks-cli", "writing"));
        _sut.GlobalProfilePath.Should().EndWith("profile.md");
        _sut.GlobalAnglicismsPath.Should().EndWith("anglicisms.txt");
        _sut.GlobalAllowlistPath.Should().EndWith("allowlist.txt");
    }

    [Fact]
    public void GlobalChannelRubricPath_composesUnderChannelsDir()
    {
        _sut.GlobalChannelRubricPath("blog").Should().Be(
            Path.Combine(_sut.GlobalChannelsDir, "blog.md"));
    }

    [Fact]
    public void ResolveProjectRoot_returnsPksWritingUnderGitRepo()
    {
        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var nested = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(nested);

        _sut.ResolveProjectRoot(nested).Should().Be(Path.Combine(repo, ".pks", "writing"));
    }

    [Fact]
    public void ResolveProjectRoot_returnsNull_whenNotInGitRepo()
    {
        var dir = Path.Combine(_home, "no-git");
        Directory.CreateDirectory(dir);
        _sut.ResolveProjectRoot(dir).Should().BeNull();
    }

    [Fact]
    public void ResolveProjectRoot_handlesGitFile_asInWorktrees()
    {
        var repo = Path.Combine(_home, "worktree");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, ".git"), "gitdir: ../main/.git/worktrees/x");

        _sut.ResolveProjectRoot(repo).Should().Be(Path.Combine(repo, ".pks", "writing"));
    }

    [Fact]
    public void ReportAndLearnSidecars_landIn_reviewSubfolder_nextToSource()
    {
        var src = Path.Combine(_home, "blog-posts", "foo", "da.md");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "x");

        var reviewDir = Path.Combine(_home, "blog-posts", "foo", "_review");
        _sut.ReviewDir(src).Should().Be(reviewDir);
        _sut.ReportSidecarMarkdownPath(src).Should().Be(Path.Combine(reviewDir, "da.WRITING-REPORT.md"));
        _sut.ReportSidecarJsonPath(src).Should().Be(Path.Combine(reviewDir, "da.WRITING-REPORT.json"));
        _sut.LearnSidecarMarkdownPath(src).Should().Be(Path.Combine(reviewDir, "da.LEARN.md"));
        _sut.LearnSidecarJsonPath(src).Should().Be(Path.Combine(reviewDir, "da.LEARN.json"));
    }

    [Fact]
    public void ProjectReportCachePath_isDeterministicForSameSource()
    {
        var projectRoot = Path.Combine(_home, "p", ".pks", "writing");
        var src = "/some/abs/path.md";
        var a = _sut.ProjectReportCachePath(projectRoot, src);
        var b = _sut.ProjectReportCachePath(projectRoot, src);
        a.Should().Be(b);
        Path.GetFileName(a).Should().EndWith(".json");
    }
}
