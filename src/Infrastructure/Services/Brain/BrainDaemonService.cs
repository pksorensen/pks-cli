using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PKS.Infrastructure.Services.Brain.Asf;
using PKS.Infrastructure.Services.Runner;

namespace PKS.Infrastructure.Services.Brain;

/// Writes and activates the daily backup job. Spec: docs/specs/asf/05-blob-backup.md.
///
/// The generated script is the contract, not the scheduler. Every platform runs
/// the same three commands in the same order and logs to the same place, so
/// "did last night's backup work?" has one answer to look at regardless of
/// whether systemd, launchd, cron or Task Scheduler started it.
public sealed class BrainDaemonService(IBrainPathResolver paths, IProcessRunner processes) : IBrainDaemonService
{
    public const string UnitName = "pks-brain";
    public const string LaunchdLabel = "dk.agentics.pks-brain";
    public const string TaskName = "PKS Brain Daily Backup";
    public const string CronMarker = "# pks-brain (managed by `pks brain daemon`)";

    private string DaemonDir => Path.Combine(paths.GlobalRoot, "daemon");
    private string ScriptPath => Path.Combine(DaemonDir, IsWindows ? "brain-daily.cmd" : "brain-daily.sh");
    private string LogPath => Path.Combine(DaemonDir, "daemon.log");

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // ── Plan ─────────────────────────────────────────────────────────────────

    public DaemonPlan Plan(DaemonOptions options)
    {
        var scheduler = DetectScheduler();
        var exe = options.ExecutablePath ?? Environment.ProcessPath ?? "pks";
        var script = IsWindows
            ? BuildWindowsScript(exe, options, LogPath)
            : BuildShellScript(exe, options, LogPath);

        var plan = new DaemonPlan
        {
            Scheduler = scheduler,
            ScriptPath = ScriptPath,
            ScriptBody = script,
            LogPath = LogPath,
        };

        switch (scheduler)
        {
            case DaemonScheduler.Systemd:
                plan.Units.Add((SystemdPath($"{UnitName}.service"), BuildSystemdService(ScriptPath)));
                plan.Units.Add((SystemdPath($"{UnitName}.timer"), BuildSystemdTimer(options.At)));
                plan.Activation.Add("systemctl --user daemon-reload");
                plan.Activation.Add($"systemctl --user enable --now {UnitName}.timer");
                break;
            case DaemonScheduler.Launchd:
                plan.Units.Add((LaunchAgentPath(), BuildLaunchAgent(ScriptPath, options.At, LogPath)));
                plan.Activation.Add($"launchctl bootstrap gui/$UID {LaunchAgentPath()}");
                break;
            case DaemonScheduler.SchTasks:
                plan.Activation.Add($"schtasks /create {SchTasksCreateArgs(ScriptPath, options.At, options.Force)}");
                break;
            case DaemonScheduler.Cron:
                plan.Units.Add(("crontab", CronLine(ScriptPath, options.At)));
                plan.Activation.Add("crontab <merged file>");
                break;
        }

        return plan;
    }

    // ── Install ──────────────────────────────────────────────────────────────

    public async Task<DaemonResult> InstallAsync(DaemonOptions options, CancellationToken ct = default)
    {
        var plan = Plan(options);
        var result = new DaemonResult { Scheduler = plan.Scheduler, Ok = true };

        Directory.CreateDirectory(DaemonDir);
        WriteExecutable(plan.ScriptPath, plan.ScriptBody);
        result.Wrote.Add(plan.ScriptPath);

        foreach (var (path, body) in plan.Units.Where(u => u.Path != "crontab"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body, new UTF8Encoding(false));
            result.Wrote.Add(path);
        }

        switch (plan.Scheduler)
        {
            case DaemonScheduler.Systemd:
                await RunAsync(result, "systemctl", "--user daemon-reload", ct);
                if (!await RunAsync(result, "systemctl", $"--user enable --now {UnitName}.timer", ct))
                {
                    result.ManualStep = $"systemctl --user enable --now {UnitName}.timer";
                }
                break;

            case DaemonScheduler.Launchd:
                // bootout first so a re-install replaces rather than collides.
                await RunAsync(result, "launchctl", $"bootout gui/{Uid()}/{LaunchdLabel}", ct, tolerateFailure: true);
                if (!await RunAsync(result, "launchctl", $"bootstrap gui/{Uid()} {Quote(LaunchAgentPath())}", ct))
                {
                    result.ManualStep = $"launchctl bootstrap gui/$(id -u) {LaunchAgentPath()}";
                }
                break;

            case DaemonScheduler.SchTasks:
                if (!await RunAsync(result, "schtasks", SchTasksCreateArgs(plan.ScriptPath, options.At, options.Force), ct))
                {
                    result.ManualStep = $"schtasks /create {SchTasksCreateArgs(plan.ScriptPath, options.At, force: true)}";
                }
                break;

            case DaemonScheduler.Cron:
                await InstallCronAsync(result, plan.ScriptPath, options.At, ct);
                break;

            default:
                result.Ok = false;
                result.Problems.Add("No supported scheduler found (systemd, launchd, cron or schtasks).");
                break;
        }

        if (result.ManualStep is not null) result.Ok = false;

        return result;
    }

