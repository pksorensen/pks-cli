using System.Diagnostics;
using System.Text;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>Everything needed to launch one detached runner.</summary>
public sealed record LocalRunnerStartRequest(
    string Owner,
    string Project,
    string Server,
    string WorkDir,
    int PollingIntervalSeconds,
    RunnerLauncherCommand Launcher);

public interface ILocalRunnerSupervisor
{
    Task<bool> IsTmuxAvailableAsync(CancellationToken ct = default);
    Task<LocalRunnerRecord> StartDetachedAsync(LocalRunnerStartRequest request, CancellationToken ct = default);
    Task<bool> IsAliveAsync(LocalRunnerRecord record, CancellationToken ct = default);
    Task<string?> CaptureOutputAsync(LocalRunnerRecord record, int lines, CancellationToken ct = default);
    Task<bool> StopAsync(LocalRunnerRecord record, CancellationToken ct = default);
}

/// <summary>
/// Starts, inspects and stops runners that keep running after the shell that launched them is gone.
///
/// tmux is the primary mechanism, not systemd, for three reasons: it is already the house
/// dependency (vibecast requires it), it is what the SSH handoff has always used, and it makes
/// <c>logs</c> free via <c>capture-pane</c> -- a bare nohup+pidfile gives you a log file but no
/// live pane to attach to. Where tmux is missing we fall back to a plain background process with
/// its output redirected to a file, so the feature degrades instead of refusing.
///
/// Both modes launch through a generated script rather than an inline command string. That avoids
/// nesting quotes inside <c>tmux new-session '...'</c> (which breaks the moment a path contains a
/// space), and it leaves an inspectable artifact: the operator can read exactly what their runner
/// was started with.
/// </summary>
public class LocalRunnerSupervisor : ILocalRunnerSupervisor
{
    private readonly IProcessRunner _processRunner;
    private readonly string _stateDir;

    public LocalRunnerSupervisor(IProcessRunner processRunner) : this(processRunner, null)
    {
    }

