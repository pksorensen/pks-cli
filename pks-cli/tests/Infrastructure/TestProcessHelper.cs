using System.Diagnostics;
using System.Text;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Helper class for managing external processes in tests
/// </summary>
public class TestProcessHelper : IDisposable
{
    private readonly List<Process> _activeProcesses = new();
    private readonly object _lock = new();

    /// <summary>
    /// Starts a process with proper timeout and cleanup handling
    /// </summary>
    /// <param name="fileName">Process file name</param>
    /// <param name="arguments">Process arguments</param>
    /// <param name="timeout">Process timeout</param>
    /// <param name="workingDirectory">Working directory</param>
    /// <returns>Process result</returns>
    public async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        string? workingDirectory = null)
    {
        Process? process = null;

        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
                }
            };

            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    errorBuilder.AppendLine(e.Data);
            };

            lock (_lock)
            {
                _activeProcesses.Add(process);
            }

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeout);
            var processTask = process.WaitForExitAsync(cts.Token);

            try
            {
                await processTask;
            }
            catch (OperationCanceledException)
            {
                // Process timed out, kill it
                KillProcess(process);
                throw new TimeoutException($"Process '{fileName} {arguments}' timed out after {timeout.TotalSeconds} seconds");
            }

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = outputBuilder.ToString(),
                StandardError = errorBuilder.ToString(),
                TimedOut = false
            };
        }
        catch (Exception ex) when (!(ex is TimeoutException))
        {
            if (process != null)
                KillProcess(process);

            return new ProcessResult
            {
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = ex.Message,
                TimedOut = false,
                Exception = ex
            };
        }
        finally
        {
            if (process != null)
            {
                lock (_lock)
                {
                    _activeProcesses.Remove(process);
                }
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Starts a long-running process that can be managed externally
    /// </summary>
    /// <param name="fileName">Process file name</param>
    /// <param name="arguments">Process arguments</param>
    /// <param name="workingDirectory">Working directory</param>
    /// <returns>Managed process wrapper</returns>
    public ManagedProcess StartManagedProcess(
        string fileName,
        string arguments,
        string? workingDirectory = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
            }
        };

        lock (_lock)
        {
            _activeProcesses.Add(process);
        }

        return new ManagedProcess(process, this);
    }

    /// <summary>
    /// Kills a process safely
    /// </summary>
    /// <param name="process">Process to kill</param>
    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000); // Wait up to 5 seconds for graceful exit
            }
        }
        catch
        {
            // Ignore errors when killing processes
        }
    }

    /// <summary>
    /// Removes a process from the active list
    /// </summary>
    /// <param name="process">Process to remove</param>
    internal void RemoveProcess(Process process)
    {
        lock (_lock)
        {
            _activeProcesses.Remove(process);
        }
    }

    /// <summary>
    /// Disposes all active processes
    /// </summary>
    public void Dispose()
    {
        Process[] processesToKill;
        lock (_lock)
        {
            processesToKill = _activeProcesses.ToArray();
            _activeProcesses.Clear();
        }

        foreach (var process in processesToKill)
        {
            try
            {
                KillProcess(process);
                process.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}

/// <summary>
/// Result of a process execution
/// </summary>
public class ProcessResult
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
    public bool TimedOut { get; set; }
    public Exception? Exception { get; set; }

    public bool IsSuccess => ExitCode == 0 && Exception == null && !TimedOut;
}

/// <summary>
/// A managed process that can be controlled and monitored
/// </summary>
public class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly TestProcessHelper _helper;
    private readonly StringBuilder _outputBuilder = new();
    private readonly StringBuilder _errorBuilder = new();

    internal ManagedProcess(Process process, TestProcessHelper helper)
    {
        _process = process;
        _helper = helper;

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                _outputBuilder.AppendLine(e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                _errorBuilder.AppendLine(e.Data);
        };
    }

    /// <summary>
    /// Starts the managed process
    /// </summary>
    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Waits for the process to exit with a timeout
    /// </summary>
    /// <param name="timeout">Timeout duration</param>
    /// <returns>True if the process exited, false if timeout occurred</returns>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the current standard output
    /// </summary>
    public string StandardOutput => _outputBuilder.ToString();

    /// <summary>
    /// Gets the current standard error
    /// </summary>
    public string StandardError => _errorBuilder.ToString();

    /// <summary>
    /// Gets the process exit code (only valid after process has exited)
    /// </summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// Gets whether the process has exited
    /// </summary>
    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Kills the process
    /// </summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore errors when killing processes
        }
    }

    /// <summary>
    /// Sends input to the process
    /// </summary>
    /// <param name="input">Input text</param>
    public async Task SendInputAsync(string input)
    {
        await _process.StandardInput.WriteLineAsync(input);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Disposes the managed process
    /// </summary>
    public void Dispose()
    {
        _helper.RemoveProcess(_process);
        Kill();
        _process.Dispose();
    }
}