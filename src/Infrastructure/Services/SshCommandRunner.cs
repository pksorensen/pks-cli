using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Result of an SSH command execution
/// </summary>
public class SshCommandResult
{
    public int ExitCode { get; set; }
    public string StdOut { get; set; } = "";
    public string StdErr { get; set; } = "";
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs commands on remote hosts via SSH
/// </summary>
public interface ISshCommandRunner
{
    /// <summary>
    /// Execute a command on a remote host via SSH
    /// </summary>
    Task<SshCommandResult> RunAsync(RemoteHostConfig host, string command, CancellationToken ct = default);

    /// <summary>
    /// Copy files to a remote host via scp
    /// </summary>
    Task<SshCommandResult> ScpAsync(RemoteHostConfig host, string localPath, string remotePath, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Test SSH connectivity to a remote host
    /// </summary>
    Task<bool> TestConnectivityAsync(RemoteHostConfig host, CancellationToken ct = default);

    /// <summary>
    /// Execute a command on a remote host with real-time output streaming.
    /// stderr is streamed line-by-line to the onOutput callback and written to logFile.
    /// stdout is captured for the result (e.g., devcontainer up JSON).
    /// </summary>
    Task<SshCommandResult> RunWithOutputAsync(RemoteHostConfig host, string command, string logFile, Action<string>? onOutput = null, CancellationToken ct = default);
}

public class SshCommandRunner : ISshCommandRunner
{
    private readonly ILogger<SshCommandRunner> _logger;

    public SshCommandRunner(ILogger<SshCommandRunner> logger)
    {
        _logger = logger;
    }

    public async Task<SshCommandResult> RunAsync(RemoteHostConfig host, string command, CancellationToken ct = default)
    {
        var args = BuildSshArgs(host);
        args.Add($"{host.Username}@{host.Host}");
        args.Add(command);

        _logger.LogDebug("SSH command: ssh {Args}", string.Join(" ", args));

        return await ExecuteProcessAsync("ssh", args, ct);
    }

    public async Task<SshCommandResult> ScpAsync(RemoteHostConfig host, string localPath, string remotePath, bool recursive = false, CancellationToken ct = default)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(host.KeyPath))
        {
            args.AddRange(new[] { "-i", host.KeyPath });
        }
        if (host.Port != 22)
        {
            args.AddRange(new[] { "-P", host.Port.ToString() });
        }
        args.AddRange(new[] { "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new" });
        if (recursive)
        {
            args.Add("-r");
        }
        args.Add(localPath);
        args.Add($"{host.Username}@{host.Host}:{remotePath}");

        _logger.LogDebug("SCP command: scp {Args}", string.Join(" ", args));

        return await ExecuteProcessAsync("scp", args, ct);
    }

    public async Task<bool> TestConnectivityAsync(RemoteHostConfig host, CancellationToken ct = default)
    {
        var result = await RunAsync(host, "echo ok", ct);
        return result.Success && result.StdOut.Trim() == "ok";
    }

    public async Task<SshCommandResult> RunWithOutputAsync(RemoteHostConfig host, string command, string logFile, Action<string>? onOutput = null, CancellationToken ct = default)
    {
        var args = BuildSshArgs(host);
        args.Add($"{host.Username}@{host.Host}");
        args.Add(command);

        _logger.LogDebug("SSH streaming command: ssh {Args}", string.Join(" ", args));

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        // Kill the SSH process when cancellation is requested
        ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        });

        var logDir = Path.GetDirectoryName(logFile);
        if (logDir != null) Directory.CreateDirectory(logDir);

        var stderrLines = new System.Text.StringBuilder();
        var stdoutLines = new System.Text.StringBuilder();

        await using var logWriter = new StreamWriter(logFile, append: false) { AutoFlush = true };

        // Stream stderr to log + callback
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(ct)) != null)
                {
                    stderrLines.AppendLine(line);
                    await logWriter.WriteLineAsync(line);
                    var statusLine = ExtractStatusLine(line);
                    if (statusLine != null) onOutput?.Invoke(statusLine);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // Process killed
        }, CancellationToken.None); // Don't cancel the task itself, let the process kill handle it

        // Capture stdout
        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
                {
                    stdoutLines.AppendLine(line);
                    await logWriter.WriteLineAsync($"[stdout] {line}");
                    var statusLine = ExtractStatusLine(line);
                    if (statusLine != null) onOutput?.Invoke(statusLine);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { } // Process killed
        }, CancellationToken.None);

        try
        {
            await Task.WhenAll(stderrTask, stdoutTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        }

        return new SshCommandResult
        {
            ExitCode = process.HasExited ? process.ExitCode : -1,
            StdOut = stdoutLines.ToString(),
            StdErr = stderrLines.ToString()
        };
    }

    /// <summary>
    /// Extract a short meaningful status from devcontainer build output
    /// </summary>
    private static string? ExtractStatusLine(string line)
    {
        // Skip noisy lines (download progress, empty)
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.Contains(".......... ..........")) return null; // wget progress
        if (line.TrimStart().StartsWith("#") && line.Contains("sha256:")) return null; // layer hashes

        // Feature installation steps
        if (line.Contains("Feature")) return line.Trim().Length > 80 ? line.Trim()[..80] : line.Trim();
        if (line.Contains("Installing")) return line.Trim().Length > 80 ? line.Trim()[..80] : line.Trim();
        if (line.Contains("DONE")) return line.Trim();
        if (line.Contains("Start:")) return line.Trim().Length > 80 ? line.Trim()[..80] : line.Trim();
        if (line.Contains("exporting")) return line.Trim();
        if (line.Contains("Step ")) return line.Trim();

        // Docker build steps
        if (line.TrimStart().StartsWith("#") && line.Contains("["))
        {
            var trimmed = line.Trim();
            return trimmed.Length > 80 ? trimmed[..80] : trimmed;
        }

        return null;
    }

    private static List<string> BuildSshArgs(RemoteHostConfig host)
    {
        var args = new List<string>();
        if (!string.IsNullOrEmpty(host.KeyPath))
        {
            args.AddRange(new[] { "-i", host.KeyPath });
        }
        if (host.Port != 22)
        {
            args.AddRange(new[] { "-p", host.Port.ToString() });
        }
        args.AddRange(new[] { "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "StrictHostKeyChecking=accept-new" });
        return args;
    }

    private static async Task<SshCommandResult> ExecuteProcessAsync(string fileName, List<string> args, CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return new SshCommandResult
        {
            ExitCode = process.ExitCode,
            StdOut = await stdoutTask,
            StdErr = await stderrTask
        };
    }
}
