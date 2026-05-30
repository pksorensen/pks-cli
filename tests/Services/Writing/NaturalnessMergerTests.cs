using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using PKS.Infrastructure.Services.Writing;
using PKS.Infrastructure.Services.Writing.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Writing;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class NaturalnessMergerTests : IDisposable
{
    private readonly string _home;
    private readonly WritingPathResolver _paths;
    private readonly NaturalnessPicksStore _store;
    private readonly NaturalnessMerger _sut;
    private readonly string _source;

    public NaturalnessMergerTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "n-merger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _paths = new WritingPathResolver(_home);
        _store = new NaturalnessPicksStore(_paths);
        _sut = new NaturalnessMerger(_paths, _store);

        var dir = Path.Combine(_home, "blog-posts", "x");
        Directory.CreateDirectory(dir);
        _source = Path.Combine(dir, "da.md");
        File.WriteAllText(_source, "stub");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    private static NaturalnessCandidatesFile MakeFile(string post, params (int line, string original, string issue)[] cands)
    {
        var f = new NaturalnessCandidatesFile { Post = post };
        int i = 0;
        foreach (var (line, original, issue) in cands)
        {
            i++;
            f.Candidates.Add(new NaturalnessCandidate
            {
                Id = $"c{i}",
                Line = line,
                Original = original,
                Issue = issue,
                Alternatives = new()
                {
                    new() { Label = "A", Text = $"{issue}-A", Rationale = "r", Authorlikeness = 0.6 },
                    new() { Label = "B", Text = $"{issue}-B", Rationale = "r", Authorlikeness = 0.7 },
                    new() { Label = "C", Text = $"{issue}-C", Rationale = "r", Authorlikeness = 0.5 },
                },
            });
        }
        return f;
    }

    [Fact]
    public void Merge_collapsesSameLine_fromTwoCritics()
    {
        var opus = MakeFile(_source, (30, "orig text", "opus-issue"));
        var gpt5 = MakeFile(_source, (30, "orig text", "gpt5-issue"));

        var merged = _sut.Merge(_source, new Dictionary<string, NaturalnessCandidatesFile>
        {
            ["opus"] = opus, ["gpt5"] = gpt5,
        });

        merged.Critics.Should().Equal("gpt5", "opus");
        merged.Candidates.Should().ContainSingle();
        var c = merged.Candidates[0];
        c.Line.Should().Be(30);
        c.CriticsFlagging.Should().Equal("gpt5", "opus");
        c.Issues.Should().HaveCount(2);
        c.Issues!.Select(i => i.Source).Should().BeEquivalentTo(new[] { "opus", "gpt5" });
        // Default cap = 6; we have exactly 6 (3+3).
        c.Alternatives.Should().HaveCount(6);
        c.Alternatives.Select(a => a.Source).Distinct().Should().BeEquivalentTo(new[] { "opus", "gpt5" });
        // Ordering: by source then label
        c.Alternatives.Select(a => $"{a.Source}:{a.Label}").Should().Equal(
            "gpt5:A", "gpt5:B", "gpt5:C", "opus:A", "opus:B", "opus:C");
    }

    [Fact]
    public void Merge_unionsDisjointLines()
    {
        var opus = MakeFile(_source, (10, "x", "io"), (20, "y", "io2"));
        var gpt5 = MakeFile(_source, (15, "z", "ig"));

        var merged = _sut.Merge(_source, new Dictionary<string, NaturalnessCandidatesFile>
        {
            ["opus"] = opus, ["gpt5"] = gpt5,
        });

        merged.Candidates.Select(c => c.Line).Should().Equal(10, 15, 20);
        merged.Candidates.Single(c => c.Line == 10).CriticsFlagging.Should().Equal("opus");
        merged.Candidates.Single(c => c.Line == 15).CriticsFlagging.Should().Equal("gpt5");
    }

    [Fact]
    public void Merge_singleCritic_passthrough()
    {
        var opus = MakeFile(_source, (5, "orig", "io"));
        var merged = _sut.Merge(_source, new Dictionary<string, NaturalnessCandidatesFile>
        {
            ["opus"] = opus,
        });
        merged.Critics.Should().Equal("opus");
        var c = merged.Candidates.Single();
        c.CriticsFlagging.Should().Equal("opus");
        c.Alternatives.Should().HaveCount(3);
        c.Alternatives.Should().OnlyContain(a => a.Source == "opus");
        c.Issue.Should().Be("io");
    }

    [Fact]
    public void Merge_capsAlternativesAtMax_droppingLowestAuthorlikeness()
    {
        // 3 critics → 9 alts on a shared line → cap to 4
        NaturalnessCandidatesFile MakeOne(string sfx, double[] al) => new()
        {
            Post = _source,
            Candidates = new()
            {
                new NaturalnessCandidate
                {
                    Id = "c1", Line = 7, Original = "orig", Issue = $"i-{sfx}",
                    Alternatives = new()
                    {
                        new() { Label = "A", Text = $"{sfx}A", Rationale = "r", Authorlikeness = al[0] },
                        new() { Label = "B", Text = $"{sfx}B", Rationale = "r", Authorlikeness = al[1] },
                        new() { Label = "C", Text = $"{sfx}C", Rationale = "r", Authorlikeness = al[2] },
                    },
                },
            },
        };
        var dict = new Dictionary<string, NaturalnessCandidatesFile>
        {
            ["opus"] = MakeOne("o", new[] { 0.9, 0.1, 0.5 }),
            ["gpt5"] = MakeOne("g", new[] { 0.8, 0.2, 0.6 }),
            ["haiku"] = MakeOne("h", new[] { 0.95, 0.05, 0.4 }),
        };
        var merged = _sut.Merge(_source, dict, maxAlternativesPerLine: 4);
        var c = merged.Candidates.Single();
        c.Alternatives.Should().HaveCount(4);
        c.Alternatives.Min(a => a.Authorlikeness).Should().BeGreaterOrEqualTo(0.6);
    }

    [Fact]
    public void Merge_idempotent_whenInputsUnchanged()
    {
        var opus = MakeFile(_source, (3, "x", "i"));
        var dict = new Dictionary<string, NaturalnessCandidatesFile> { ["opus"] = opus };
        var first = _sut.Merge(_source, dict);
        var second = _sut.Merge(_source, dict);
        first.Candidates.Should().HaveSameCount(second.Candidates);
        first.Candidates[0].Line.Should().Be(second.Candidates[0].Line);
        first.Critics.Should().Equal(second.Critics!);
    }

    [Fact]
    public async System.Threading.Tasks.Task MergeAsync_loadsPerCriticFilesFromDisk_andWritesCanonical()
    {
        var opus = MakeFile(_source, (4, "x", "io"));
        var gpt5 = MakeFile(_source, (4, "x", "ig"));
        await _store.SaveCandidatesAsync(_source, "opus", opus);
        await _store.SaveCandidatesAsync(_source, "gpt5", gpt5);

        var path = await _sut.MergeAsync(_source);
        File.Exists(path).Should().BeTrue();
        var loaded = await _store.LoadCandidatesAsync(_source);
        loaded.Should().NotBeNull();
        loaded!.Critics.Should().Equal("gpt5", "opus");
        loaded.Candidates.Single().CriticsFlagging.Should().Equal("gpt5", "opus");
    }

    [Fact]
    public void Merge_prefersLongerOriginal_andLogsWhenCriticsDisagree()
    {
        var opus = MakeFile(_source, (8, "short", "io"));
        var gpt5 = MakeFile(_source, (8, "a much longer original text", "ig"));
        var log = new List<string>();
        var merged = _sut.Merge(_source, new Dictionary<string, NaturalnessCandidatesFile>
        {
            ["opus"] = opus, ["gpt5"] = gpt5,
        }, debugLog: log);
        merged.Candidates[0].Original.Should().Be("a much longer original text");
        log.Should().NotBeEmpty();
    }
}
