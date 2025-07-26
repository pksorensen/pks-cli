using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Service for capturing and tracking command execution telemetry data
/// </summary>
public interface ICommandTelemetryService
{
    /// <summary>
    /// Start tracking a command execution
    /// </summary>
    /// <param name="commandName">The name of the command being executed</param>
    /// <param name="commandArgs">Arguments passed to the command</param>
    /// <param name="userId">Optional user identifier</param>
    /// <returns>A correlation ID for tracking this command execution</returns>
    Task<string> StartCommandExecutionAsync(string commandName, string[] commandArgs, string? userId = null);

    /// <summary>
    /// Track command completion with success status
    /// </summary>
    /// <param name="correlationId">The correlation ID from StartCommandExecutionAsync</param>
    /// <param name="success">Whether the command completed successfully</param>
    /// <param name="error">Error details if the command failed</param>
    /// <param name="outputSummary">Summary of command output</param>
    Task CompleteCommandExecutionAsync(string correlationId, bool success, string? error = null, string? outputSummary = null);

    /// <summary>
    /// Track performance metrics for a command execution
    /// </summary>
    /// <param name="correlationId">The correlation ID for the command</param>
    /// <param name="executionTimeMs">Execution time in milliseconds</param>
    /// <param name="memoryUsageMb">Memory usage in megabytes</param>
    /// <param name="cpuUsagePercent">CPU usage percentage</param>
    Task RecordPerformanceMetricsAsync(string correlationId, long executionTimeMs, double memoryUsageMb = 0, double cpuUsagePercent = 0);

    /// <summary>
    /// Track feature usage within commands
    /// </summary>
    /// <param name="correlationId">The correlation ID for the command</param>
    /// <param name="featureName">Name of the feature used</param>
    /// <param name="featureData">Additional feature-specific data</param>
    Task RecordFeatureUsageAsync(string correlationId, string featureName, Dictionary<string, object>? featureData = null);

    /// <summary>
    /// Get command execution statistics
    /// </summary>
    /// <param name="commandName">Optional command name filter</param>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <returns>Aggregated statistics</returns>
    Task<CommandExecutionStatistics> GetCommandStatisticsAsync(string? commandName = null, DateTime? fromDate = null, DateTime? toDate = null);

    /// <summary>
    /// Get recent command executions
    /// </summary>
    /// <param name="limit">Maximum number of records to return</param>
    /// <param name="commandName">Optional command name filter</param>
    /// <returns>List of recent command executions</returns>
    Task<IEnumerable<CommandExecutionRecord>> GetRecentCommandExecutionsAsync(int limit = 50, string? commandName = null);
}

/// <summary>
/// Represents statistics for command executions
/// </summary>
public class CommandExecutionStatistics
{
    public int TotalExecutions { get; set; }
    public int SuccessfulExecutions { get; set; }
    public int FailedExecutions { get; set; }
    public double SuccessRate => TotalExecutions > 0 ? (double)SuccessfulExecutions / TotalExecutions : 0;
    public long AverageExecutionTimeMs { get; set; }
    public long MedianExecutionTimeMs { get; set; }
    public long MaxExecutionTimeMs { get; set; }
    public long MinExecutionTimeMs { get; set; }
    public Dictionary<string, int> CommandUsageCount { get; set; } = new();
    public Dictionary<string, int> FeatureUsageCount { get; set; } = new();
    public Dictionary<string, int> ErrorTypeCount { get; set; } = new();
    public DateTime FirstExecution { get; set; }
    public DateTime LastExecution { get; set; }
}

/// <summary>
/// Represents a single command execution record
/// </summary>
public class CommandExecutionRecord
{
    public string CorrelationId { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public string[] CommandArgs { get; set; } = Array.Empty<string>();
    public string? UserId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long ExecutionTimeMs { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? OutputSummary { get; set; }
    public double MemoryUsageMb { get; set; }
    public double CpuUsagePercent { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
    public List<FeatureUsageRecord> FeaturesUsed { get; set; } = new();
}

/// <summary>
/// Represents feature usage within a command execution
/// </summary>
public class FeatureUsageRecord
{
    public string FeatureName { get; set; } = string.Empty;
    public DateTime UsageTime { get; set; }
    public Dictionary<string, object> FeatureData { get; set; } = new();
}