using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using PKS.Infrastructure.Services.Brain;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PKS.Commands.Brain;

/// <summary>
/// Implements FT-12 (Brain) commit-plan command — group uncommitted files by
/// shared session origin to enable focused commits.
/// See .pks/brain/feature-specs/FT-012-*.md (when materialized).
/// </summary>
public class BrainCommitPlanSettings : BrainSettings
{
    [CommandOption("--files <FILES>")]
    [Description("Explicit list of file paths to plan groups for.")]
    public string[]? Files { get; set; }

    [CommandOption("--files-from <PATH>")]
    [Description("Read file paths (one per line) from the given file.")]
    public string? FilesFrom { get; set; }

    [CommandOption("--uncommitted")]
    [Description("Auto-detect changed + untracked files from `git status --porcelain` in cwd.")]
    public bool Uncommitted { get; set; }

    [CommandOption("--since")]
    [Description("Filter sessions by first-entry timestamp (ISO date).")]
    public string? Since { get; set; }

    [CommandOption("--min-files")]
    [Description("Groups must contain at least N files to qualify (default: 2).")]
    public int MinFiles { get; set; } = 2;

    [CommandOption("--include-bash")]
    [Description("Pass through to scanner — also match Bash tool_use entries.")]
    public bool IncludeBash { get; set; }

    [CommandOption("--projects-dir")]
    [Description("Override the Claude projects dir (default: ~/.claude/projects).")]
    public string? ProjectsDir { get; set; }

    [CommandOption("--format")]
    [Description("Output format: text (default), json, or jsonl.")]
    public string Format { get; set; } = "text";

    [CommandOption("--include-prompts")]
    [Description("Include user prompts that preceded the file edits per group (max 10).")]
    public bool IncludePrompts { get; set; }
}

public class BrainCommitPlanCommand : AsyncCommand<BrainCommitPlanSettings>
{
    private readonly IBrainCommitPlanner _planner;

    public BrainCommitPlanCommand(IBrainCommitPlanner planner)
    {
        _planner = planner;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, BrainCommitPlanSettings settings)
    {
        int sourceCount = (settings.Files is { Length: > 0 } ? 1 : 0)
                          + (!string.IsNullOrWhiteSpace(settings.FilesFrom) ? 1 : 0)
                          + (settings.Uncommitted ? 1 : 0);
        if (sourceCount != 1)
        {
            AnsiConsole.MarkupLine("[red]Specify exactly one of[/] --files, --files-from, --uncommitted.");
            return 1;
        }

        var format = (settings.Format ?? "text").ToLowerInvariant();
        if (format is not ("text" or "json" or "jsonl"))
        {
            AnsiConsole.MarkupLine($"[red]Unknown --format:[/] {settings.Format}");
            return 1;
        }

        DateTime? since = null;
        if (settings.Since is { Length: > 0 } s)
        {
            if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                AnsiConsole.MarkupLine($"[red]Could not parse --since:[/] {s}");
                return 1;
            }
            since = dt;
        }

        List<string> files;
        try
        {
            files = ResolveInputFiles(settings);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to resolve input files:[/] {ex.Message}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No input files to plan.[/]");
            return 0;
        }

        var projectsDir = settings.ProjectsDir ?? ResolveDefaultProjectsDir();

        var result = await _planner.PlanAsync(new BrainCommitPlanOptions
        {
            Files = files,
            ProjectsDir = projectsDir,
            IncludeBash = settings.IncludeBash,
            SinceUtc = since,
            MinFiles = settings.MinFiles,
            IncludePrompts = settings.IncludePrompts,
        });

