using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

public interface IRunnerDaemonService
{
    /// <summary>
    /// Start the daemon loop. Blocks until cancellation is requested.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current daemon status
    /// </summary>
    RunnerDaemonStatus GetStatus();

    /// <summary>
    /// Request graceful shutdown - finish active jobs but don't accept new ones
    /// </summary>
    void RequestShutdown();

    /// <summary>
    /// Event raised when a job starts
    /// </summary>
    event EventHandler<RunnerJobState>? JobStarted;

    /// <summary>
    /// Event raised when a job completes (success or failure)
    /// </summary>
    event EventHandler<RunnerJobState>? JobCompleted;

    /// <summary>
    /// Event raised when polling occurs
    /// </summary>
    event EventHandler<string>? StatusChanged;
}
