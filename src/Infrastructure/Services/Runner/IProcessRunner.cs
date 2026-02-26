namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Abstraction over external process execution to enable testability
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs an external process and captures its output
    /// </summary>
    /// <param name="command">The executable to run</param>
    /// <param name="arguments">Command-line arguments</param>
    /// <param name="workingDirectory">Optional working directory</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result containing exit code, stdout, and stderr</returns>
    Task<ProcessResult> RunAsync(
        string command,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an external process execution
/// </summary>
public record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
