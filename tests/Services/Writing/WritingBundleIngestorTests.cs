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
public sealed class WritingBundleIngestorTests : IDisposable
{
    private readonly string _home;
    private readonly WritingPathResolver _paths;
    private readonly WritingProfileStore _store;

    public WritingBundleIngestorTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "writing-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _paths = new WritingPathResolver(_home);
        _store = new WritingProfileStore(_paths);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    private const string SampleBundle = """
        {
          "version": 1,
          "generatedBy": "claude-cowork",
          "profile": "# Writer Profile\n\nReal answers here.\n",
          "anglicisms": [
            { "english": "ship", "danishAlternatives": ["udgive"], "note": "verb form" }
          ],
          "allowlist": ["AppHost", "vibecast"],
          "references": {
            "blog": [
              { "id": "post-01", "content": "# En rigtig dansk blogpost\n\nBrødtekst." },
              { "id": "post-02", "content": "# Endnu en\n\nKortere." }
            ]
          },
          "lessons": [
            { "dimension": "Hook", "lesson": "Aldrig start med 'I dette indlæg…'", "sourcePath": "/p/x.md" }
          ]
        }
        """;

    [Fact]
    public void ExtractJson_findsFencedJsonBlock_amidProse()
    {
        var input = "Sure, here is the bundle:\n\n```json\n{\"version\":1}\n```\n\nHope that helps!";
        var json = WritingBundleIngestor.ExtractJson(input);
        json.Should().Be("{\"version\":1}");
    }

    [Fact]
    public void ExtractJson_acceptsUntaggedFence_whenBodyStartsWithBrace()
    {
        var input = "```\n{\"version\":1}\n```";
        WritingBundleIngestor.ExtractJson(input).Should().Be("{\"version\":1}");
    }

    [Fact]
    public void ExtractJson_falsBackToRawObject_betweenFirstAndLastBrace()
    {
        var input = "prefix garbage {\"version\":1,\"profile\":\"x\"} trailing";
        WritingBundleIngestor.ExtractJson(input).Should().Contain("\"profile\":\"x\"");
    }

    [Fact]
    public void Parse_returnsBundle_onValidInput()
    {
        var (bundle, err) = WritingBundleIngestor.Parse(SampleBundle);
        err.Should().BeNull();
        bundle.Should().NotBeNull();
        bundle!.Version.Should().Be(1);
        bundle.Profile.Should().Contain("Real answers here");
        bundle.Anglicisms.Should().ContainSingle().Which.English.Should().Be("ship");
        bundle.References!["blog"].Should().HaveCount(2);
    }

    [Fact]
    public void Parse_toleratesAlternativeSchemaField_andCoworkInventedFieldNames()
    {
        // What cowork actually produced in the field — no `version`, uses
        // `schema`, `term`/`suggestion` instead of `english`/`danishAlternatives`,
        // `text` instead of `content`, extra fields like `severity`, `source`, `words`.
        var cowork = """
        {
          "schema": "pks-writing-v1",
          "profile": "# Profile\n…",
          "anglicisms": [
            { "term": "deploye", "severity": "warning",
              "suggestion": "udrulning / sætte i drift", "note": "verb form" }
          ],
          "allowlist": ["AppHost"],
          "references": {
            "linkedin": [
              { "id": "post-01", "source": "linkedin", "date": "2026-04-02",
                "text": "Real post body", "words": 314 }
            ]
          }
        }
        """;

        var (bundle, err) = WritingBundleIngestor.Parse(cowork);
        err.Should().BeNull();
        bundle.Should().NotBeNull();
        bundle!.Anglicisms.Should().ContainSingle().Which.English.Should().Be("deploye");
        bundle.Anglicisms![0].DanishAlternatives.Should().Equal("udrulning", "sætte i drift");
        bundle.References!["linkedin"][0].Id.Should().Be("post-01");
        bundle.References["linkedin"][0].Content.Should().Be("Real post body");
    }

    [Fact]
    public async Task Apply_writesProfileOverTemplate_addsLists_andCreatesReferences()
    {
        await _store.EnsureGlobalLayoutAsync();
        var (bundle, _) = WritingBundleIngestor.Parse(SampleBundle);

        var result = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: false);

        result.ProfileWritten.Should().BeTrue(
            "the seed profile is the untouched template (contains <!-- INTERVIEWER:), so it gets replaced");
        result.AnglicismsAdded.Should().Be(1);
        result.AllowlistAdded.Should().Be(2);
        result.ReferencesAdded["blog"].Should().Be(2);
        result.LessonsAppended.Should().Be(1);

        File.Exists(Path.Combine(_paths.GlobalReferenceChannelDir("blog"), "post-01.md")).Should().BeTrue();
        (await File.ReadAllTextAsync(_paths.GlobalProfilePath)).Should().Contain("Real answers here");
        (await File.ReadAllTextAsync(_paths.GlobalAllowlistPath)).Should().Contain("AppHost");
        (await File.ReadAllTextAsync(_paths.GlobalAnglicismsPath)).Should().Contain("ship");
        (await File.ReadAllTextAsync(_paths.GlobalLessonsPath)).Should().Contain("Aldrig start med");
    }

    [Fact]
    public async Task Apply_skipsExistingNonTemplateProfile_withoutForce()
    {
        await _store.EnsureGlobalLayoutAsync();
        // Replace the template with a real-looking profile so it's NOT the template.
        await File.WriteAllTextAsync(_paths.GlobalProfilePath, "# My real profile\n\nDon't touch this.");

        var (bundle, _) = WritingBundleIngestor.Parse(SampleBundle);

        var result = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: false);
        result.ProfileSkipped.Should().BeTrue();
        result.ProfileWritten.Should().BeFalse();
        (await File.ReadAllTextAsync(_paths.GlobalProfilePath)).Should().Contain("Don't touch this");

        var forced = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: true);
        forced.ProfileWritten.Should().BeTrue();
        (await File.ReadAllTextAsync(_paths.GlobalProfilePath)).Should().Contain("Real answers here");
    }

    [Fact]
    public async Task Apply_skipsExistingReferenceSamples_unlessForce()
    {
        await _store.EnsureGlobalLayoutAsync();
        var (bundle, _) = WritingBundleIngestor.Parse(SampleBundle);

        // First run creates them.
        var r1 = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: false);
        r1.ReferencesAdded["blog"].Should().Be(2);

        // Second run finds them present, skips by default.
        var r2 = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: false);
        r2.ReferencesAdded.GetValueOrDefault("blog").Should().Be(0);
        r2.ReferencesSkipped["blog"].Should().Be(2);

        // With --force, they're overwritten.
        var r3 = await WritingBundleIngestor.ApplyAsync(bundle!, _paths, _store, force: true);
        r3.ReferencesAdded["blog"].Should().Be(2);
    }
}
