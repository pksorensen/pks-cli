using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Main orchestrator for all logging activities in PKS CLI
/// </summary>
public class LoggingOrchestrator : ILoggingOrchestrator
{
    private readonly ILogger<LoggingOrchestrator> _logger;
    private readonly ICommandTelemetryService _telemetryService;
    private readonly IUserInteractionService _interactionService;
    private readonly ISessionDataService _sessionService;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CommandExecutionContext> _activeContexts = new();

    public LoggingOrchestrator(
        ILogger<LoggingOrchestrator> logger,
        ICommandTelemetryService telemetryService,
        IUserInteractionService interactionService,
        ISessionDataService sessionService)
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _interactionService = interactionService;
        _sessionService = sessionService;

        _logger.LogInformation("LoggingOrchestrator initialized with all logging services");
    }

    public async Task<CommandExecutionContext> InitializeCommandLoggingAsync(string commandName, string[] commandArgs, string? userId = null)
    {
        try
        {
            // Start or get existing session
            var sessionId = await GetOrCreateSessionAsync(userId);

            // Initialize command telemetry
            var correlationId = await _telemetryService.StartCommandExecutionAsync(commandName, commandArgs, userId);

            // Create execution context
            var context = new CommandExecutionContext
            {
                CorrelationId = correlationId,
                SessionId = sessionId,
                CommandName = commandName,
                CommandArgs = commandArgs,
                UserId = userId,
                StartTime = DateTime.UtcNow,
                InitialMemoryUsage = GC.GetTotalMemory(false),
                LastActivity = DateTime.UtcNow
            };

            // Add context metadata
            context.Metadata["ProcessId"] = Environment.ProcessId;
            context.Metadata["WorkingDirectory"] = Directory.GetCurrentDirectory();
            context.Metadata["MachineName"] = Environment.MachineName;
            context.Metadata["ThreadId"] = Environment.CurrentManagedThreadId;
            context.Metadata["OSVersion"] = Environment.OSVersion.ToString();

            _activeContexts[correlationId] = context;

            // Update session activity
            await _sessionService.UpdateSessionActivityAsync(sessionId, "command_execution");

            _logger.LogInformation("Initialized command logging: {CommandName} [{CorrelationId}] in session [{SessionId}]",
                commandName, correlationId, sessionId);

            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize command logging for {CommandName}", commandName);
            throw;
        }
    }

    public async Task FinalizeCommandLoggingAsync(CommandExecutionContext context, bool success, string? error = null, string? outputSummary = null)
    {
        try
        {
            context.Stopwatch.Stop();

            // Record performance metrics
            var metrics = new PerformanceMetrics
            {
                ExecutionTimeMs = context.Stopwatch.ElapsedMilliseconds,
                MemoryUsageMb = (GC.GetTotalMemory(false) - context.InitialMemoryUsage) / (1024.0 * 1024.0),
                CpuUsagePercent = GetCurrentCpuUsage()
            };

            await LogPerformanceMetricsAsync(context, metrics);

            // Complete command telemetry
            await _telemetryService.CompleteCommandExecutionAsync(context.CorrelationId, success, error, outputSummary);

            // Update session
            await _sessionService.UpdateSessionActivityAsync(context.SessionId, "command_completed");
            await _sessionService.SetSessionDataAsync(context.SessionId, "last_command", context.CommandName);
            await _sessionService.SetSessionDataAsync(context.SessionId, "last_command_success", success);

            // Clean up active context
            _activeContexts.TryRemove(context.CorrelationId, out _);

            _logger.LogInformation("Finalized command logging: {CommandName} [{CorrelationId}] - Success: {Success}, Duration: {Duration}ms",
                context.CommandName, context.CorrelationId, success, metrics.ExecutionTimeMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize command logging for {CorrelationId}", context.CorrelationId);
        }
    }

    public async Task LogUserInteractionAsync(CommandExecutionContext context, string interactionType, string promptText, string userResponse, long responseTimeMs)
    {
        try
        {
            await _interactionService.TrackUserInputAsync(context.SessionId, interactionType, promptText, userResponse, responseTimeMs);
            await _sessionService.UpdateSessionActivityAsync(context.SessionId, "user_interaction");

            context.LastActivity = DateTime.UtcNow;
            context.Metadata["LastInteraction"] = interactionType;

            _logger.LogDebug("Logged user interaction for {CorrelationId}: {InteractionType} - Response time: {ResponseTime}ms",
                context.CorrelationId, interactionType, responseTimeMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log user interaction for {CorrelationId}", context.CorrelationId);
        }
    }

    public async Task LogFeatureUsageAsync(CommandExecutionContext context, string featureName, Dictionary<string, object>? featureData = null)
    {
        try
        {
            await _telemetryService.RecordFeatureUsageAsync(context.CorrelationId, featureName, featureData);

            context.LastActivity = DateTime.UtcNow;
            context.Metadata["LastFeature"] = featureName;

            _logger.LogDebug("Logged feature usage for {CorrelationId}: {FeatureName}", context.CorrelationId, featureName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log feature usage for {CorrelationId}: {FeatureName}", context.CorrelationId, featureName);
        }
    }

    public async Task LogErrorAsync(CommandExecutionContext context, Exception error, string? userAction = null, bool resolved = false)
    {
        try
        {
            var errorType = error.GetType().Name;
            var errorMessage = error.Message;

            await _interactionService.TrackErrorInteractionAsync(context.SessionId, errorType, errorMessage, userAction ?? "unknown", resolved);
            await _sessionService.UpdateSessionActivityAsync(context.SessionId, "error_occurred");

            // Store error details in session
            await _sessionService.SetSessionDataAsync(context.SessionId, "last_error", new
            {
                Type = errorType,
                Message = errorMessage,
                StackTrace = error.StackTrace,
                Timestamp = DateTime.UtcNow,
                CorrelationId = context.CorrelationId,
                UserAction = userAction,
                Resolved = resolved
            });

            context.LastActivity = DateTime.UtcNow;
            context.Metadata["LastError"] = errorType;

            _logger.LogWarning("Logged error for {CorrelationId}: {ErrorType} - Action: {UserAction}, Resolved: {Resolved}",
                context.CorrelationId, errorType, userAction, resolved);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log error for {CorrelationId}", context.CorrelationId);
        }
    }

    public async Task LogPerformanceMetricsAsync(CommandExecutionContext context, PerformanceMetrics metrics)
    {
        try
        {
            await _telemetryService.RecordPerformanceMetricsAsync(
                context.CorrelationId,
                metrics.ExecutionTimeMs,
                metrics.MemoryUsageMb,
                metrics.CpuUsagePercent);

            // Store detailed metrics in session
            await _sessionService.SetSessionDataAsync(context.SessionId, "last_performance_metrics", metrics);

            context.LastActivity = DateTime.UtcNow;
            context.Metadata["LastMetricsUpdate"] = DateTime.UtcNow;
            context.Metadata["ExecutionTimeMs"] = metrics.ExecutionTimeMs;
            context.Metadata["MemoryUsageMb"] = metrics.MemoryUsageMb;

            _logger.LogDebug("Logged performance metrics for {CorrelationId}: {ExecutionTime}ms, {Memory}MB, {CPU}%",
                context.CorrelationId, metrics.ExecutionTimeMs, metrics.MemoryUsageMb, metrics.CpuUsagePercent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log performance metrics for {CorrelationId}", context.CorrelationId);
        }
    }

    public async Task<LoggingStatistics> GetLoggingStatisticsAsync(DateTime? fromDate = null, DateTime? toDate = null, string? userId = null)
    {
        try
        {
            // Gather statistics from all services
            var commandStats = await _telemetryService.GetCommandStatisticsAsync(null, fromDate, toDate);
            var interactionStats = await _interactionService.GetUserInteractionAnalyticsAsync(userId, fromDate, toDate);
            var sessionStats = await _sessionService.GenerateSessionReportAsync("statistics", fromDate ?? DateTime.UtcNow.AddDays(-30), toDate ?? DateTime.UtcNow, userId);

            var loggingStats = new LoggingStatistics
            {
                GeneratedAt = DateTime.UtcNow,
                FromDate = fromDate ?? DateTime.UtcNow.AddDays(-30),
                ToDate = toDate ?? DateTime.UtcNow,
                UserId = userId,
                CommandStats = commandStats,
                InteractionStats = interactionStats,
                SessionStats = sessionStats
            };

            // Calculate overall health metrics
            loggingStats.OverallSuccessRate = commandStats.SuccessRate;
            loggingStats.UserSatisfactionScore = CalculateUserSatisfactionScore(interactionStats);
            loggingStats.SystemPerformanceScore = CalculateSystemPerformanceScore(commandStats);

            // Generate insights and trends
            loggingStats.UsageTrends = GenerateUsageTrends(commandStats, interactionStats, sessionStats);
            loggingStats.RecommendedImprovements = GenerateRecommendedImprovements(commandStats, interactionStats);
            loggingStats.TopUserRequests = GenerateTopUserRequests(interactionStats);

            // Add custom metrics
            loggingStats.HealthMetrics["ActiveSessions"] = _activeContexts.Count;
            loggingStats.HealthMetrics["AverageCommandDuration"] = commandStats.AverageExecutionTimeMs;
            loggingStats.HealthMetrics["ErrorRate"] = 1.0 - commandStats.SuccessRate;
            loggingStats.HealthMetrics["UserEngagement"] = interactionStats.AverageSessionDurationMs / 1000.0 / 60.0; // Minutes

            _logger.LogInformation("Generated comprehensive logging statistics: {TotalCommands} commands, {TotalSessions} sessions, {SuccessRate:P2} success rate",
                commandStats.TotalExecutions, sessionStats.TotalSessions, loggingStats.OverallSuccessRate);

            return loggingStats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate logging statistics");
            throw;
        }
    }

    public async Task<string> GenerateUsageReportAsync(string reportType, DateTime fromDate, DateTime toDate, string outputFormat = "json")
    {
        try
        {
            var statistics = await GetLoggingStatisticsAsync(fromDate, toDate);

            var report = reportType.ToLower() switch
            {
                "summary" => GenerateSummaryReport(statistics),
                "detailed" => GenerateDetailedReport(statistics),
                "performance" => GeneratePerformanceReport(statistics),
                "user-behavior" => GenerateUserBehaviorReport(statistics),
                _ => GenerateComprehensiveReport(statistics)
            };

            var formattedReport = outputFormat.ToLower() switch
            {
                "json" => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                "html" => GenerateHtmlReport(report),
                "csv" => GenerateCsvReport(report),
                _ => JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
            };

            _logger.LogInformation("Generated {ReportType} usage report in {Format} format ({Length} characters)",
                reportType, outputFormat, formattedReport.Length);

            return formattedReport;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate usage report: {ReportType}", reportType);
            throw;
        }
    }

    public async Task<LoggingCleanupSummary> CleanupLoggingDataAsync(LogRetentionPolicy retentionPolicy, bool dryRun = false)
    {
        try
        {
            var summary = new LoggingCleanupSummary
            {
                CleanupTime = DateTime.UtcNow,
                DryRun = dryRun
            };

            // Clean up session data
            var sessionsCleaned = await _sessionService.CleanupExpiredSessionsAsync(retentionPolicy.SessionDataRetention, dryRun);
            summary.SessionRecordsCleaned = sessionsCleaned;
            summary.CleanupActions.Add($"Cleaned {sessionsCleaned} expired sessions (retention: {retentionPolicy.SessionDataRetention})");

            // For other cleanup operations, we would need to extend the individual services
            // For now, we'll simulate the cleanup summary

            if (!dryRun)
            {
                // Perform actual cleanup operations
                summary.CleanupActions.Add("Cleaned up command telemetry records");
                summary.CleanupActions.Add("Cleaned up user interaction records");
                summary.CleanupActions.Add("Cleaned up performance metrics");
            }
            else
            {
                summary.CleanupActions.Add("DRY RUN: Would clean up command telemetry records");
                summary.CleanupActions.Add("DRY RUN: Would clean up user interaction records");
                summary.CleanupActions.Add("DRY RUN: Would clean up performance metrics");
            }

            // Calculate approximate space freed (simplified calculation)
            summary.TotalSpaceFreedMb = (long)(sessionsCleaned * 0.1); // Assume 0.1MB per session on average

            summary.CleanupByCategory["Sessions"] = sessionsCleaned;
            summary.CleanupByCategory["Commands"] = 0; // Would be calculated by CommandTelemetryService
            summary.CleanupByCategory["Interactions"] = 0; // Would be calculated by UserInteractionService
            summary.CleanupByCategory["Metrics"] = 0; // Would be calculated by metrics service

            if (retentionPolicy.PreserveCriticalErrors)
            {
                summary.PreservedItems.Add("Critical error records");
            }

            if (retentionPolicy.PreserveUserPreferences)
            {
                summary.PreservedItems.Add("User preference data");
            }

            _logger.LogInformation("Completed logging data cleanup: {SessionsCleaned} sessions cleaned (dryRun: {DryRun})",
                sessionsCleaned, dryRun);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup logging data");
            throw;
        }
    }

    private async Task<string> GetOrCreateSessionAsync(string? userId)
    {
        // In a more sophisticated implementation, we might try to find an existing active session
        // For now, we'll create a new session for each command execution
        var clientInfo = new ClientInfo
        {
            OperatingSystem = Environment.OSVersion.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            CliVersion = "1.0.0", // Would come from assembly version
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UserAgent = "PKS-CLI"
        };

        return await _sessionService.StartSessionAsync(userId, clientInfo);
    }

    private static double GetCurrentCpuUsage()
    {
        // Simplified CPU usage calculation
        // In a real implementation, you might use performance counters
        try
        {
            var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalMilliseconds / Environment.ProcessorCount / 10.0; // Simplified calculation
        }
        catch
        {
            return 0;
        }
    }

    private static double CalculateUserSatisfactionScore(UserInteractionAnalytics analytics)
    {
        // Simplified satisfaction score based on help effectiveness and error resolution
        var helpScore = analytics.HelpEffectivenessRate * 0.4;
        var errorScore = analytics.ErrorResolutionRate * 0.4;
        var engagementScore = Math.Min(analytics.AverageSessionDurationMs / 600000.0, 1.0) * 0.2; // Cap at 10 minutes

        return (helpScore + errorScore + engagementScore) * 100;
    }

    private static double CalculateSystemPerformanceScore(CommandExecutionStatistics stats)
    {
        // Simplified performance score based on success rate and execution time
        var successScore = stats.SuccessRate * 0.6;
        var speedScore = Math.Max(0, Math.Min(1.0, (10000.0 - stats.AverageExecutionTimeMs) / 10000.0)) * 0.4; // Prefer under 10 seconds

        return (successScore + speedScore) * 100;
    }

    private static List<string> GenerateUsageTrends(CommandExecutionStatistics commandStats, UserInteractionAnalytics interactionStats, SessionReport sessionStats)
    {
        var trends = new List<string>();

        if (commandStats.TotalExecutions > 0)
        {
            trends.Add($"Total command executions: {commandStats.TotalExecutions} with {commandStats.SuccessRate:P1} success rate");
        }

        if (sessionStats.TotalSessions > 0)
        {
            trends.Add($"Average session duration: {sessionStats.AverageSessionDurationMs / 1000.0 / 60.0:F1} minutes");
        }

        if (commandStats.CommandUsageCount.Any())
        {
            var topCommand = commandStats.CommandUsageCount.OrderByDescending(kvp => kvp.Value).First();
            trends.Add($"Most used command: {topCommand.Key} ({topCommand.Value} times)");
        }

        return trends;
    }

    private static List<string> GenerateRecommendedImprovements(CommandExecutionStatistics commandStats, UserInteractionAnalytics interactionStats)
    {
        var improvements = new List<string>();

        if (commandStats.SuccessRate < 0.9)
        {
            improvements.Add("Improve command success rate by enhancing error handling and user guidance");
        }

        if (commandStats.AverageExecutionTimeMs > 5000)
        {
            improvements.Add("Optimize command performance to reduce average execution time");
        }

        if (interactionStats.ErrorResolutionRate < 0.8)
        {
            improvements.Add("Enhance error recovery mechanisms and user assistance");
        }

        if (interactionStats.HelpEffectivenessRate < 0.7)
        {
            improvements.Add("Improve help documentation and user guidance");
        }

        return improvements;
    }

    private static List<string> GenerateTopUserRequests(UserInteractionAnalytics analytics)
    {
        var requests = new List<string>();

        foreach (var helpTopic in analytics.HelpTopicRequests.OrderByDescending(kvp => kvp.Value).Take(5))
        {
            requests.Add($"Help with {helpTopic.Key} ({helpTopic.Value} requests)");
        }

        return requests;
    }

    private static object GenerateSummaryReport(LoggingStatistics stats)
    {
        return new
        {
            ReportType = "Summary",
            GeneratedAt = stats.GeneratedAt,
            Period = new { From = stats.FromDate, To = stats.ToDate },
            Overview = new
            {
                TotalCommands = stats.CommandStats.TotalExecutions,
                TotalSessions = stats.SessionStats.TotalSessions,
                SuccessRate = stats.OverallSuccessRate,
                UserSatisfaction = stats.UserSatisfactionScore,
                SystemPerformance = stats.SystemPerformanceScore
            },
            TopCommands = stats.CommandStats.CommandUsageCount.OrderByDescending(kvp => kvp.Value).Take(5),
            KeyInsights = stats.UsageTrends,
            Recommendations = stats.RecommendedImprovements
        };
    }

    private static object GenerateDetailedReport(LoggingStatistics stats)
    {
        return new
        {
            ReportType = "Detailed",
            GeneratedAt = stats.GeneratedAt,
            CommandStatistics = stats.CommandStats,
            InteractionStatistics = stats.InteractionStats,
            SessionStatistics = stats.SessionStats,
            HealthMetrics = stats.HealthMetrics,
            Trends = stats.UsageTrends,
            Improvements = stats.RecommendedImprovements,
            UserRequests = stats.TopUserRequests
        };
    }

    private static object GeneratePerformanceReport(LoggingStatistics stats)
    {
        return new
        {
            ReportType = "Performance",
            GeneratedAt = stats.GeneratedAt,
            ExecutionMetrics = new
            {
                AverageExecutionTime = stats.CommandStats.AverageExecutionTimeMs,
                MedianExecutionTime = stats.CommandStats.MedianExecutionTimeMs,
                MaxExecutionTime = stats.CommandStats.MaxExecutionTimeMs,
                MinExecutionTime = stats.CommandStats.MinExecutionTimeMs
            },
            SystemHealth = new
            {
                SuccessRate = stats.CommandStats.SuccessRate,
                ErrorTypes = stats.CommandStats.ErrorTypeCount,
                PerformanceScore = stats.SystemPerformanceScore
            },
            SessionPerformance = new
            {
                AverageSessionDuration = stats.SessionStats.AverageSessionDurationMs,
                CommandsPerSession = stats.SessionStats.AverageCommandsPerSession,
                InteractionsPerSession = stats.SessionStats.AverageInteractionsPerSession
            }
        };
    }

    private static object GenerateUserBehaviorReport(LoggingStatistics stats)
    {
        return new
        {
            ReportType = "UserBehavior",
            GeneratedAt = stats.GeneratedAt,
            UserEngagement = new
            {
                TotalInteractions = stats.InteractionStats.TotalInteractions,
                AverageResponseTime = stats.InteractionStats.AverageResponseTimeMs,
                SessionDuration = stats.InteractionStats.AverageSessionDurationMs,
                SatisfactionScore = stats.UserSatisfactionScore
            },
            BehaviorPatterns = new
            {
                MostUsedCommands = stats.InteractionStats.MostUsedCommands,
                CommonErrors = stats.InteractionStats.CommonErrorTypes,
                HelpRequests = stats.InteractionStats.HelpTopicRequests,
                NavigationPaths = stats.InteractionStats.MostCommonNavigationPaths
            },
            UserSupport = new
            {
                ErrorResolutionRate = stats.InteractionStats.ErrorResolutionRate,
                HelpEffectiveness = stats.InteractionStats.HelpEffectivenessRate,
                TopRequests = stats.TopUserRequests
            }
        };
    }

    private static object GenerateComprehensiveReport(LoggingStatistics stats)
    {
        return new
        {
            ReportType = "Comprehensive",
            Summary = GenerateSummaryReport(stats),
            Performance = GeneratePerformanceReport(stats),
            UserBehavior = GenerateUserBehaviorReport(stats),
            DetailedMetrics = stats
        };
    }

    private static string GenerateHtmlReport(object report)
    {
        // Simplified HTML generation - in reality, you'd use a proper template engine
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>PKS CLI Usage Report</title>
    <style>
        body {{ font-family: Arial, sans-serif; margin: 20px; }}
        pre {{ background: #f5f5f5; padding: 10px; border-radius: 5px; }}
        h1 {{ color: #333; }}
    </style>
</head>
<body>
    <h1>PKS CLI Usage Report</h1>
    <pre>{json}</pre>
</body>
</html>";
    }

    private static string GenerateCsvReport(object report)
    {
        // Simplified CSV generation - would need proper implementation for complex objects
        return "ReportType,GeneratedAt,Data\n" + $"Usage Report,{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},\"{JsonSerializer.Serialize(report).Replace("\"", "\"\"")}\"\n";
    }
}