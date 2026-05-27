using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Models;
using Xunit;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// Verifies <see cref="BrainCommitPlanner"/> uses the firehose graph
/// (<c>~/.pks-cli/brain/files.jsonl</c> + <c>prompts.jsonl</c>) as its primary
/// data path, falling back to the per-file <see cref="BrainSessionScanner"/>
/// only when the firehose is absent or <c>--force-scan</c> is supplied.
///
/// Fixture pattern: write firehose rows into a temp home-dir-shaped tree using
/// <see cref="BrainIndexStore.FirehoseJsonOptions"/>, then point a
/// <see cref="BrainPathResolver"/> at it.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class BrainCommitPlannerFirehoseTests : IDisposable
{
    private readonly string _home;
    private readonly BrainPathResolver _paths;
    private readonly FirehoseReader _firehose;
    private readonly StubIngestPipeline _ingest;
    private readonly BrainCommitPlanner _planner;
    private readonly string _projectsDir; // for the scanner fallback

    public BrainCommitPlannerFirehoseTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "brain-firehose-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, ".pks-cli", "brain"));
        _projectsDir = Path.Combine(_home, ".claude", "projects");
        Directory.CreateDirectory(Path.Combine(_projectsDir, "proj1"));

        _paths = new BrainPathResolver(_home);
        _firehose = new FirehoseReader(_paths);
        _ingest = new StubIngestPipeline();
        _planner = new BrainCommitPlanner(new BrainSessionScanner(), _firehose, _paths, _ingest);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); }
        catch { /* best-effort */ }
    }

    private void AppendFileOps(params FileOpRow[] rows)
    {
        var path = _paths.GlobalFirehose(BrainFirehose.Files);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(JsonSerializer.Serialize(r, BrainIndexStore.FirehoseJsonOptions));
            sb.Append('\n');
        }
        File.AppendAllText(path, sb.ToString());
    }

    private void AppendPrompts(params PromptRow[] rows)
    {
        var path = _paths.GlobalFirehose(BrainFirehose.Prompts);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(JsonSerializer.Serialize(r, BrainIndexStore.FirehoseJsonOptions));
            sb.Append('\n');
        }
        File.AppendAllText(path, sb.ToString());
    }

    private static FileOpRow Op(string sess, string file, DateTime ts, string op = "edit") =>
        new()
        {
            SessionId = sess,
            ProjectSlug = "proj1",
            TimestampUtc = ts,
            Op = op,
            FilePath = file,
            Success = true,
        };

    private static PromptRow Prompt(string sess, DateTime ts, string text) =>
        new()
        {
            SessionId = sess,
            ProjectSlug = "proj1",
            TimestampUtc = ts,
            Uuid = Guid.NewGuid().ToString(),
            Text = text,
            TextHash = text.GetHashCode().ToString("x"),
            Length = text.Length,
        };

    [Fact]
    public async Task tc_firehose_groups_files_by_session()
    {
        var now = DateTime.UtcNow;
        AppendFileOps(
            Op("sess-A", "/repo/f1.cs", now),
            Op("sess-A", "/repo/f2.cs", now),
            Op("sess-A", "/repo/f3.cs", now),
            Op("sess-B", "/repo/f4.cs", now));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs", "/repo/f4.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" });
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Ungrouped.Should().BeEquivalentTo(new[] { "/repo/f4.cs" });
    }

    [Fact]
    public async Task tc_firehose_read_op_does_not_count_as_editor()
    {
        // sess-A edits f1; sess-B only "reads" f1 (later). sess-A must remain the editor.
        var earlier = DateTime.UtcNow.AddMinutes(-10);
        var later = DateTime.UtcNow;
        AppendFileOps(
            Op("sess-A", "/repo/f1.cs", earlier, "edit"),
            Op("sess-A", "/repo/f2.cs", earlier, "edit"),
            Op("sess-B", "/repo/f1.cs", later, "read"));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs" });
    }

    [Fact]
    public async Task tc_firehose_two_groups_greedy()
    {
        var older = DateTime.UtcNow.AddDays(-1);
        var newer = DateTime.UtcNow;
        AppendFileOps(
            Op("sess-A", "/repo/f1.cs", newer),
            Op("sess-A", "/repo/f2.cs", newer),
            Op("sess-A", "/repo/f3.cs", newer),
            Op("sess-B", "/repo/f3.cs", older),
            Op("sess-B", "/repo/f4.cs", older),
            Op("sess-B", "/repo/f5.cs", older));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs", "/repo/f4.cs", "/repo/f5.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
        });

        result.Groups.Should().HaveCount(2);
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs", "/repo/f3.cs" });
        result.Groups[1].PrimarySession.Should().Be("sess-B");
        result.Groups[1].Files.Should().BeEquivalentTo(new[] { "/repo/f4.cs", "/repo/f5.cs" });
        result.Groups[1].SharedFiles.Should().ContainKey("/repo/f3.cs").WhoseValue.Should().Be(1);
    }

    [Fact]
    public async Task tc_firehose_include_prompts_binary_search_join()
    {
        // Prompt P1 at t=1, edit at t=2 ; Prompt P2 at t=3, edit at t=4.
        // Both prompts should attach to the group in chronological order.
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        AppendPrompts(
            Prompt("sess-A", t0.AddMinutes(1), "do X"),
            Prompt("sess-A", t0.AddMinutes(3), "now Y"));
        AppendFileOps(
            Op("sess-A", "/repo/f1.cs", t0.AddMinutes(2)),
            Op("sess-A", "/repo/f2.cs", t0.AddMinutes(4)));

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            IncludePrompts = true,
            AutoRefresh = false,
        });

        result.Groups.Should().HaveCount(1);
        var g = result.Groups[0];
        g.PrimarySession.Should().Be("sess-A");
        g.Prompts.Select(p => p.Text).Should().Equal("do X", "now Y");
    }

    [Fact]
    public async Task tc_firehose_missing_falls_back_to_scanner()
    {
        // Don't write a files.jsonl — instead write a raw JSONL the scanner can read.
        // Delete the empty firehose file so the firehose-path is skipped.
        // (We start with no firehose file at all.)
        var firehosePath = _paths.GlobalFirehose(BrainFirehose.Files);
        if (File.Exists(firehosePath)) File.Delete(firehosePath);

        var now = DateTime.UtcNow;
        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = now.ToString("o"),
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_1", name = "Edit", input = new { file_path = "/repo/f1.cs" } },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = now.ToString("o"),
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_2", name = "Edit", input = new { file_path = "/repo/f2.cs" } },
                    },
                },
            }),
        };
        File.WriteAllLines(Path.Combine(_projectsDir, "proj1", "sess-A.jsonl"), lines);

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].PrimarySession.Should().Be("sess-A");
        result.Groups[0].Files.Should().BeEquivalentTo(new[] { "/repo/f1.cs", "/repo/f2.cs" });
    }

    [Fact]
    public async Task tc_firehose_force_scan_bypasses_firehose()
    {
        // Set up CONFLICTING data: firehose says sess-X, scanner JSONL says sess-Y.
        var now = DateTime.UtcNow;
        AppendFileOps(
            Op("sess-X", "/repo/f1.cs", now),
            Op("sess-X", "/repo/f2.cs", now));

        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = now.ToString("o"),
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_1", name = "Edit", input = new { file_path = "/repo/f1.cs" } },
                    },
                },
            }),
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                timestamp = now.ToString("o"),
                message = new
                {
                    content = new object[]
                    {
                        new { type = "tool_use", id = "tu_2", name = "Edit", input = new { file_path = "/repo/f2.cs" } },
                    },
                },
            }),
        };
        File.WriteAllLines(Path.Combine(_projectsDir, "proj1", "sess-Y.jsonl"), lines);

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
            ForceScan = true,
        });

        result.Groups.Should().HaveCount(1);
        result.Groups[0].PrimarySession.Should().Be("sess-Y");
    }

    [Fact]
    public async Task tc_firehose_auto_refresh_invokes_ingest()
    {
        var now = DateTime.UtcNow;
        AppendFileOps(Op("sess-A", "/repo/f1.cs", now), Op("sess-A", "/repo/f2.cs", now));

        await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = true,
        });

        _ingest.RunCount.Should().Be(1);
    }

    [Fact]
    public async Task tc_firehose_no_refresh_skips_ingest()
    {
        var now = DateTime.UtcNow;
        AppendFileOps(Op("sess-A", "/repo/f1.cs", now), Op("sess-A", "/repo/f2.cs", now));

        await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = new[] { "/repo/f1.cs", "/repo/f2.cs" },
            ProjectsDir = _projectsDir,
            MinFiles = 2,
            AutoRefresh = false,
        });

        _ingest.RunCount.Should().Be(0);
    }

    private sealed class StubIngestPipeline : IBrainIngestPipeline
    {
        public int RunCount;

        public Task<IngestRun> RunAsync(IngestOptions options, IIngestProgress progress, CancellationToken ct = default)
        {
            RunCount++;
            return Task.FromResult(new IngestRun
            {
                RunId = Guid.NewGuid().ToString("N"),
                StartedAtUtc = DateTime.UtcNow,
                FinishedAtUtc = DateTime.UtcNow,
            });
        }
    }
}
