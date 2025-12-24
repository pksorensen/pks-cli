using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Wrapper that adds logging capabilities to command executions
/// </summary>
public class CommandLoggingWrapper : ICommandLoggingWrapper
{
    private readonly ILoggingOrchestrator _loggingOrchestrator;
    private readonly ILogger<CommandLoggingWrapper> _logger;

    public CommandLoggingWrapper(
        ILoggingOrchestrator loggingOrchestrator,
        ILogger<CommandLoggingWrapper> logger)
    {
        _loggingOrchestrator = loggingOrchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Wraps command execution with comprehensive logging
    /// </summary>
    public async Task<int> ExecuteWithLoggingAsync<TSettings>(
        ICommand<TSettings> command,
        CommandContext context,
        TSettings settings,
        string commandName)
        where TSettings : CommandSettings
    {
        CommandExecutionContext? loggingContext = null;
        var success = false;
        Exception? executionException = null;
        var outputSummary = string.Empty;

        try
        {
            // Extract user information (simplified - could be enhanced)
            var userId = await ExtractUserIdAsync(context, settings);
            var commandArgs = context.Remaining?.Raw?.ToArray() ?? Array.Empty<string>();

            // Initialize logging
            loggingContext = await _loggingOrchestrator.InitializeCommandLoggingAsync(commandName, commandArgs, userId);

            // Track initial performance state
            var initialMemory = GC.GetTotalMemory(false);
            var stopwatch = Stopwatch.StartNew();

            // Execute the command
            int result;
            try
            {
                result = await ExecuteCommandSafelyAsync(command, context, settings);
                success = result == 0; // Assume 0 = success
                outputSummary = $"Command completed with exit code: {result}";
            }
            catch (Exception ex)
            {
                executionException = ex;
                result = -1;
                success = false;
                outputSummary = $"Command failed with exception: {ex.Message}";

                // Log the error
                await _loggingOrchestrator.LogErrorAsync(loggingContext, ex, "exception_thrown", false);

                throw; // Re-throw to maintain normal error handling
            }
            finally
            {
                stopwatch.Stop();

                // Record final performance metrics
                var finalMemory = GC.GetTotalMemory(false);
                var metrics = new PerformanceMetrics
                {
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                    MemoryUsageMb = (finalMemory - initialMemory) / (1024.0 * 1024.0),
                    CpuUsagePercent = GetEstimatedCpuUsage(stopwatch.ElapsedMilliseconds)
                };

                await _loggingOrchestrator.LogPerformanceMetricsAsync(loggingContext, metrics);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during command execution logging for {CommandName}", commandName);
            throw;
        }
        finally
        {
            // Always finalize logging
            if (loggingContext != null)
            {
                try
                {
                    await _loggingOrchestrator.FinalizeCommandLoggingAsync(
                        loggingContext,
                        success,
                        executionException?.Message,
                        outputSummary);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error finalizing command logging for {CommandName}", commandName);
                }
            }
        }
    }

    /// <summary>
    /// Logs a user interaction during command execution
    /// </summary>
    public async Task LogUserInteractionAsync(
        string correlationId,
        string interactionType,
        string promptText,
        string userResponse,
        long responseTimeMs)
    {
        // Find the active logging context
        if (_loggingOrchestrator is LoggingOrchestrator orchestrator)
        {
            // Access active contexts (would need to expose this or use a different approach)
            // For now, we'll log directly to the orchestrator
            try
            {
                var dummyContext = new CommandExecutionContext
                {
                    CorrelationId = correlationId,
                    SessionId = correlationId, // Simplified
                    CommandName = "unknown",
                    CommandArgs = Array.Empty<string>(),
                    StartTime = DateTime.UtcNow,
                    LastActivity = DateTime.UtcNow
                };

                await _loggingOrchestrator.LogUserInteractionAsync(
                    dummyContext,
                    interactionType,
                    promptText,
                    userResponse,
                    responseTimeMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging user interaction for correlation {CorrelationId}", correlationId);
            }
        }
    }

    /// <summary>
    /// Logs feature usage during command execution
    /// </summary>
    public async Task LogFeatureUsageAsync(
        string correlationId,
        string featureName,
        Dictionary<string, object>? featureData = null)
    {
        try
        {
            var dummyContext = new CommandExecutionContext
            {
                CorrelationId = correlationId,
                SessionId = correlationId,
                CommandName = "unknown",
                CommandArgs = Array.Empty<string>(),
                StartTime = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow
            };

            await _loggingOrchestrator.LogFeatureUsageAsync(dummyContext, featureName, featureData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging feature usage for correlation {CorrelationId}: {FeatureName}",
                correlationId, featureName);
        }
    }

    private static async Task<int> ExecuteCommandSafelyAsync<TSettings>(
        ICommand<TSettings> command,
        CommandContext context,
        TSettings settings)
        where TSettings : CommandSettings
    {
        // TODO: Fix command execution based on type - for now use reflection
        var executeMethod = command.GetType().GetMethod("ExecuteAsync") ?? command.GetType().GetMethod("Execute");
        if (executeMethod == null)
            throw new InvalidOperationException($"No Execute or ExecuteAsync method found on {command.GetType()}");

        var result = executeMethod.Invoke(command, new object[] { context, settings });

        if (result is Task<int> taskResult)
        {
            return await taskResult;
        }
        else if (result is int intResult)
        {
            return intResult;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected return type: {result?.GetType()}");
        }
    }

    private static async Task<string?> ExtractUserIdAsync<TSettings>(CommandContext context, TSettings settings)
        where TSettings : CommandSettings
    {
        // Try to extract user ID from various sources
        // This is a simplified implementation - could be enhanced based on actual needs

        // Check environment variables
        var envUserId = Environment.GetEnvironmentVariable("PKS_USER_ID");
        if (!string.IsNullOrEmpty(envUserId))
            return envUserId;

        // Check if settings has a user-related property
        var settingsType = typeof(TSettings);
        var userProperty = settingsType.GetProperty("UserId") ??
                          settingsType.GetProperty("User") ??
                          settingsType.GetProperty("UserName");

        if (userProperty != null)
        {
            var userValue = userProperty.GetValue(settings)?.ToString();
            if (!string.IsNullOrEmpty(userValue))
                return userValue;
        }

        // Try to get current user
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return null;
        }
    }

    private static double GetEstimatedCpuUsage(long elapsedMs)
    {
        // Simplified CPU usage estimation
        // In a real implementation, you might use performance counters or other methods
        try
        {
            var process = Process.GetCurrentProcess();
            var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
            var cpuUsage = (totalProcessorTime / elapsedMs / Environment.ProcessorCount) * 100;
            return Math.Min(100, Math.Max(0, cpuUsage));
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Interface for command logging wrapper
/// </summary>
public interface ICommandLoggingWrapper
{
    /// <summary>
    /// Wraps command execution with comprehensive logging
    /// </summary>
    Task<int> ExecuteWithLoggingAsync<TSettings>(
        ICommand<TSettings> command,
        CommandContext context,
        TSettings settings,
        string commandName)
        where TSettings : CommandSettings;

    /// <summary>
    /// Logs a user interaction during command execution
    /// </summary>
    Task LogUserInteractionAsync(
        string correlationId,
        string interactionType,
        string promptText,
        string userResponse,
        long responseTimeMs);

    /// <summary>
    /// Logs feature usage during command execution
    /// </summary>
    Task LogFeatureUsageAsync(
        string correlationId,
        string featureName,
        Dictionary<string, object>? featureData = null);
}