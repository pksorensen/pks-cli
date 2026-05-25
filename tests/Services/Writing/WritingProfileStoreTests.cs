using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class WritingProfileStoreTests : IDisposable
{
    private readonly string _home;
    private readonly WritingPathResolver _paths;
    private readonly WritingProfileStore _sut;

    public WritingProfileStoreTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "writing-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _paths = new WritingPathResolver(_home);
        _sut = new WritingProfileStore(_paths);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task EnsureGlobalLayout_isIdempotent_andSeedsFiles()
    {
        await _sut.EnsureGlobalLayoutAsync();

        File.Exists(_paths.GlobalProfilePath).Should().BeTrue();
        File.Exists(_paths.GlobalAnglicismsPath).Should().BeTrue();
        File.Exists(_paths.GlobalAllowlistPath).Should().BeTrue();
        File.Exists(_paths.GlobalChannelRubricPath("blog")).Should().BeTrue();
        File.Exists(_paths.GlobalValeConfigPath).Should().BeTrue();

        var customProfile = "my own profile, leave me alone";
        await File.WriteAllTextAsync(_paths.GlobalProfilePath, customProfile);

        await _sut.EnsureGlobalLayoutAsync();

        (await File.ReadAllTextAsync(_paths.GlobalProfilePath)).Should().Be(customProfile);
    }

    [Fact]
    public async Task EnsureProjectLayout_createsFoldersAndChannelConfig_andUpdatesGitignore()
    {
        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var projectRoot = _paths.ResolveProjectRoot(repo)!;

        await _sut.EnsureProjectLayoutAsync(projectRoot);

        Directory.Exists(projectRoot).Should().BeTrue();
        Directory.Exists(Path.Combine(projectRoot, "overrides")).Should().BeTrue();
        Directory.Exists(_paths.ProjectReportsDir(projectRoot)).Should().BeTrue();
        File.Exists(_paths.ProjectChannelConfigPath(projectRoot)).Should().BeTrue();

        var gitignore = await File.ReadAllTextAsync(Path.Combine(repo, ".gitignore"));
        gitignore.Should().Contain(".pks/writing/");
        gitignore.Should().Contain("**/_review/", "every source folder's _review/ sidecar dir should be ignored");
    }

    [Fact]
    public async Task EnsureProjectLayout_doesNotDuplicateGitignoreEntry()
    {
        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var projectRoot = _paths.ResolveProjectRoot(repo)!;

        await _sut.EnsureProjectLayoutAsync(projectRoot);
        await _sut.EnsureProjectLayoutAsync(projectRoot);

        var gitignore = await File.ReadAllTextAsync(Path.Combine(repo, ".gitignore"));
        var count = gitignore.Split('\n').Count(l => l.Trim() == ".pks/writing/");
        count.Should().Be(1);
    }

    [Fact]
    public async Task LoadAnglicisms_mergesProjectOverrides_overGlobal()
    {
        await _sut.EnsureGlobalLayoutAsync();

        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var projectRoot = _paths.ResolveProjectRoot(repo)!;
        await _sut.EnsureProjectLayoutAsync(projectRoot);

        // Project override: redefine `feature` and add a new term.
        var override_ = """
            feature → træk
            kustom → tilpasset
            """;
        var overridePath = _paths.ProjectOverridesAnglicismsPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        await File.WriteAllTextAsync(overridePath, override_);

        var merged = await _sut.LoadAnglicismsAsync(projectRoot);

        merged.Should().Contain(e => e.English == "kustom");
        merged.First(e => e.English == "feature").DanishAlternatives
            .Should().ContainSingle().Which.Should().Be("træk");
    }

    [Fact]
    public async Task AddAnglicism_persistsAndIsReadableAfterReload()
    {
        await _sut.EnsureGlobalLayoutAsync();

        await _sut.AddAnglicismAsync(new AnglicismEntry
        {
            English = "submitte",
            DanishAlternatives = new() { "indsende" },
            Note = "from learn pass",
        });

        var fresh = new WritingProfileStore(_paths);
        var all = await fresh.LoadAnglicismsAsync(projectRoot: null);
        all.Should().Contain(e => e.English == "submitte");
    }

    [Fact]
    public async Task AddAllowedTerm_persistsAndDeduplicates()
    {
        await _sut.EnsureGlobalLayoutAsync();
        await _sut.AddAllowedTermAsync("Foundry");
        await _sut.AddAllowedTermAsync("Foundry");

        var allow = await _sut.LoadAllowlistAsync();
        allow.Count(t => t == "Foundry").Should().Be(1);
    }

    [Fact]
    public async Task SaveReport_writesSidecarsNextToSource_andCachesInProject()
    {
        var repo = Path.Combine(_home, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var projectRoot = _paths.ResolveProjectRoot(repo)!;
        await _sut.EnsureProjectLayoutAsync(projectRoot);

        var src = Path.Combine(repo, "blog-posts", "x", "da.md");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        await File.WriteAllTextAsync(src, "# hej\n");

        var report = new WritingReport
        {
            SourcePath = src,
            Channel = "blog",
            Score = 73,
            Findings = { new WritingFinding { RuleId = "Writing.Anglicisms",
                Line = 1, Column = 3, Match = "deploye",
                Suggestions = { "udrulle" }, Message = "Anglicism" } },
        };

        await _sut.SaveReportAsync(src, report);

        File.Exists(_paths.ReportSidecarJsonPath(src)).Should().BeTrue();
        File.Exists(_paths.ReportSidecarMarkdownPath(src)).Should().BeTrue();
        File.Exists(_paths.ProjectReportCachePath(projectRoot, src)).Should().BeTrue();

        var reloaded = await _sut.LoadReportAsync(src);
        reloaded.Should().NotBeNull();
        reloaded!.Score.Should().Be(73);
        reloaded.Findings.Should().HaveCount(1);
        reloaded.Findings[0].Match.Should().Be("deploye");
    }
}