    // ── Status ───────────────────────────────────────────────────────────────

    public async Task<DaemonStatus> StatusAsync(CancellationToken ct = default)
    {
        var status = new DaemonStatus
        {
            Scheduler = DetectScheduler(),
            ScriptPath = ScriptPath,
            LogPath = LogPath,
            Installed = File.Exists(ScriptPath),
        };

        switch (status.Scheduler)
        {
            case DaemonScheduler.Systemd:
                status.Installed = status.Installed && File.Exists(SystemdPath($"{UnitName}.timer"));
                var enabled = await processes.RunAsync("systemctl", $"--user is-enabled {UnitName}.timer", null, ct);
                status.Enabled = enabled.ExitCode == 0;
                var timers = await processes.RunAsync("systemctl", "--user list-timers --all --no-pager", null, ct);
                status.NextRun = timers.StandardOutput
                    .Split('\n')
                    .FirstOrDefault(l => l.Contains(UnitName, StringComparison.Ordinal))
                    ?.Trim();
                break;

            case DaemonScheduler.Launchd:
                status.Installed = status.Installed && File.Exists(LaunchAgentPath());
                var list = await processes.RunAsync("launchctl", $"list {LaunchdLabel}", null, ct);
                status.Enabled = list.ExitCode == 0;
                break;

            case DaemonScheduler.SchTasks:
                var query = await processes.RunAsync("schtasks", $"/query /tn {Quote(TaskName)}", null, ct);
                status.Enabled = query.ExitCode == 0;
                status.Installed = status.Installed && status.Enabled;
                break;

            case DaemonScheduler.Cron:
                var crontab = await processes.RunAsync("crontab", "-l", null, ct);
                status.Enabled = crontab.ExitCode == 0 && crontab.StandardOutput.Contains(CronMarker, StringComparison.Ordinal);
                status.Installed = status.Installed && status.Enabled;
                break;
        }

        if (File.Exists(LogPath))
        {
            status.LastRun = File.GetLastWriteTime(LogPath).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            status.LogTail = Tail(LogPath, 20);
        }

        ReadManifestState(status);

        return status;
    }

