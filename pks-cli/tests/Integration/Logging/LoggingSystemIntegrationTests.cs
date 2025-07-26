using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PKS.Infrastructure.Services.Logging;
using PKS.Commands;
using Spectre.Console.Cli;
using Xunit;

namespace PKS.CLI.Tests.Integration.Logging;

/// <summary>
/// Integration tests for the comprehensive logging system
/// </summary>
public class LoggingSystemIntegrationTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly ILoggingOrchestrator _loggingOrchestrator;
    private readonly ICommandTelemetryService _telemetryService;
    private readonly IUserInteractionService _interactionService;
    private readonly ISessionDataService _sessionService;

    public LoggingSystemIntegrationTests()
    {
        var services = new ServiceCollection();
        
        // Register logging
        services.AddLogging(builder => builder.AddProvider(NullLoggerProvider.Instance));
        
        // Register all logging services
        services.AddSingleton<ICommandTelemetryService, CommandTelemetryService>();
        services.AddSingleton<IUserInteractionService, UserInteractionService>();
        services.AddSingleton<ISessionDataService, SessionDataService>();
        services.AddSingleton<ILoggingOrchestrator, LoggingOrchestrator>();
        services.AddSingleton<ICommandLoggingWrapper, CommandLoggingWrapper>();

        _serviceProvider = services.BuildServiceProvider();
        
        _loggingOrchestrator = _serviceProvider.GetRequiredService<ILoggingOrchestrator>();
        _telemetryService = _serviceProvider.GetRequiredService<ICommandTelemetryService>();
        _interactionService = _serviceProvider.GetRequiredService<IUserInteractionService>();
        _sessionService = _serviceProvider.GetRequiredService<ISessionDataService>();
    }

    [Fact]
    public async Task CompleteCommandExecutionFlow_ShouldTrackAllAspects()
    {
        // Arrange
        var commandName = "integration-test";
        var commandArgs = new[] { "--test", "value" };
        var userId = "test-user-123";

        // Act - Initialize command logging
        var context = await _loggingOrchestrator.InitializeCommandLoggingAsync(commandName, commandArgs, userId);

        // Simulate command execution activities
        await _loggingOrchestrator.LogFeatureUsageAsync(context, "feature1", new Dictionary<string, object>
        {
            ["param1"] = "value1",
            ["count"] = 42
        });

        await _loggingOrchestrator.LogUserInteractionAsync(context, "prompt", "Enter value:", "test-input", 1500);

        await _loggingOrchestrator.LogPerformanceMetricsAsync(context, new PerformanceMetrics
        {
            ExecutionTimeMs = 2500,
            MemoryUsageMb = 45.2,
            CpuUsagePercent = 35.8
        });

        // Simulate an error and recovery
        try
        {
            throw new InvalidOperationException("Test error for integration test");
        }
        catch (Exception ex)
        {
            await _loggingOrchestrator.LogErrorAsync(context, ex, "retry", true);
        }

        // Finalize command execution
        await _loggingOrchestrator.FinalizeCommandLoggingAsync(context, true, null, "Integration test completed successfully");

        // Assert - Verify data was recorded across all services
        
        // Check telemetry data
        var commandStats = await _telemetryService.GetCommandStatisticsAsync(commandName);
        Assert.Equal(1, commandStats.TotalExecutions);
        Assert.Equal(1, commandStats.SuccessfulExecutions);
        Assert.Contains("feature1", commandStats.FeatureUsageCount.Keys);

        // Check recent executions
        var recentCommands = await _telemetryService.GetRecentCommandExecutionsAsync(10, commandName);
        var commandRecord = recentCommands.First();
        Assert.Equal(commandName, commandRecord.CommandName);
        Assert.True(commandRecord.Success);
        Assert.Single(commandRecord.FeaturesUsed);
        Assert.Equal("feature1", commandRecord.FeaturesUsed[0].FeatureName);

        // Check user interaction data
        var interactionStats = await _interactionService.GetUserInteractionAnalyticsAsync(userId);
        Assert.True(interactionStats.TotalInteractions >= 2); // At least prompt + error interaction

        // Check session data
        var sessionInfo = await _sessionService.GetSessionAsync(context.SessionId);
        Assert.NotNull(sessionInfo);
        Assert.Equal(userId, sessionInfo.UserId);
        Assert.True(sessionInfo.CommandCount >= 1);
        Assert.True(sessionInfo.InteractionCount >= 1);
    }

    [Fact]
    public async Task MultipleCommandExecutions_ShouldAggregateCorrectly()
    {
        // Arrange
        var userId = "multi-test-user";
        var commands = new[]
        {
            ("command1", new[] { "--arg1" }, true),
            ("command2", new[] { "--arg2", "value" }, false),
            ("command1", new[] { "--arg3" }, true),
            ("command3", Array.Empty<string>(), true)
        };

        // Act - Execute multiple commands
        var contexts = new List<CommandExecutionContext>();
        
        foreach (var (commandName, args, success) in commands)
        {
            var context = await _loggingOrchestrator.InitializeCommandLoggingAsync(commandName, args, userId);
            contexts.Add(context);

            await _loggingOrchestrator.LogFeatureUsageAsync(context, $"feature_{commandName}");
            await _loggingOrchestrator.LogUserInteractionAsync(context, "execution", $"Running {commandName}", "ok", 500);

            if (!success)
            {
                var error = new Exception($"Simulated error in {commandName}");
                await _loggingOrchestrator.LogErrorAsync(context, error, "ignore", false);
            }

            await _loggingOrchestrator.FinalizeCommandLoggingAsync(context, success, success ? null : "Simulated failure");
        }

        // Assert - Check aggregated statistics
        var overallStats = await _loggingOrchestrator.GetLoggingStatisticsAsync();
        
        Assert.Equal(4, overallStats.CommandStats.TotalExecutions);
        Assert.Equal(3, overallStats.CommandStats.SuccessfulExecutions);
        Assert.Equal(1, overallStats.CommandStats.FailedExecutions);
        Assert.Equal(0.75, overallStats.CommandStats.SuccessRate);

        // Check command usage distribution
        Assert.Equal(2, overallStats.CommandStats.CommandUsageCount["command1"]); // Executed twice
        Assert.Equal(1, overallStats.CommandStats.CommandUsageCount["command2"]);
        Assert.Equal(1, overallStats.CommandStats.CommandUsageCount["command3"]);

        // Check feature usage
        Assert.Equal(4, overallStats.CommandStats.FeatureUsageCount.Values.Sum()); // One feature per command

        // Check interaction analytics
        Assert.True(overallStats.InteractionStats.TotalInteractions >= 4); // At least one interaction per command
        Assert.Equal(1, overallStats.SessionStats.TotalSessions); // Should be one session for the user
    }

    [Fact]
    public async Task SessionManagement_ShouldTrackUserSessions()
    {
        // Arrange
        var userId = "session-test-user";
        var clientInfo = new ClientInfo
        {
            OperatingSystem = "Test OS",
            DotNetVersion = "8.0.0",
            CliVersion = "1.0.0"
        };

        // Act - Create session and execute commands
        var sessionId = await _sessionService.StartSessionAsync(userId, clientInfo);

        // Store some session data
        await _sessionService.SetSessionDataAsync(sessionId, "user_preference", "dark_theme", true);
        await _sessionService.SetSessionDataAsync(sessionId, "last_action", "init_project");

        // Update session activity
        await _sessionService.UpdateSessionActivityAsync(sessionId, "command_execution");
        await _sessionService.UpdateSessionActivityAsync(sessionId, "user_interaction");

        // End session
        await _sessionService.EndSessionAsync(sessionId, "normal");

        // Assert - Verify session data
        var sessionInfo = await _sessionService.GetSessionAsync(sessionId);
        Assert.NotNull(sessionInfo);
        Assert.Equal(userId, sessionInfo.UserId);
        Assert.Equal("ended", sessionInfo.Status);
        Assert.Equal("normal", sessionInfo.EndReason);
        Assert.True(sessionInfo.DurationMs > 0);
        Assert.True(sessionInfo.ActivityLog.Count >= 2);

        // Check persistent session data
        Assert.Contains("user_preference", sessionInfo.SessionData.Keys);
        Assert.Contains("last_action", sessionInfo.SessionData.Keys);

        // Check user session history
        var userSessions = await _sessionService.GetUserSessionHistoryAsync(userId, 10);
        Assert.Contains(sessionInfo, userSessions);
    }

    [Fact]
    public async Task LoggingSystemPerformance_ShouldHandleHighVolume()
    {
        // Arrange
        var commandCount = 100;
        var userId = "performance-test-user";
        var tasks = new List<Task>();

        // Act - Execute many commands concurrently
        for (int i = 0; i < commandCount; i++)
        {
            var commandIndex = i;
            var task = Task.Run(async () =>
            {
                var context = await _loggingOrchestrator.InitializeCommandLoggingAsync(
                    $"perf-command-{commandIndex % 10}", 
                    new[] { $"--index={commandIndex}" }, 
                    userId);

                await _loggingOrchestrator.LogFeatureUsageAsync(context, "performance_test");
                await _loggingOrchestrator.LogUserInteractionAsync(context, "batch_execution", "processing", "done", 100);
                
                var success = commandIndex % 20 != 0; // 5% failure rate
                await _loggingOrchestrator.FinalizeCommandLoggingAsync(context, success);
            });
            
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);

        // Assert - Verify all commands were processed
        var stats = await _loggingOrchestrator.GetLoggingStatisticsAsync();
        
        Assert.Equal(commandCount, stats.CommandStats.TotalExecutions);
        Assert.Equal(95, stats.CommandStats.SuccessfulExecutions); // 95% success rate
        Assert.Equal(5, stats.CommandStats.FailedExecutions); // 5% failure rate
        Assert.Equal(0.95, stats.CommandStats.SuccessRate);
        
        // Check that different command names were recorded
        Assert.True(stats.CommandStats.CommandUsageCount.Count <= 10); // At most 10 different command names
        Assert.True(stats.CommandStats.CommandUsageCount.Values.Sum() == commandCount);
    }

    [Fact]
    public async Task ReportGeneration_ShouldProduceComprehensiveReport()
    {
        // Arrange - Create some test data
        var userId = "report-test-user";
        var context = await _loggingOrchestrator.InitializeCommandLoggingAsync("report-test", new[] { "--format=json" }, userId);
        
        await _loggingOrchestrator.LogFeatureUsageAsync(context, "report_generation");
        await _loggingOrchestrator.LogUserInteractionAsync(context, "configuration", "Select format", "json", 800);
        await _loggingOrchestrator.FinalizeCommandLoggingAsync(context, true);

        // Act - Generate different types of reports
        var fromDate = DateTime.UtcNow.AddHours(-1);
        var toDate = DateTime.UtcNow;

        var summaryReport = await _loggingOrchestrator.GenerateUsageReportAsync("summary", fromDate, toDate, "json");
        var detailedReport = await _loggingOrchestrator.GenerateUsageReportAsync("detailed", fromDate, toDate, "json");
        var performanceReport = await _loggingOrchestrator.GenerateUsageReportAsync("performance", fromDate, toDate, "json");

        // Assert - Verify reports contain expected data
        Assert.NotNull(summaryReport);
        Assert.NotEmpty(summaryReport);
        Assert.Contains("Summary", summaryReport);
        Assert.Contains("report-test", summaryReport);

        Assert.NotNull(detailedReport);
        Assert.NotEmpty(detailedReport);
        Assert.Contains("Detailed", detailedReport);
        Assert.Contains("CommandStatistics", detailedReport);

        Assert.NotNull(performanceReport);
        Assert.NotEmpty(performanceReport);
        Assert.Contains("Performance", performanceReport);
        Assert.Contains("ExecutionMetrics", performanceReport);

        // Verify JSON format
        Assert.True(summaryReport.StartsWith("{") && summaryReport.EndsWith("}"));
        Assert.True(detailedReport.StartsWith("{") && detailedReport.EndsWith("}"));
        Assert.True(performanceReport.StartsWith("{") && performanceReport.EndsWith("}"));
    }

    [Fact]
    public async Task DataCleanup_ShouldRemoveOldData()
    {
        // Arrange - Create some test data that should be cleaned up
        var oldUserId = "cleanup-test-user";
        var context = await _loggingOrchestrator.InitializeCommandLoggingAsync("cleanup-test", Array.Empty<string>(), oldUserId);
        await _loggingOrchestrator.FinalizeCommandLoggingAsync(context, true);

        // Act - Perform cleanup with aggressive retention policy
        var retentionPolicy = new LogRetentionPolicy
        {
            SessionDataRetention = TimeSpan.FromMilliseconds(1), // Very short retention
            CommandTelemetryRetention = TimeSpan.FromMilliseconds(1),
            UserInteractionRetention = TimeSpan.FromMilliseconds(1)
        };

        // Wait a bit to ensure data is old enough
        await Task.Delay(10);

        var dryRunSummary = await _loggingOrchestrator.CleanupLoggingDataAsync(retentionPolicy, dryRun: true);
        var actualSummary = await _loggingOrchestrator.CleanupLoggingDataAsync(retentionPolicy, dryRun: false);

        // Assert - Verify cleanup was performed
        Assert.NotNull(dryRunSummary);
        Assert.True(dryRunSummary.DryRun);
        Assert.NotEmpty(dryRunSummary.CleanupActions);

        Assert.NotNull(actualSummary);
        Assert.False(actualSummary.DryRun);
        Assert.NotEmpty(actualSummary.CleanupActions);
        Assert.True(actualSummary.SessionRecordsCleaned >= 0);
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
    }
}