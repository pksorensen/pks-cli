using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.Infrastructure.Services.Brain;
using Xunit;

namespace PKS.CLI.Tests.Commands.Brain;

/// <summary>
/// Implements FT-12 (Brain) scan command — Session→ToolCall→File edge discovery.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class BrainScanFilepathTests : IDisposable
{
    private readonly string _projectsDir;
    private readonly BrainSessionScanner _scanner = new();

    public BrainScanFilepathTests()
    {
        _projectsDir = Path.Combine(Path.GetTempPath(), "brain-scan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_projectsDir, "proj1"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_projectsDir)) Directory.Delete(_projectsDir, recursive: true); }
        catch { /* test cleanup best-effort */ }
    }

    private string WriteSession(string sessionId, params string[] lines)
    {
        var path = Path.Combine(_projectsDir, "proj1", sessionId + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static string EditEntry(string filePath, string toolUseId = "tu_1", string toolName = "Edit", string? timestamp = null)
    {
        var ts = timestamp ?? DateTime.UtcNow.ToString("o");
        return JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = ts,
            message = new
            {
                content = new object[]
                {
                    new { type = "tool_use", id = toolUseId, name = toolName, input = new { file_path = filePath } }
                }
            }
        });
    }

    private static string BashEntry(string command, string toolUseId = "tu_b", string? timestamp = null)
    {
        var ts = timestamp ?? DateTime.UtcNow.ToString("o");
        return JsonSerializer.Serialize(new
        {
            type = "assistant",
            timestamp = ts,
            message = new
            {
                content = new object[]
                {
                    new { type = "tool_use", id = toolUseId, name = "Bash", input = new { command } }
                }
            }
        });
    }

    [Fact]
    public async Task tc_scan_finds_edit_tool_uses_on_target_file()
    {
        var target = "/tmp/foo/target.cs";
        WriteSession("sess-a",
            EditEntry(target),
            EditEntry("/tmp/foo/other.cs"));

        var result = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = target,
            ProjectsDir = _projectsDir,
            TargetIsDirectory = false,
        });

        result.Edges.Should().HaveCount(1);
        result.Edges[0].ToolName.Should().Be("Edit");
        result.Edges[0].FilePath.Should().Be(target);
        result.Edges[0].MatchKind.Should().Be("tool_input_file_path");
        result.Edges[0].SessionId.Should().Be("sess-a");
    }

    [Fact]
    public async Task tc_scan_skips_bash_by_default()
    {
        var target = "/tmp/foo/target.cs";
        WriteSession("sess-b", BashEntry($"cat {target}"));

        var withoutBash = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = target,
            ProjectsDir = _projectsDir,
            IncludeBash = false,
            TargetIsDirectory = false,
        });
        withoutBash.Edges.Should().BeEmpty();

        var withBash = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = target,
            ProjectsDir = _projectsDir,
            IncludeBash = true,
            TargetIsDirectory = false,
        });
        withBash.Edges.Should().HaveCount(1);
        withBash.Edges[0].MatchKind.Should().Be("bash_command_substring");
        withBash.Edges[0].ToolName.Should().Be("Bash");
    }

    [Fact]
    public async Task tc_scan_directory_target_matches_subpaths()
    {
        WriteSession("sess-c",
            EditEntry("/a/b/foo.cs"),
            EditEntry("/a/c/bar.cs"),
            EditEntry("/other/baz.cs"));

        var result = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = "/a/",
            ProjectsDir = _projectsDir,
            TargetIsDirectory = true,
        });

        result.Edges.Should().HaveCount(2);
        result.Edges.Select(e => e.FilePath).Should().BeEquivalentTo(new[] { "/a/b/foo.cs", "/a/c/bar.cs" });
    }

    [Fact]
    public async Task tc_scan_filters_by_since()
    {
        var target = "/tmp/foo/target.cs";
        var old = DateTime.UtcNow.AddDays(-30).ToString("o");
        var recent = DateTime.UtcNow.AddHours(-1).ToString("o");

        WriteSession("sess-old", EditEntry(target, "tu_old", timestamp: old));
        WriteSession("sess-new", EditEntry(target, "tu_new", timestamp: recent));

        var since = DateTime.UtcNow.AddDays(-7);
        var result = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = target,
            ProjectsDir = _projectsDir,
            SinceUtc = since,
            TargetIsDirectory = false,
        });

        result.Edges.Should().HaveCount(1);
        result.Edges[0].SessionId.Should().Be("sess-new");
    }

    [Fact]
    public async Task tc_scan_outputs_jsonl_format_one_edge_per_line()
    {
        var target = "/tmp/foo/target.cs";
        WriteSession("sess-j",
            EditEntry(target, "tu_1"),
            EditEntry(target, "tu_2", toolName: "Write"));

        var result = await _scanner.ScanAsync(new BrainScanOptions
        {
            TargetPath = target,
            ProjectsDir = _projectsDir,
            TargetIsDirectory = false,
        });
        result.Edges.Should().HaveCount(2);

        // Manually serialize as the command would in jsonl mode and verify shape.
        var lines = result.Edges.Select(e => JsonSerializer.Serialize(new
        {
            session_id = e.SessionId,
            jsonl_path = e.JsonlPath,
            timestamp = e.TimestampUtc.ToString("o"),
            tool_use_id = e.ToolUseId,
            tool_name = e.ToolName,
            file_path = e.FilePath,
            match_kind = e.MatchKind,
        })).ToList();

        lines.Should().HaveCount(2);
        foreach (var line in lines)
        {
            line.Should().NotContain("\n");
            var doc = JsonDocument.Parse(line).RootElement;
            doc.GetProperty("session_id").GetString().Should().Be("sess-j");
            doc.GetProperty("file_path").GetString().Should().Be(target);
            doc.GetProperty("tool_name").GetString().Should().BeOneOf("Edit", "Write");
            doc.GetProperty("match_kind").GetString().Should().Be("tool_input_file_path");
        }
    }
}
