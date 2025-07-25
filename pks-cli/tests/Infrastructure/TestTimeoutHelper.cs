using System.Diagnostics;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Helper class for managing test timeouts and preventing hangs
/// </summary>
public static class TestTimeoutHelper
{
    /// <summary>
    /// Default timeout for fast operations (5 seconds)
    /// </summary>
    public static readonly TimeSpan FastTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Default timeout for medium operations (15 seconds)
    /// </summary>
    public static readonly TimeSpan MediumTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Default timeout for slow operations (30 seconds)
    /// </summary>
    public static readonly TimeSpan SlowTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Executes an async operation with a timeout
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="timeout">Timeout duration</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>The result of the operation</returns>
    /// <exception cref="TimeoutException">Thrown when the operation times out</exception>
    public static async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await operation(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
        }
    }

    /// <summary>
    /// Executes an async operation with a timeout (void return)
    /// </summary>
    /// <param name="operation">The operation to execute</param>
    /// <param name="timeout">Timeout duration</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <exception cref="TimeoutException">Thrown when the operation times out</exception>
    public static async Task ExecuteWithTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await operation(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
        }
    }

    /// <summary>
    /// Executes a synchronous operation with a timeout using Task.Run
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="operation">The operation to execute</param>
    /// <param name="timeout">Timeout duration</param>
    /// <returns>The result of the operation</returns>
    /// <exception cref="TimeoutException">Thrown when the operation times out</exception>
    public static async Task<T> ExecuteWithTimeoutAsync<T>(
        Func<T> operation,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        
        try
        {
            return await Task.Run(operation, cts.Token);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Operation timed out after {timeout.TotalSeconds} seconds");
        }
    }

    /// <summary>
    /// Waits for a condition to be true with a timeout
    /// </summary>
    /// <param name="condition">The condition to check</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">How often to check the condition</param>
    /// <returns>True if the condition became true, false if timeout occurred</returns>
    public static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        pollInterval ??= TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return true;

            await Task.Delay(pollInterval.Value);
        }

        return false;
    }

    /// <summary>
    /// Waits for an async condition to be true with a timeout
    /// </summary>
    /// <param name="condition">The async condition to check</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">How often to check the condition</param>
    /// <returns>True if the condition became true, false if timeout occurred</returns>
    public static async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        pollInterval ??= TimeSpan.FromMilliseconds(100);
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < timeout)
        {
            if (await condition())
                return true;

            await Task.Delay(pollInterval.Value);
        }

        return false;
    }

    /// <summary>
    /// Creates a cancellation token that will be cancelled after the specified timeout
    /// </summary>
    /// <param name="timeout">Timeout duration</param>
    /// <returns>A cancellation token source</returns>
    public static CancellationTokenSource CreateTimeoutToken(TimeSpan timeout)
    {
        return new CancellationTokenSource(timeout);
    }

    /// <summary>
    /// Gets the appropriate timeout based on test category
    /// </summary>
    /// <param name="category">Test category</param>
    /// <returns>Appropriate timeout for the category</returns>
    public static TimeSpan GetTimeoutForCategory(string category)
    {
        return category switch
        {
            TestCategories.Unit => FastTimeout,
            TestCategories.Integration => MediumTimeout,
            TestCategories.EndToEnd => SlowTimeout,
            TestCategories.Performance => SlowTimeout,
            _ => MediumTimeout
        };
    }
}