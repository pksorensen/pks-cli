using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Persona;
using PKS.Infrastructure.Services.Persona.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Persona;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class PersonaSessionStoreTests : IDisposable
{
    private readonly string _home;
    private readonly string _contentPath;
    private readonly PersonaSessionStore _sut;

    public PersonaSessionStoreTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "persona-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
        _contentPath = Path.Combine(_home, "da.md");
        File.WriteAllText(_contentPath, "# hello");
        _sut = new PersonaSessionStore(new PersonaPathResolver());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadAsync_missingSidecar_returnsEmptyFile()
    {
        var file = await _sut.LoadAsync(_contentPath, "da");

        file.Calls.Should().BeEmpty();
        file.TotalCalls.Should().Be(0);
        file.TotalCostUsd.Should().Be(0);
    }

    [Fact]
    public async Task AppendCallAsync_persistsAndRollsUpTotals()
    {
        await _sut.AppendCallAsync(_contentPath, "da", new PersonaSessionCall
        {
            PersonaId = "senior-ic",
            Rubric = "relevance",
            Model = "gpt-5.5",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01,
            DurationMs = 1200,
            Ok = true,
        });
        await _sut.AppendCallAsync(_contentPath, "da", new PersonaSessionCall
        {
            PersonaId = "senior-ic",
            Rubric = "resonance",
            Model = "gpt-5.5",
            InputTokens = 80,
            OutputTokens = 40,
            CostUsd = 0.02,
            DurationMs = 900,
            Ok = true,
        });

        var file = await _sut.LoadAsync(_contentPath, "da");

        file.TotalCalls.Should().Be(2);
        file.TotalInputTokens.Should().Be(180);
        file.TotalOutputTokens.Should().Be(90);
        file.TotalCostUsd.Should().BeApproximately(0.03, 1e-9);
        file.Calls.Should().HaveCount(2);

        var resolver = new PersonaPathResolver();
        var sidecar = resolver.SessionSidecarPath(_contentPath, "da");
        File.Exists(sidecar).Should().BeTrue();
    }

    [Fact]
    public async Task AppendCallAsync_perModel_scopesToDistinctSidecar()
    {
        await _sut.AppendCallAsync(_contentPath, "da", new PersonaSessionCall
        {
            PersonaId = "senior-ic",
            Rubric = "relevance",
            Model = "gpt-5.5",
            InputTokens = 10,
            OutputTokens = 5,
            CostUsd = 0.001,
            Ok = true,
        }, modelTag: "gpt-5.5");

        var scoped = await _sut.LoadAsync(_contentPath, "da", modelTag: "gpt-5.5");
        var shared = await _sut.LoadAsync(_contentPath, "da");

        scoped.TotalCalls.Should().Be(1);
        shared.TotalCalls.Should().Be(0);
    }
}