    /// The manifest is the only honest answer to "is the backup working". A timer
    /// that fires nightly and pushes nothing looks healthy to the scheduler.
    private void ReadManifestState(DaemonStatus status)
    {
        var path = paths.ExportManifestPath;
        if (!File.Exists(path)) return;

        try
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize<ExportManifest>(
                File.ReadAllText(path), CanonicalJson.SerializerOptions);
            if (manifest is null) return;

            status.Endpoint = manifest.Endpoint;
            status.ChunksUploaded = manifest.Chunks.Count(c => c.UploadedAt is not null);
            status.ChunksPending = manifest.Chunks.Count - status.ChunksUploaded;
            status.BlobsPending = manifest.Blobs.Count(b => b.UploadedAt is null && b.PrunedAt is null);
            status.LastUpload = manifest.Chunks
                .Select(c => c.UploadedAt)
                .Concat(manifest.Blobs.Select(b => b.UploadedAt))
                .Where(t => t is not null)
                .DefaultIfEmpty(null)
                .Max();
        }
        catch (System.Text.Json.JsonException)
        {
            // A half-written manifest is the export's problem to report, not the
            // daemon's to crash on.
        }
    }

    // ── Uninstall ────────────────────────────────────────────────────────────

    public async Task<DaemonResult> UninstallAsync(CancellationToken ct = default)
    {
        var scheduler = DetectScheduler();
        var result = new DaemonResult { Scheduler = scheduler, Ok = true };

        switch (scheduler)
        {
            case DaemonScheduler.Systemd:
                await RunAsync(result, "systemctl", $"--user disable --now {UnitName}.timer", ct, tolerateFailure: true);
                RemoveFile(result, SystemdPath($"{UnitName}.timer"));
                RemoveFile(result, SystemdPath($"{UnitName}.service"));
                await RunAsync(result, "systemctl", "--user daemon-reload", ct, tolerateFailure: true);
                break;

            case DaemonScheduler.Launchd:
                await RunAsync(result, "launchctl", $"bootout gui/{Uid()}/{LaunchdLabel}", ct, tolerateFailure: true);
                RemoveFile(result, LaunchAgentPath());
                break;

            case DaemonScheduler.SchTasks:
                await RunAsync(result, "schtasks", $"/delete /tn {Quote(TaskName)} /f", ct, tolerateFailure: true);
                break;

            case DaemonScheduler.Cron:
                await RemoveCronAsync(result, ct);
                break;
        }

        // The script and its log stay. They are evidence of what ran, and the
        // next install rewrites the script anyway.
        return result;
    }

    // ── Script bodies (pure, so they can be asserted on) ──────────────────────

    /// Steps run unconditionally rather than chained with `&&`: a failing ingest
    /// must not stop the export, because the export is the part that rescues
    /// opencode's spill files before they are swept.
    public static string BuildShellScript(string exe, DaemonOptions options, string logPath)
    {
        var level = AsfLevel.Parse(options.Level);
        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\n");
        sb.Append("# Generated by `pks brain daemon install`. Edit freely — install overwrites it.\n");
        sb.Append("set -u\n\n");
        sb.Append($"LOG={Quote(logPath)}\n");
        sb.Append("mkdir -p \"$(dirname \"$LOG\")\"\n");
        sb.Append("[ -f \"$LOG\" ] && mv -f \"$LOG\" \"$LOG.1\"\n");
        sb.Append("exec >\"$LOG\" 2>&1\n\n");
        sb.Append("echo \"=== pks brain daily backup: $(date -Is) ===\"\n");
        sb.Append("status=0\n\n");
        if (options.IncludeIngest)
            sb.Append($"{Quote(exe)} brain ingest || status=$?\n");
        sb.Append($"{Quote(exe)} brain export --level {CliLevel(level)} --quiet || status=$?\n");
        sb.Append($"{Quote(exe)} brain push --endpoint {Quote(options.Endpoint)} --quiet || status=$?\n\n");
        sb.Append("echo \"=== finished: $(date -Is) (exit $status) ===\"\n");
        sb.Append("exit $status\n");

        return sb.ToString();
    }

    public static string BuildWindowsScript(string exe, DaemonOptions options, string logPath)
    {
        var level = AsfLevel.Parse(options.Level);
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("rem Generated by `pks brain daemon install`. Edit freely - install overwrites it.\r\n");
        sb.Append($"set LOG=\"{logPath}\"\r\n");
        sb.Append("if exist %LOG% move /y %LOG% %LOG%.1 >nul\r\n");
        sb.Append("(\r\n");
        sb.Append("echo === pks brain daily backup: %DATE% %TIME% ===\r\n");
        if (options.IncludeIngest)
            sb.Append($"\"{exe}\" brain ingest\r\n");
        sb.Append($"\"{exe}\" brain export --level {CliLevel(level)} --quiet\r\n");
        sb.Append($"\"{exe}\" brain push --endpoint \"{options.Endpoint}\" --quiet\r\n");
        sb.Append("echo === finished: %DATE% %TIME% ===\r\n");
        sb.Append(") > %LOG% 2>&1\r\n");

        return sb.ToString();
    }

    public static string BuildSystemdService(string scriptPath) =>
        "[Unit]\n" +
        "Description=pks brain — daily agent session backup\n" +
        "Documentation=https://agentics.dk/docs/brain\n\n" +
        "[Service]\n" +
        "Type=oneshot\n" +
        $"ExecStart={scriptPath}\n";

    /// `Persistent=true` is the load-bearing line: a laptop that was asleep at
    /// 03:30 runs the job at the next boot instead of skipping a day, which is
    /// what keeps the 7-day opencode window from closing on a week of travel.
    public static string BuildSystemdTimer(TimeOnly at) =>
        "[Unit]\n" +
        "Description=Daily pks brain backup\n\n" +
        "[Timer]\n" +
        $"OnCalendar=*-*-* {at:HH\\:mm}:00\n" +
        "Persistent=true\n" +
        "RandomizedDelaySec=900\n\n" +
        "[Install]\n" +
        "WantedBy=timers.target\n";

    public static string BuildLaunchAgent(string scriptPath, TimeOnly at, string logPath) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n<dict>\n" +
        $"  <key>Label</key><string>{LaunchdLabel}</string>\n" +
        $"  <key>ProgramArguments</key><array><string>/bin/bash</string><string>{scriptPath}</string></array>\n" +
        "  <key>StartCalendarInterval</key>\n" +
        $"  <dict><key>Hour</key><integer>{at.Hour}</integer><key>Minute</key><integer>{at.Minute}</integer></dict>\n" +
        "  <key>RunAtLoad</key><false/>\n" +
        $"  <key>StandardErrorPath</key><string>{logPath}.launchd</string>\n" +
        "</dict>\n</plist>\n";

    public static string SchTasksCreateArgs(string scriptPath, TimeOnly at, bool force) =>
        $"/create /tn {Quote(TaskName)} /tr {Quote(scriptPath)} /sc daily /st {at:HH\\:mm}" + (force ? " /f" : "");

    public static string CronLine(string scriptPath, TimeOnly at) =>
        $"{at.Minute} {at.Hour} * * * {scriptPath}";

    // ── Scheduler plumbing ───────────────────────────────────────────────────

    private DaemonScheduler DetectScheduler()
    {
        if (IsWindows) return DaemonScheduler.SchTasks;
        if (IsMac) return DaemonScheduler.Launchd;

        // A user systemd instance needs both the binary and a running session
        // bus; a devcontainer usually has neither, and falls back to cron.
        if (Which("systemctl") && HasUserSystemd()) return DaemonScheduler.Systemd;
        if (Which("crontab")) return DaemonScheduler.Cron;

        return DaemonScheduler.None;
    }

    private static bool HasUserSystemd()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");

        return runtimeDir is { Length: > 0 } && Directory.Exists(Path.Combine(runtimeDir, "systemd"));
    }

    private static bool Which(string command) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(Path.Combine(dir, command)));

    private static string SystemdPath(string unit) =>
        Path.Combine(Home(), ".config", "systemd", "user", unit);

    private static string LaunchAgentPath() =>
        Path.Combine(Home(), "Library", "LaunchAgents", $"{LaunchdLabel}.plist");

    private static string Home() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Uid() =>
        Environment.GetEnvironmentVariable("UID") ?? "$(id -u)";

    private async Task InstallCronAsync(DaemonResult result, string scriptPath, TimeOnly at, CancellationToken ct)
    {
        var existing = await processes.RunAsync("crontab", "-l", null, ct);
        // A missing crontab exits non-zero with an empty listing; that is a fresh
        // install, not a failure.
        var lines = (existing.ExitCode == 0 ? existing.StandardOutput : "")
            .Split('\n')
            .Where(l => !l.Contains(CronMarker, StringComparison.Ordinal) && !l.Contains(scriptPath, StringComparison.Ordinal))
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        lines.Add(CronMarker);
        lines.Add(CronLine(scriptPath, at));

        var tmp = Path.Combine(DaemonDir, "crontab.pending");
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n", new UTF8Encoding(false));

        if (!await RunAsync(result, "crontab", Quote(tmp), ct))
            result.ManualStep = $"crontab {tmp}";
        else
            File.Delete(tmp);
    }

    private async Task RemoveCronAsync(DaemonResult result, CancellationToken ct)
    {
        var existing = await processes.RunAsync("crontab", "-l", null, ct);
        if (existing.ExitCode != 0) return;
        if (!existing.StandardOutput.Contains(CronMarker, StringComparison.Ordinal)) return;

        var lines = existing.StandardOutput
            .Split('\n')
            .Where(l => !l.Contains(CronMarker, StringComparison.Ordinal) && !l.Contains(ScriptPath, StringComparison.Ordinal))
            .ToList();

        Directory.CreateDirectory(DaemonDir);
        var tmp = Path.Combine(DaemonDir, "crontab.pending");
        File.WriteAllText(tmp, string.Join('\n', lines).TrimEnd() + "\n", new UTF8Encoding(false));

        if (await RunAsync(result, "crontab", Quote(tmp), ct))
        {
            result.Removed.Add("crontab entry");
            File.Delete(tmp);
        }
    }

    private async Task<bool> RunAsync(DaemonResult result, string command, string args, CancellationToken ct, bool tolerateFailure = false)
    {
        result.Ran.Add($"{command} {args}");
        try
        {
            var run = await processes.RunAsync(command, args, null, ct);
            if (run.ExitCode == 0) return true;
            if (!tolerateFailure)
            {
                result.Problems.Add($"{command} {args} → exit {run.ExitCode}: {run.StandardError.Trim()}");
            }

            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            if (!tolerateFailure) result.Problems.Add($"{command}: {ex.Message}");

            return false;
        }
    }

    private static void RemoveFile(DaemonResult result, string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        result.Removed.Add(path);
    }

    private static void WriteExecutable(string path, string body)
    {
        File.WriteAllText(path, body, new UTF8Encoding(false));
        if (IsWindows) return;
#pragma warning disable CA1416 // Guarded by the IsWindows check above.
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
#pragma warning restore CA1416
    }

    private static string Tail(string path, int lines)
    {
        try
        {
            var all = File.ReadAllLines(path);

            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// `all` is the CLI spelling of `full`; the script must use what the parser
    /// accepts, not the internal constant.
    private static string CliLevel(string level) => level == AsfLevel.Full ? "all" : level;

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