    /// <param name="stateDir">Where launch scripts and fallback logs live. Overridable so tests
    /// don't scatter files through the developer's own ~/.pks-cli.</param>
    public LocalRunnerSupervisor(IProcessRunner processRunner, string? stateDir)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _stateDir = stateDir ?? DefaultStateDir;
    }

    /// <summary>
    /// Where a detached runner works by default. Deliberately absolute and derived from the
    /// project: the foreground command resolves <c>.agentics/_work</c> relative to the current
    /// directory, and a detached process's working directory is an accident of whoever started it.
    /// </summary>
    public static string DefaultWorkDir(string owner, string project) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".pks-cli", "agentics-work", $"{Sanitize(owner)}-{Sanitize(project)}");

    public static string DefaultStateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pks-cli", "runners");

    public async Task<bool> IsTmuxAvailableAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return false;

        try
        {
            var result = await _processRunner.RunAsync("tmux", "-V", null, ct);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<LocalRunnerRecord> StartDetachedAsync(LocalRunnerStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Directory.CreateDirectory(request.WorkDir);
        Directory.CreateDirectory(_stateDir);

        var slug = $"{Sanitize(request.Owner)}-{Sanitize(request.Project)}";
        var logPath = Path.Combine(_stateDir, $"{slug}.log");
        // --no-prompt is not optional here. A tmux pane has a real TTY, so every interactive gate
        // in the run command would fire -- and the first thing the operator would get is a runner
        // parked on a capability-configure prompt in a pane nobody is watching, looking exactly
        // like a runner that started fine but never claims a job. Measured, not theorised.
        var arguments =
            $"agentics runner run --project {request.Owner}/{request.Project} " +
            $"--server {request.Server} --work-dir {RunnerLauncher.Quote(request.WorkDir)} " +
            $"--polling-interval {request.PollingIntervalSeconds} --no-prompt";
        var commandLine = request.Launcher.BuildCommandLine(arguments);

        if (await IsTmuxAvailableAsync(ct))
        {
            var scriptPath = Path.Combine(_stateDir, $"{slug}.sh");
            await WriteScriptAsync(scriptPath, $"#!/bin/sh\nexec {commandLine}\n", ct);

            var session = RunnerTmuxSession.Name(request.Owner, request.Project);
            var result = await _processRunner.RunAsync(
                "tmux", $"new-session -d -s {session} {ArgQuote(scriptPath)}", request.WorkDir, ct);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"tmux refused to start session '{session}': {result.StandardError.Trim()}");
            }

            return new LocalRunnerRecord
            {
                Owner = request.Owner,
                Project = request.Project,
                Server = request.Server,
                Mode = LocalRunnerMode.Tmux,
                TmuxSession = session,
                WorkDir = request.WorkDir,
                StartedAt = DateTime.UtcNow,
            };
        }

        // No tmux: background process, output to a log file the script itself opens (the parent
        // cannot redirect a child's stdout straight to a file without pumping the stream, and a
        // pump dies with the parent -- which is precisely what we're detaching from).
        var isWindows = OperatingSystem.IsWindows();
        var fallbackScript = Path.Combine(_stateDir, isWindows ? $"{slug}.cmd" : $"{slug}.sh");
        var body = isWindows
            ? $"@echo off\r\n{commandLine} >> \"{logPath}\" 2>&1\r\n"
            : $"#!/bin/sh\nexec >> {RunnerLauncher.Quote(logPath)} 2>&1\nexec {commandLine}\n";
        await WriteScriptAsync(fallbackScript, body, ct);

        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = request.WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
        }
        psi.ArgumentList.Add(fallbackScript);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the detached runner process.");

        return new LocalRunnerRecord
        {
            Owner = request.Owner,
            Project = request.Project,
            Server = request.Server,
            Mode = LocalRunnerMode.Process,
            Pid = process.Id,
            WorkDir = request.WorkDir,
            LogPath = logPath,
            StartedAt = DateTime.UtcNow,
        };
    }

    public async Task<bool> IsAliveAsync(LocalRunnerRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Mode == LocalRunnerMode.Tmux)
        {
            if (string.IsNullOrEmpty(record.TmuxSession)) return false;
            var result = await _processRunner.RunAsync("tmux", $"has-session -t {record.TmuxSession}", null, ct);
            return result.ExitCode == 0;
        }

        if (record.Pid is not { } pid) return false;

        // On Linux the pid alone is not proof: pids are recycled. Where /proc is available, confirm
        // the process is still a pks runner before reporting it alive -- otherwise `list` would
        // cheerfully show a runner that is really somebody else's compiler.
        var cmdlinePath = $"/proc/{pid}/cmdline";
        if (File.Exists(cmdlinePath))
        {
            try
            {
                var cmdline = (await File.ReadAllTextAsync(cmdlinePath, ct)).Replace('\0', ' ');
                return cmdline.Contains("agentics", StringComparison.OrdinalIgnoreCase) &&
                       cmdline.Contains("runner", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            return !Process.GetProcessById(pid).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<string?> CaptureOutputAsync(LocalRunnerRecord record, int lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Mode == LocalRunnerMode.Tmux)
        {
            if (string.IsNullOrEmpty(record.TmuxSession)) return null;
            var result = await _processRunner.RunAsync(
                "tmux", $"capture-pane -p -S -{lines} -t {record.TmuxSession}", null, ct);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }

        if (string.IsNullOrEmpty(record.LogPath) || !File.Exists(record.LogPath)) return null;

        var all = await File.ReadAllLinesAsync(record.LogPath, ct);
        return string.Join(Environment.NewLine, all.TakeLast(lines));
    }

    public async Task<bool> StopAsync(LocalRunnerRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Mode == LocalRunnerMode.Tmux)
        {
            if (string.IsNullOrEmpty(record.TmuxSession)) return false;
            var result = await _processRunner.RunAsync("tmux", $"kill-session -t {record.TmuxSession}", null, ct);
            return result.ExitCode == 0;
        }

        if (record.Pid is not { } pid) return false;

        // SIGTERM, not Process.Kill: the runner installs a ProcessExit handler that drains
        // in-flight jobs and cleans up their containers. .NET's Kill() is SIGKILL on Unix, which
        // would leave those containers orphaned for `runner cleanup` to find later.
        if (!OperatingSystem.IsWindows())
        {
            var result = await _processRunner.RunAsync("kill", $"-TERM {pid}", null, ct);
            return result.ExitCode == 0;
        }

        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Quoting for <see cref="ProcessStartInfo.Arguments"/>, which is parsed with Windows rules on
    /// every platform -- single quotes mean nothing there, so <see cref="RunnerLauncher.Quote"/>
    /// (POSIX, for script bodies) would pass its quote characters through literally.
    /// </summary>
    private static string ArgQuote(string value) =>
        value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;

    private static async Task WriteScriptAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
