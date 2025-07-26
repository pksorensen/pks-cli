using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace PKS.Infrastructure.Commands;

/// <summary>
/// Base class for commands that provides integrated logging capabilities
/// </summary>
public abstract class LoggingCommandBase<TSettings> : AsyncCommand<TSettings>
    where TSettings : CommandSettings
{
    protected readonly ILogger Logger;
    protected readonly ILoggingOrchestrator LoggingOrchestrator;
    protected CommandExecutionContext? LoggingContext { get; private set; }

    protected LoggingCommandBase(
        ILogger logger,
        ILoggingOrchestrator loggingOrchestrator)
    {
        Logger = logger;
        LoggingOrchestrator = loggingOrchestrator;
    }

    /// <summary>
    /// Executes the command with integrated logging
    /// </summary>
    public sealed override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        var commandName = GetType().Name.Replace("Command", "").ToLower();
        var success = false;
        Exception? executionException = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Initialize logging
            var userId = await ExtractUserIdAsync(context, settings);
            var commandArgs = context.Remaining?.Raw?.ToArray() ?? Array.Empty<string>();

            LoggingContext = await LoggingOrchestrator.InitializeCommandLoggingAsync(commandName, commandArgs, userId);

            // Execute the actual command implementation
            var result = await ExecuteCommandAsync(context, settings);

            success = result == 0;
            stopwatch.Stop();

            // Log final performance metrics
            await LogPerformanceMetricsAsync(stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            executionException = ex;
            success = false;
            stopwatch.Stop();

            // Log the error
            if (LoggingContext != null)
            {
                await LoggingOrchestrator.LogErrorAsync(LoggingContext, ex, "exception_thrown", false);
            }

            Logger.LogError(ex, "Command execution failed: {CommandName}", commandName);
            throw;
        }
        finally
        {
            // Finalize logging
            if (LoggingContext != null)
            {
                try
                {
                    var outputSummary = success
                        ? "Command completed successfully"
                        : $"Command failed: {executionException?.Message ?? "Unknown error"}";

                    await LoggingOrchestrator.FinalizeCommandLoggingAsync(
                        LoggingContext,
                        success,
                        executionException?.Message,
                        outputSummary);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error finalizing command logging for {CommandName}", commandName);
                }
            }
        }
    }

    /// <summary>
    /// Abstract method that subclasses must implement for their command logic
    /// </summary>
    protected abstract Task<int> ExecuteCommandAsync(CommandContext context, TSettings settings);

    /// <summary>
    /// Logs a user interaction (prompt, selection, etc.)
    /// </summary>
    protected async Task LogUserInteractionAsync(string interactionType, string promptText, string userResponse, long responseTimeMs = 0)
    {
        if (LoggingContext != null)
        {
            try
            {
                await LoggingOrchestrator.LogUserInteractionAsync(
                    LoggingContext,
                    interactionType,
                    promptText,
                    userResponse,
                    responseTimeMs);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log user interaction: {InteractionType}", interactionType);
            }
        }
    }

    /// <summary>
    /// Logs feature usage within the command
    /// </summary>
    protected async Task LogFeatureUsageAsync(string featureName, Dictionary<string, object>? featureData = null)
    {
        if (LoggingContext != null)
        {
            try
            {
                await LoggingOrchestrator.LogFeatureUsageAsync(LoggingContext, featureName, featureData);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log feature usage: {FeatureName}", featureName);
            }
        }
    }

    /// <summary>
    /// Logs an error with optional user action tracking
    /// </summary>
    protected async Task LogErrorAsync(Exception error, string? userAction = null, bool resolved = false)
    {
        if (LoggingContext != null)
        {
            try
            {
                await LoggingOrchestrator.LogErrorAsync(LoggingContext, error, userAction, resolved);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log error: {ErrorType}", error.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Logs performance metrics
    /// </summary>
    protected async Task LogPerformanceMetricsAsync(long executionTimeMs, double memoryUsageMb = 0, double cpuUsagePercent = 0)
    {
        if (LoggingContext != null)
        {
            try
            {
                var metrics = new PerformanceMetrics
                {
                    ExecutionTimeMs = executionTimeMs,
                    MemoryUsageMb = memoryUsageMb > 0 ? memoryUsageMb : GetCurrentMemoryUsageMb(),
                    CpuUsagePercent = cpuUsagePercent > 0 ? cpuUsagePercent : GetEstimatedCpuUsage()
                };

                await LoggingOrchestrator.LogPerformanceMetricsAsync(LoggingContext, metrics);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to log performance metrics");
            }
        }
    }

    /// <summary>
    /// Helper method for logging user prompts with automatic timing
    /// </summary>
    protected async Task<T> LoggedPromptAsync<T>(Func<Task<T>> promptAction, string promptText, string interactionType = "prompt")
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await promptAction();
            stopwatch.Stop();

            var userResponse = result?.ToString() ?? "null";
            await LogUserInteractionAsync(interactionType, promptText, userResponse, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await LogUserInteractionAsync(interactionType, promptText, $"Error: {ex.Message}", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Helper method for logging synchronous user prompts with automatic timing
    /// </summary>
    protected async Task<T> LoggedPromptAsync<T>(Func<T> promptAction, string promptText, string interactionType = "prompt")
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = promptAction();
            stopwatch.Stop();

            var userResponse = result?.ToString() ?? "null";
            await LogUserInteractionAsync(interactionType, promptText, userResponse, stopwatch.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await LogUserInteractionAsync(interactionType, promptText, $"Error: {ex.Message}", stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Helper method for tracking feature usage with exception handling
    /// </summary>
    protected async Task<T> WithFeatureTrackingAsync<T>(Func<Task<T>> action, string featureName, Dictionary<string, object>? featureData = null)
    {
        await LogFeatureUsageAsync(featureName, featureData);

        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            await LogErrorAsync(ex, $"using_{featureName}", false);
            throw;
        }
    }

    /// <summary>
    /// Helper method for showing status with logging
    /// </summary>
    protected async Task<T> WithStatusAsync<T>(Func<Task<T>> action, string statusMessage, string featureName)
    {
        await LogFeatureUsageAsync(featureName, new Dictionary<string, object>
        {
            ["StatusMessage"] = statusMessage,
            ["StartTime"] = DateTime.UtcNow
        });

        return await AnsiConsole.Status()
            .StartAsync(statusMessage, async ctx =>
            {
                try
                {
                    var result = await action();
                    ctx.Status($"{statusMessage} ✓");
                    return result;
                }
                catch (Exception ex)
                {
                    ctx.Status($"{statusMessage} ✗");
                    await LogErrorAsync(ex, $"status_operation_{featureName}", false);
                    throw;
                }
            });
    }

    private static async Task<string?> ExtractUserIdAsync<T>(CommandContext context, T settings) where T : CommandSettings
    {
        // Try various methods to extract user ID
        var envUserId = Environment.GetEnvironmentVariable("PKS_USER_ID");
        if (!string.IsNullOrEmpty(envUserId))
            return envUserId;

        // Check if settings has user-related properties
        var settingsType = typeof(T);
        var userProperties = new[] { "UserId", "User", "UserName", "Username" };

        foreach (var propName in userProperties)
        {
            var property = settingsType.GetProperty(propName);
            if (property != null)
            {
                var value = property.GetValue(settings)?.ToString();
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }

        // Fallback to current user
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return null;
        }
    }

    private static double GetCurrentMemoryUsageMb()
    {
        try
        {
            return GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        }
        catch
        {
            return 0;
        }
    }

    private static double GetEstimatedCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalMilliseconds / Environment.ProcessorCount / 100.0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Base class for commands that don't need settings
/// </summary>
public abstract class LoggingCommandBase : LoggingCommandBase<EmptyCommandSettings>
{
    protected LoggingCommandBase(
        ILogger logger,
        ILoggingOrchestrator loggingOrchestrator)
        : base(logger, loggingOrchestrator)
    {
    }
}

/// <summary>
/// Empty command settings for commands that don't need configuration
/// </summary>
public class EmptyCommandSettings : CommandSettings
{
}