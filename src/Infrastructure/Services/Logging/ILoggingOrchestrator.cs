using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Orchestrator service that coordinates all logging activities
/// </summary>
public interface ILoggingOrchestrator
{
    /// <summary>
    /// Initialize logging for a new command execution
    /// </summary>
    /// <param name="commandName">Name of the command</param>
    /// <param name="commandArgs">Command arguments</param>
    /// <param name="userId">Optional user identifier</param>
    /// <returns>Command execution context</returns>
    Task<CommandExecutionContext> InitializeCommandLoggingAsync(string commandName, string[] commandArgs, string? userId = null);

    /// <summary>
    /// Finalize logging for a command execution  
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="success">Whether the command succeeded</param>
    /// <param name="error">Error information if failed</param>
    /// <param name="outputSummary">Summary of command output</param>
    Task FinalizeCommandLoggingAsync(CommandExecutionContext context, bool success, string? error = null, string? outputSummary = null);

    /// <summary>
    /// Log a user interaction within a command
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="interactionType">Type of interaction</param>
    /// <param name="promptText">Text shown to user</param>
    /// <param name="userResponse">User's response</param>
    /// <param name="responseTimeMs">Response time in milliseconds</param>
    Task LogUserInteractionAsync(CommandExecutionContext context, string interactionType, string promptText, string userResponse, long responseTimeMs);

    /// <summary>
    /// Log feature usage within a command
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="featureName">Name of the feature used</param>
    /// <param name="featureData">Additional feature data</param>
    Task LogFeatureUsageAsync(CommandExecutionContext context, string featureName, Dictionary<string, object>? featureData = null);

    /// <summary>
    /// Log an error or exception
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="error">Error or exception</param>
    /// <param name="userAction">What the user did about the error</param>
    /// <param name="resolved">Whether the error was resolved</param>
    Task LogErrorAsync(CommandExecutionContext context, Exception error, string? userAction = null, bool resolved = false);

    /// <summary>
    /// Log performance metrics
    /// </summary>
    /// <param name="context">Command execution context</param>
    /// <param name="metrics">Performance metrics to log</param>
    Task LogPerformanceMetricsAsync(CommandExecutionContext context, PerformanceMetrics metrics);

    /// <summary>
    /// Get comprehensive logging statistics
    /// </summary>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <param name="userId">Optional user filter</param>
    /// <returns>Comprehensive logging statistics</returns>
    Task<LoggingStatistics> GetLoggingStatisticsAsync(DateTime? fromDate = null, DateTime? toDate = null, string? userId = null);

    /// <summary>
    /// Generate a comprehensive usage report
    /// </summary>
    /// <param name="reportType">Type of report to generate</param>
    /// <param name="fromDate">Start date for report</param>
    /// <param name="toDate">End date for report</param>
    /// <param name="outputFormat">Output format (json, html, csv)</param>
    /// <returns>Generated report</returns>
    Task<string> GenerateUsageReportAsync(string reportType, DateTime fromDate, DateTime toDate, string outputFormat = "json");

    /// <summary>
    /// Clean up old logging data
    /// </summary>
    /// <param name="retentionPolicy">Data retention policy</param>
    /// <param name="dryRun">If true, return what would be cleaned without actually cleaning</param>
    /// <returns>Cleanup summary</returns>
    Task<LoggingCleanupSummary> CleanupLoggingDataAsync(LogRetentionPolicy retentionPolicy, bool dryRun = false);
}

/// <summary>
/// Context for a command execution with all relevant identifiers
/// </summary>
public class CommandExecutionContext
{
    public string CorrelationId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public string[] CommandArgs { get; set; } = Array.Empty<string>();
    public string? UserId { get; set; }
    public DateTime StartTime { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();

    // Performance tracking
    public System.Diagnostics.Stopwatch Stopwatch { get; set; } = System.Diagnostics.Stopwatch.StartNew();
    public long InitialMemoryUsage { get; set; }
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Performance metrics for logging
/// </summary>
public class PerformanceMetrics
{
    public long ExecutionTimeMs { get; set; }
    public double MemoryUsageMb { get; set; }
    public double CpuUsagePercent { get; set; }
    public long DiskReadMb { get; set; }
    public long DiskWriteMb { get; set; }
    public int NetworkRequestCount { get; set; }
    public long NetworkTransferMb { get; set; }
    public Dictionary<string, double> CustomMetrics { get; set; } = new();
}

/// <summary>
/// Comprehensive logging statistics
/// </summary>
public class LoggingStatistics
{
    public DateTime GeneratedAt { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string? UserId { get; set; }

    // Command Statistics
    public CommandExecutionStatistics CommandStats { get; set; } = new();

    // User Interaction Statistics
    public UserInteractionAnalytics InteractionStats { get; set; } = new();

    // Session Statistics
    public SessionReport SessionStats { get; set; } = new();

    // Overall Health Metrics
    public double OverallSuccessRate { get; set; }
    public double UserSatisfactionScore { get; set; }
    public double SystemPerformanceScore { get; set; }
    public Dictionary<string, object> HealthMetrics { get; set; } = new();

    // Trends and Insights
    public List<string> UsageTrends { get; set; } = new();
    public List<string> RecommendedImprovements { get; set; } = new();
    public List<string> TopUserRequests { get; set; } = new();
}

/// <summary>
/// Data retention policy for logging cleanup
/// </summary>
public class LogRetentionPolicy
{
    public TimeSpan CommandTelemetryRetention { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan UserInteractionRetention { get; set; } = TimeSpan.FromDays(60);
    public TimeSpan SessionDataRetention { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan ErrorLogRetention { get; set; } = TimeSpan.FromDays(180);
    public TimeSpan PerformanceMetricsRetention { get; set; } = TimeSpan.FromDays(45);
    public bool PreserveCriticalErrors { get; set; } = true;
    public bool PreserveUserPreferences { get; set; } = true;
    public int MaxRecordsPerCategory { get; set; } = 100000;
}

/// <summary>
/// Summary of logging data cleanup
/// </summary>
public class LoggingCleanupSummary
{
    public DateTime CleanupTime { get; set; }
    public bool DryRun { get; set; }
    public int CommandRecordsCleaned { get; set; }
    public int InteractionRecordsCleaned { get; set; }
    public int SessionRecordsCleaned { get; set; }
    public int ErrorRecordsCleaned { get; set; }
    public int PerformanceRecordsCleaned { get; set; }
    public long TotalSpaceFreedMb { get; set; }
    public List<string> CleanupActions { get; set; } = new();
    public List<string> PreservedItems { get; set; } = new();
    public Dictionary<string, int> CleanupByCategory { get; set; } = new();
}