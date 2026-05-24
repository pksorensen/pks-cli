using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Brain;
using Xunit;

namespace PKS.CLI.Tests.Commands.Brain;

/// <summary>
/// Implements FT-12 (Brain) commit-plan command — group uncommitted files by
/// shared session origin to enable focused commits.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class BrainCommitPlanCommandTests : IDisposable
{
    private readonly string _projectsDir;
    private readonly BrainCommitPlanner _planner;

    public BrainCommitPlanCommandTests()
    {
        _projectsDir = Path.Combine(Path.GetTempPath(), "brain-commit-plan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectsDir, "proj1"));
        _planner = new BrainCommitPlanner(new BrainSessionScanner());
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectsDir)) Directory.Delete(_projectsDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private void WriteSession(string sessionId, params (string filePath, DateTime ts)[] edits)
    {
        var lines = edits.Select((e, i) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = e.ts.ToString("o"),
            message = new
            {
                content = new object[]
                {
                    new { type = "tool_use", id = $"tu_{sessionId}_{i}", name = "Edit", input = new { file_path = e.filePath } }
                }
            }
        }));
        var path = Path.Combine(_projectsDir, "proj1", sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
    }

    [Fact]
    public async Task tc_plan_groups_files_by_shared_session()
    {
        // session-A touches f1,f2,f3; session-B touches f4 alone.
        var now = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", now),
            ("/repo/f2.cs", now),
            ("/repo/f3.cs", now));
        WriteSession("sess-B", ("/repo/f4.cs", now));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs", "/repo/f4.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" });
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Ungrouped.Should().BeEquivalentTo(new[] { "/repo/f4.cs" });
    }

    [Fact]
    public async Task tc_plan_respects_min_files()
    {
        var now = DateTime.UtcNow;
        WriteSession("sess-A", ("/repo/f1.cs", now));
        WriteSession("sess-B", ("/repo/f2.cs", now));
        WriteSession("sess-C", ("/repo/f3.cs", now));
        var files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" };

        var defaultResult = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = files,
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });
        defaultResult.Groups.Should().BeEmpty();
        defaultResult.Ungrouped.Should().BeEquivalentTo(files);

        var minOneResult = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = files,
            ProjectsDir = _projectsDir,
            MinFiles = 1,
        });
        minOneResult.Groups.Should().HaveCount(3);
        minOneResult.Groups.Sum(g => g.Files.Count).Should().Be(3);
        minOneResult.Ungrouped.Should().BeEmpty();
    }

    [Fact]
    public async Task tc_plan_records_contributing_sessions()
    {
        // session-A touches all 3 files; session-B touches 2 of them.
        var now = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", now),
            ("/repo/f2.cs", now),
            ("/repo/f3.cs", now));
        WriteSession("sess-B",
            ("/repo/f1.cs", now),
            ("/repo/f2.cs", now));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });

        result.Groups.Should().HaveCount(1);
        var g = result.Groups[0];
        g.PrimarySession.Should().Be("sess-A");
        g.Files.Should().HaveCount(3);
        g.ContributingSessions.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SessionId = "sess-B", FileCount = 2 });
    }

    [Fact]
    public async Task tc_plan_greedy_no_double_assignment()
    {
        // session-A: f1,f2,f3 ; session-B: f3,f4,f5
        // A more recent -> A first; Group 1 = {f1,f2,f3} primary A.
        // B has unassigned {f4,f5} (>= min 2) -> Group 2 = {f4,f5} primary B.
        // f3 must NOT appear in Group 2 file list but in SharedFiles.
        var older = DateTime.UtcNow.AddDays(-1);
        var newer = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", newer),
            ("/repo/f2.cs", newer),
            ("/repo/f3.cs", newer));
        WriteSession("sess-B",
            ("/repo/f3.cs", older),
            ("/repo/f4.cs", older),
            ("/repo/f5.cs", older));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs", "/repo/f4.cs", "/repo/f5.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });

        result.Groups.Should().HaveCount(2);
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" });

        result.Groups[1].PrimarySession.Should().Be("sess-B");
        result.Groups[1].Files.Should().BeEquivalentTo(new[] { "/repo/f4.cs", "/repo/f5.cs" });
        result.Groups[1].Files.Should().NotContain("/repo/f3.cs");
        result.Groups[1].SharedFiles.Should().ContainKey("/repo/f3.cs")
            .WhoseValue.Should().Be(1);

        result.Ungrouped.Should().BeEmpty();
    }

    [Fact]
    public async Task tc_plan_jsonl_format_one_group_per_line()
    {
        var now = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", now),
            ("/repo/f2.cs", now));
        WriteSession("sess-B",
            ("/repo/f3.cs", now),
            ("/repo/f4.cs", now));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs", "/repo/f4.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });

        result.Groups.Should().HaveCount(2);

        // Mirror jsonl serialization done by the command.
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var lines = result.Groups.Select(g => JsonSerializer.Serialize(new
        {
            group_id = g.GroupId,
            kind = "group",
            files = g.Files,
            primary_session = g.PrimarySession,
            latest_timestamp = g.LatestTimestampUtc.ToString("o"),
        }, opts)).ToList();

        lines.Should().HaveCount(2);
        foreach (var line in lines)
        {
            line.Should().NotContain("\n");
            var doc = JsonDocument.Parse(line).RootElement;
            doc.GetProperty("kind").GetString().Should().Be("group");
            doc.GetProperty("files").GetArrayLength().Should().BeGreaterThan(0);
            doc.GetProperty("primary_session").GetString().Should().NotBeNullOrEmpty();
        }
    }
}
