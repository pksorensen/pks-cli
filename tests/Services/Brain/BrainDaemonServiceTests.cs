using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services.Brain;
using PKS.Infrastructure.Services.Brain.Asf;
using PKS.Infrastructure.Services.Runner;
using Xunit;
using ProcessResult = PKS.Infrastructure.Services.Runner.ProcessResult;

namespace PKS.CLI.Tests.Services.Brain;

/// <summary>
/// The daily job. Nobody watches it run, so the tests stand in for the eyes:
/// the generated script must survive a failing step, the timer must catch up
/// after a closed laptop, and `status` must answer from the manifest rather than
/// from the scheduler — a timer that fires nightly and uploads nothing looks
/// perfectly healthy to systemd.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public class BrainDaemonServiceTests : TestBase
{
    private static DaemonOptions Options(string level = AsfLevel.Full, bool ingest = true) => new()
    {
        Level = level,
        Endpoint = "https://agentics.dk",
        At = new TimeOnly(3, 30),
        IncludeIngest = ingest,
        ExecutablePath = "/usr/local/bin/pks",
    };

    // ── the script ────────────────────────────────────────────────────────────

    [Fact]
    public void The_shell_script_runs_all_three_steps_even_when_one_fails()
    {
        var script = BrainDaemonService.BuildShellScript("/usr/local/bin/pks", Options(), "/home/p/.pks-cli/brain/daemon/daemon.log");

        script.Should().StartWith("#!/usr/bin/env bash");
        script.Should().Contain("/usr/local/bin/pks brain ingest || status=$?");
        script.Should().Contain("brain export --level all --quiet || status=$?");
        script.Should().Contain("brain push --endpoint https://agentics.dk --quiet || status=$?");
        script.Should().NotContain("brain ingest &&", "chaining would let a failed ingest skip the export that rescues opencode's spill files");
        script.Should().NotContain("brain export --level all --quiet &&");
        script.Should().Contain("exit $status", "the scheduler must see the failure");
    }

    [Fact]
    public void The_script_rotates_its_log_so_last_night_survives_tonight()
    {
        var script = BrainDaemonService.BuildShellScript("pks", Options(), "/var/log/brain.log");

        script.Should().Contain("mv -f \"$LOG\" \"$LOG.1\"");
        script.Should().Contain("exec >\"$LOG\" 2>&1");
    }

    [Theory]
    [InlineData(AsfLevel.Full, "--level all")]
    [InlineData(AsfLevel.Prompts, "--level prompts")]
    [InlineData(AsfLevel.Metrics, "--level metrics")]
    public void The_script_spells_the_level_the_way_the_cli_parses_it(string level, string expected) =>
        BrainDaemonService.BuildShellScript("pks", Options(level), "/l").Should().Contain(expected);

    [Fact]
    public void No_ingest_means_backup_only()
    {
        var script = BrainDaemonService.BuildShellScript("pks", Options(ingest: false), "/l");

        script.Should().NotContain("brain ingest");
        script.Should().Contain("brain export");
        script.Should().Contain("brain push");
    }

    [Fact]
    public void The_windows_script_runs_the_same_three_steps()
    {
        var script = BrainDaemonService.BuildWindowsScript(@"C:\tools\pks.exe", Options(), @"C:\logs\brain.log");

        script.Should().Contain("brain ingest");
        script.Should().Contain("brain export --level all --quiet");
        script.Should().Contain("brain push --endpoint \"https://agentics.dk\" --quiet");
        script.Should().Contain("\r\n", "cmd.exe needs CRLF");
    }

    // ── the schedules ─────────────────────────────────────────────────────────

    [Fact]
    public void The_timer_catches_up_after_a_closed_laptop()
    {
        var timer = BrainDaemonService.BuildSystemdTimer(new TimeOnly(3, 30));

        timer.Should().Contain("OnCalendar=*-*-* 03:30:00");
        timer.Should().Contain("Persistent=true", "a missed day is a day of opencode tool output lost forever");
        timer.Should().Contain("WantedBy=timers.target");
    }

    [Fact]
    public void The_service_unit_points_at_the_generated_script()
    {
        var unit = BrainDaemonService.BuildSystemdService("/home/p/.pks-cli/brain/daemon/brain-daily.sh");

        unit.Should().Contain("Type=oneshot");
        unit.Should().Contain("ExecStart=/home/p/.pks-cli/brain/daemon/brain-daily.sh");
    }

    [Fact]
    public void The_launch_agent_asks_for_the_requested_time_and_not_for_boot()
    {
        var plist = BrainDaemonService.BuildLaunchAgent("/s.sh", new TimeOnly(4, 5), "/l");

        plist.Should().Contain("<key>Hour</key><integer>4</integer><key>Minute</key><integer>5</integer>");
        plist.Should().Contain("<key>RunAtLoad</key><false/>");
        plist.Should().Contain($"<string>{BrainDaemonService.LaunchdLabel}</string>");
    }

    [Fact]
    public void The_cron_line_is_minute_then_hour()
    {
        BrainDaemonService.CronLine("/s.sh", new TimeOnly(3, 30)).Should().Be("30 3 * * * /s.sh");
        BrainDaemonService.CronLine("/s.sh", new TimeOnly(23, 5)).Should().Be("5 23 * * * /s.sh");
    }

    [Fact]
    public void Schtasks_gets_a_quoted_task_name_and_only_forces_when_asked()
    {
        BrainDaemonService.SchTasksCreateArgs(@"C:\s.cmd", new TimeOnly(3, 30), force: false)
            .Should().Be($"/create /tn \"{BrainDaemonService.TaskName}\" /tr C:\\s.cmd /sc daily /st 03:30");

        BrainDaemonService.SchTasksCreateArgs(@"C:\s.cmd", new TimeOnly(3, 30), force: true)
            .Should().EndWith(" /f");
    }

    // ── plan and install ──────────────────────────────────────────────────────

    [Fact]
    public void Plan_writes_nothing()
    {
        var (paths, home) = Paths();
        var daemon = new BrainDaemonService(paths, new FakeProcessRunner());

        var plan = daemon.Plan(Options());

        plan.ScriptPath.Should().StartWith(paths.GlobalRoot);
        plan.ScriptBody.Should().Contain("brain push");
        Directory.Exists(Path.Combine(home, ".pks-cli")).Should().BeFalse("a plan is a preview");
    }

    [Fact]
    public async Task Installing_via_cron_writes_an_executable_script_and_merges_the_crontab()
    {
        if (!OperatingSystem.IsLinux()) return; // systemd/launchd write outside the fake home.

        var (paths, _) = Paths();
        var runner = new FakeProcessRunner();
        runner.Reply("crontab", "-l", new ProcessResult(0, "0 5 * * * /usr/bin/backup-something-else\n", ""));
        var daemon = new BrainDaemonService(paths, runner);

        using var _scope = new PathScope(fakeCommands: new[] { "crontab" }, clearUserSystemd: true);
        var result = await daemon.InstallAsync(Options());

        result.Scheduler.Should().Be(DaemonScheduler.Cron);
        result.Ok.Should().BeTrue();
        result.ManualStep.Should().BeNull();

        var script = Path.Combine(paths.GlobalRoot, "daemon", "brain-daily.sh");
        File.Exists(script).Should().BeTrue();
        File.GetUnixFileMode(script).Should().HaveFlag(UnixFileMode.UserExecute, "cron will exec it directly");

        // The crontab handed to `crontab <file>` is deleted on success, so assert
        // on what was written before the install command ran.
        runner.Calls.Should().Contain(c => c.Command == "crontab" && c.Arguments.Contains("crontab.pending"));
        var written = runner.Captured["crontab.pending"];
        written.Should().Contain("/usr/bin/backup-something-else", "someone else's cron entries are not ours to delete");
        written.Should().Contain(BrainDaemonService.CronMarker);
        written.Should().Contain("30 3 * * *");
    }

    // ── status ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_reports_the_backlog_from_the_manifest_not_from_the_timer()
    {
        var (paths, _) = Paths();
        var uploaded = DateTimeOffset.Parse("2026-08-02T02:00:00Z");
        var manifest = new ExportManifest
        {
            Endpoint = "https://agentics.dk",
            Chunks =
            {
                new ChunkManifest { ChunkHash = "a", UploadedAt = uploaded, SyncId = "sy_1" },
                new ChunkManifest { ChunkHash = "b" },
                new ChunkManifest { ChunkHash = "c" },
            },
            Blobs =
            {
                new BlobRecord { Sha = "d" },
                new BlobRecord { Sha = "e", PrunedAt = DateTimeOffset.UtcNow },
            },
        };
        Directory.CreateDirectory(paths.ExportRoot);
        File.WriteAllText(paths.ExportManifestPath, JsonSerializer.Serialize(manifest, CanonicalJson.SerializerOptions));

        var status = await new BrainDaemonService(paths, new FakeProcessRunner()).StatusAsync();

        status.Endpoint.Should().Be("https://agentics.dk");
        status.ChunksUploaded.Should().Be(1);
        status.ChunksPending.Should().Be(2);
        status.BlobsPending.Should().Be(1, "a pruned blob is gone, not pending");
        status.LastUpload.Should().Be(uploaded);
    }

    [Fact]
    public async Task Status_survives_a_half_written_manifest()
    {
        var (paths, _) = Paths();
        Directory.CreateDirectory(paths.ExportRoot);
        File.WriteAllText(paths.ExportManifestPath, "{\"chunks\": [{\"chunkHa");

        var status = await new BrainDaemonService(paths, new FakeProcessRunner()).StatusAsync();

        status.ChunksPending.Should().Be(0);
        status.Endpoint.Should().BeNull();
    }

    [Fact]
    public async Task Status_says_not_installed_before_anything_is_installed()
    {
        var (paths, _) = Paths();

        var status = await new BrainDaemonService(paths, new FakeProcessRunner()).StatusAsync();

        status.Installed.Should().BeFalse();
        status.LastUpload.Should().BeNull();
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private (BrainPathResolver Paths, string Home) Paths()
    {
        var home = CreateTempDirectory();

        return (new BrainPathResolver(home), home);
    }

    /// Makes `crontab` look installed without touching the real one: the detector
    /// only checks PATH for the file, and every actual invocation goes through
    /// the faked IProcessRunner.
    private sealed class PathScope : IDisposable
    {
        private readonly string? _path = Environment.GetEnvironmentVariable("PATH");
        private readonly string? _runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "pks-brain-path-" + Guid.NewGuid().ToString("N")[..8]);

        public PathScope(IEnumerable<string> fakeCommands, bool clearUserSystemd)
        {
            Directory.CreateDirectory(_dir);
            foreach (var command in fakeCommands) File.WriteAllText(Path.Combine(_dir, command), "");
            Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + _path);
            if (clearUserSystemd) Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PATH", _path);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", _runtimeDir);
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public readonly List<(string Command, string Arguments)> Calls = new();

        /// Contents of any file passed as the sole argument, captured before the
        /// caller deletes it. Keyed by file name.
        public readonly Dictionary<string, string> Captured = new(StringComparer.Ordinal);

        private readonly Dictionary<string, ProcessResult> _replies = new(StringComparer.Ordinal);

        public void Reply(string command, string arguments, ProcessResult result) =>
            _replies[$"{command} {arguments}"] = result;

        public Task<ProcessResult> RunAsync(string command, string arguments, string? workingDirectory = null, CancellationToken ct = default)
        {
            Calls.Add((command, arguments));

            var candidate = arguments.Trim('"');
            if (candidate.Length > 0 && File.Exists(candidate))
                Captured[Path.GetFileName(candidate)] = File.ReadAllText(candidate);

            return Task.FromResult(_replies.TryGetValue($"{command} {arguments}", out var reply)
                ? reply
                : new ProcessResult(0, "", ""));
        }
    }
}
