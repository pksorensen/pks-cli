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
        // session-A is the last-editor of all 3 files; session-B also touched
        // 2 of them earlier (so it qualifies as a contributing — not primary — session).
        var earlier = DateTime.UtcNow.AddMinutes(-5);
        var later = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", later),
            ("/repo/f2.cs", later),
            ("/repo/f3.cs", later));
        WriteSession("sess-B",
            ("/repo/f1.cs", earlier),
            ("/repo/f2.cs", earlier));

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

    [Fact]
    public async Task tc_plan_uses_last_edit_author_heuristic()
    {
        // SEMANTIC TEST: ranking is "files this session is last-editor of",
        // not "files this session touched". Session A touched f1,f2,f3 at t=100;
        // session B re-edited f3 at t=200. Even though A "touched" 3 files,
        // last-editor counts are: A={f1,f2} (count=2), B={f3} (count=1).
        // With min-files=2, only A qualifies as primary.
        var t100 = DateTime.UtcNow.AddMinutes(-10);
        var t200 = DateTime.UtcNow;
        WriteSession("sess-A",
            ("/repo/f1.cs", t100),
            ("/repo/f2.cs", t100),
            ("/repo/f3.cs", t100));
        WriteSession("sess-B",
            ("/repo/f3.cs", t200));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs" });
        result.Groups[0].Files.Should().NotContain("/repo/f3.cs");

        // sess-B is the last editor of only 1 file → below min-files; never primary.
        result.Groups.Should().NotContain(g => g.PrimarySession == "sess-B");

        // f3 ungrouped: sess-B owns it as last-editor but doesn't reach the threshold.
        result.Ungrouped.Should().Contain("/repo/f3.cs");
    }

    [Fact]
    public async Task tc_plan_include_prompts_extracts_preceding_user_messages()
    {
        // JSONL with: user "do X" → assistant Edit(f1) → user "now Y" → assistant Edit(f2).
        var t1 = DateTime.UtcNow.AddMinutes(-10);
        var t2 = DateTime.UtcNow.AddMinutes(-9);
        var t3 = DateTime.UtcNow.AddMinutes(-8);
        var t4 = DateTime.UtcNow.AddMinutes(-7);
        const string f1 = "/repo/f1.cs";
        const string f2 = "/repo/f2.cs";

        var lines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                type = "user",
                timestamp = t1.ToString("o"),
                message = new { role = "user", content = "do X" },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = t2.ToString("o"),
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_1", name = "Edit", input = new { file_path = f1 } },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                type = "user",
                timestamp = t3.ToString("o"),
                message = new { role = "user", content = "now Y" },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = t4.ToString("o"),
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_2", name = "Edit", input = new { file_path = f2 } },
                    },
                },
            }),
        };
        File.WriteAllLines(Path.Combine(_projectsDir, "proj1", "sess-A.jsonl"), lines);

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { f1, f2 },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            IncludePrompts = true,
        });

        result.Groups.Should().HaveCount(1);
        var g = result.Groups[0];
        g.PrimarySession.Should().Be("sess-A");
        g.Prompts.Should().HaveCount(2);
        g.Prompts.Select(p => p.Text).Should().Equal("do X", "now Y");
    }
}