        switch (format)
        {
            case "json": WriteJson(result); break;
            case "jsonl": WriteJsonl(result); break;
            default: WriteText(result); break;
        }
        return 0;
    }

    private static string ResolveDefaultProjectsDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects");
    }

    private static List<string> ResolveInputFiles(BrainCommitPlanSettings settings)
    {
        IEnumerable<string> raw;
        if (settings.Files is { Length: > 0 })
            raw = settings.Files;
        else if (!string.IsNullOrWhiteSpace(settings.FilesFrom))
            raw = File.ReadAllLines(settings.FilesFrom);
        else
            raw = ResolveUncommittedFiles();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var line in raw)
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var abs = Path.GetFullPath(trimmed);
            if (seen.Add(abs)) result.Add(abs);
        }
        return result;
    }

    private static IEnumerable<string> ResolveUncommittedFiles()
    {
        var repoRoot = RunGit("rev-parse", "--show-toplevel").Trim();
        if (string.IsNullOrEmpty(repoRoot))
            throw new InvalidOperationException("Not inside a git repository.");

        var stdout = RunGit("status", "--porcelain");

        var results = new List<string>();
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4) continue;
            var status = line.Substring(0, 2);
            var path = line.Substring(3);

            // Handle rename "R  old -> new" / "C  old -> new" - take the new path.
            var arrowIdx = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0) path = path.Substring(arrowIdx + 4);

            // Skip directory entries from "??" (we don't recurse for the smoke test).
            if (status == "??" && (path.EndsWith('/') || path.EndsWith('\\'))) continue;

            // git status --porcelain emits paths relative to the repo top-level.
            var abs = Path.GetFullPath(Path.Combine(repoRoot, path));
            if (Directory.Exists(abs)) continue; // skip dirs
            if (!File.Exists(abs)) continue;
            results.Add(abs);
        }
        return results;
    }

    private static string RunGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start `git {string.Join(' ', args)}`.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"`git {string.Join(' ', args)}` exited {proc.ExitCode}: {stderr}");
        return stdout;
    }

    private static void WriteText(BrainCommitPlanResult result)
    {
        if (result.Groups.Count == 0 && result.Ungrouped.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No groups produced.[/]");
            return;
        }

        foreach (var g in result.Groups)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Group {g.GroupId}[/] ([cyan]{g.Files.Count} files[/]) — primary session [yellow]{Shorten(g.PrimarySession)}[/], latest edge {g.LatestTimestampUtc:yyyy-MM-dd}");
            foreach (var f in g.Files)
                AnsiConsole.MarkupLine($"  {Markup.Escape(f)}");
            if (g.SharedFiles.Count > 0)
            {
                var sharedDesc = string.Join(", ", g.SharedFiles
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{Path.GetFileName(kv.Key)} → Group {kv.Value}"));
                AnsiConsole.MarkupLine($"  [grey]Shared with earlier groups: {Markup.Escape(sharedDesc)}[/]");
            }
            if (g.ContributingSessions.Count > 0)
            {
                var contribDesc = string.Join(", ", g.ContributingSessions
                    .Select(c => $"{Shorten(c.SessionId)} ({c.FileCount})"));
                AnsiConsole.MarkupLine($"  [grey]Contributing: {contribDesc}[/]");
            }
            if (g.Prompts.Count > 0)
            {
                AnsiConsole.MarkupLine("  [grey]Prompts that drove the edits:[/]");
                foreach (var p in g.Prompts)
                {
                    var line = p.Text.Replace("\r", " ").Replace("\n", " ");
                    AnsiConsole.MarkupLine($"    [grey][[{p.TimestampUtc:HH:mm:ss}]][/] {Markup.Escape(line)}");
                }
            }
            AnsiConsole.WriteLine();
        }

        if (result.Ungrouped.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Ungrouped[/] ([cyan]{result.Ungrouped.Count} files[/], single-file commits suggested)");
            foreach (var f in result.Ungrouped)
                AnsiConsole.MarkupLine($"  {Markup.Escape(f)}");
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(
            $"[grey]{result.InputFiles} input file(s); {result.ScannedSessions} session(s) matched.[/]");
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;

    private static void WriteJson(BrainCommitPlanResult result)
    {
        var payload = new
        {
            input_files = result.InputFiles,
            scanned_sessions = result.ScannedSessions,
            groups = result.Groups.Select(ToGroupDto),
            ungrouped = result.Ungrouped,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteJsonl(BrainCommitPlanResult result)
    {
        var opts = new JsonSerializerOptions { WriteIndented = false };
        foreach (var g in result.Groups)
            Console.WriteLine(JsonSerializer.Serialize(ToGroupDto(g), opts));
        if (result.Ungrouped.Count > 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                group_id = 0,
                kind = "ungrouped",
                files = result.Ungrouped,
            }, opts));
        }
    }

    private static object ToGroupDto(BrainCommitGroup g) => new
    {
        group_id = g.GroupId,
        kind = "group",
        files = g.Files,
        primary_session = g.PrimarySession,
        latest_timestamp = g.LatestTimestampUtc.ToString("o"),
        shared_files = g.SharedFiles.ToDictionary(kv => kv.Key, kv => kv.Value),
        contributing_sessions = g.ContributingSessions.Select(c => new
        {
            session_id = c.SessionId,
            file_count = c.FileCount,
        }),
        prompts = g.Prompts.Select(p => new
        {
            timestamp = p.TimestampUtc.ToString("o"),
            text = p.Text,
        }),
    };
}
