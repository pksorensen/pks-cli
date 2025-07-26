using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PKS.Infrastructure.Services.Logging;
using Xunit;
using Moq;

namespace PKS.CLI.Tests.Infrastructure.Services.Logging;

public class LoggingOrchestratorTests
{
    private readonly Mock<ICommandTelemetryService> _mockTelemetryService;
    private readonly Mock<IUserInteractionService> _mockInteractionService;
    private readonly Mock<ISessionDataService> _mockSessionService;
    private readonly LoggingOrchestrator _orchestrator;
    private readonly ILogger<LoggingOrchestrator> _logger;

    public LoggingOrchestratorTests()
    {
        _mockTelemetryService = new Mock<ICommandTelemetryService>();
        _mockInteractionService = new Mock<IUserInteractionService>();
        _mockSessionService = new Mock<ISessionDataService>();
        _logger = NullLogger<LoggingOrchestrator>.Instance;

        _orchestrator = new LoggingOrchestrator(
            _logger,
            _mockTelemetryService.Object,
            _mockInteractionService.Object,
            _mockSessionService.Object);
    }

    [Fact]
    public async Task InitializeCommandLoggingAsync_ShouldCreateContext()
    {
        // Arrange
        var commandName = "test-command";
        var commandArgs = new[] { "--arg1", "value1" };
        var userId = "test-user";
        var correlationId = "test-correlation-id";
        var sessionId = "test-session-id";

        _mockSessionService.Setup(s => s.StartSessionAsync(userId, It.IsAny<ClientInfo>()))
            .ReturnsAsync(sessionId);
        _mockTelemetryService.Setup(t => t.StartCommandExecutionAsync(commandName, commandArgs, userId))
            .ReturnsAsync(correlationId);

        // Act
        var context = await _orchestrator.InitializeCommandLoggingAsync(commandName, commandArgs, userId);

        // Assert
        Assert.NotNull(context);
        Assert.Equal(correlationId, context.CorrelationId);
        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(commandName, context.CommandName);
        Assert.Equal(commandArgs, context.CommandArgs);
        Assert.Equal(userId, context.UserId);
        Assert.True(context.Stopwatch.IsRunning);
        Assert.Contains("ProcessId", context.Metadata.Keys);
        Assert.Contains("WorkingDirectory", context.Metadata.Keys);

        // Verify service calls
        _mockSessionService.Verify(s => s.StartSessionAsync(userId, It.IsAny<ClientInfo>()), Times.Once);
        _mockTelemetryService.Verify(t => t.StartCommandExecutionAsync(commandName, commandArgs, userId), Times.Once);
        _mockSessionService.Verify(s => s.UpdateSessionActivityAsync(sessionId, "command_execution"), Times.Once);
    }

    [Fact]
    public async Task FinalizeCommandLoggingAsync_ShouldCompleteLogging()
    {
        // Arrange
        var context = new CommandExecutionContext
        {
            CorrelationId = "test-correlation-id",
            SessionId = "test-session-id",
            CommandName = "test-command",
            CommandArgs = new[] { "--test" },
            StartTime = DateTime.UtcNow,
            InitialMemoryUsage = 1024 * 1024 // 1MB
        };

        var success = true;
        var outputSummary = "Command completed successfully";

        // Act
        await _orchestrator.FinalizeCommandLoggingAsync(context, success, null, outputSummary);

        // Assert
        Assert.False(context.Stopwatch.IsRunning);

        _mockTelemetryService.Verify(t => t.RecordPerformanceMetricsAsync(
            context.CorrelationId,
            It.IsAny<long>(),
            It.IsAny<double>(),
            It.IsAny<double>()), Times.Once);

        _mockTelemetryService.Verify(t => t.CompleteCommandExecutionAsync(
            context.CorrelationId,
            success,
            null,
            outputSummary), Times.Once);

        _mockSessionService.Verify(s => s.UpdateSessionActivityAsync(
            context.SessionId,
            "command_completed"), Times.Once);

        _mockSessionService.Verify(s => s.SetSessionDataAsync(
            context.SessionId,
            "last_command",
            context.CommandName,
            false), Times.Once);

        _mockSessionService.Verify(s => s.SetSessionDataAsync(
            context.SessionId,
            "last_command_success",
            success,
            false), Times.Once);
    }

    [Fact]
    public async Task LogUserInteractionAsync_ShouldTrackInteraction()
    {
        // Arrange
        var context = CreateTestContext();
        var interactionType = "prompt";
        var promptText = "Enter value:";
        var userResponse = "test-value";
        var responseTime = 1500L;

        // Act
        await _orchestrator.LogUserInteractionAsync(context, interactionType, promptText, userResponse, responseTime);

        // Assert
        _mockInteractionService.Verify(i => i.TrackUserInputAsync(
            context.SessionId,
            interactionType,
            promptText,
            userResponse,
            responseTime), Times.Once);

        _mockSessionService.Verify(s => s.UpdateSessionActivityAsync(
            context.SessionId,
            "user_interaction"), Times.Once);

        Assert.Equal("prompt", context.Metadata["LastInteraction"]);
    }

    [Fact]
    public async Task LogFeatureUsageAsync_ShouldTrackFeature()
    {
        // Arrange
        var context = CreateTestContext();
        var featureName = "advanced-search";
        var featureData = new Dictionary<string, object> { ["filters"] = 3, ["results"] = 25 };

        // Act
        await _orchestrator.LogFeatureUsageAsync(context, featureName, featureData);

        // Assert
        _mockTelemetryService.Verify(t => t.RecordFeatureUsageAsync(
            context.CorrelationId,
            featureName,
            featureData), Times.Once);

        Assert.Equal(featureName, context.Metadata["LastFeature"]);
    }

    [Fact]
    public async Task LogErrorAsync_ShouldTrackError()
    {
        // Arrange
        var context = CreateTestContext();
        var exception = new InvalidOperationException("Test error");
        var userAction = "retry";
        var resolved = true;

        // Act
        await _orchestrator.LogErrorAsync(context, exception, userAction, resolved);

        // Assert
        _mockInteractionService.Verify(i => i.TrackErrorInteractionAsync(
            context.SessionId,
            "InvalidOperationException",
            "Test error",
            userAction,
            resolved), Times.Once);

        _mockSessionService.Verify(s => s.UpdateSessionActivityAsync(
            context.SessionId,
            "error_occurred"), Times.Once);

        _mockSessionService.Verify(s => s.SetSessionDataAsync(
            context.SessionId,
            "last_error",
            It.IsAny<object>(),
            false), Times.Once);

        Assert.Equal("InvalidOperationException", context.Metadata["LastError"]);
    }

    [Fact]
    public async Task LogPerformanceMetricsAsync_ShouldTrackMetrics()
    {
        // Arrange
        var context = CreateTestContext();
        var metrics = new PerformanceMetrics
        {
            ExecutionTimeMs = 2500,
            MemoryUsageMb = 45.5,
            CpuUsagePercent = 35.2,
            CustomMetrics = new Dictionary<string, double> { ["custom"] = 123.45 }
        };

        // Act
        await _orchestrator.LogPerformanceMetricsAsync(context, metrics);

        // Assert
        _mockTelemetryService.Verify(t => t.RecordPerformanceMetricsAsync(
            context.CorrelationId,
            metrics.ExecutionTimeMs,
            metrics.MemoryUsageMb,
            metrics.CpuUsagePercent), Times.Once);

        _mockSessionService.Verify(s => s.SetSessionDataAsync(
            context.SessionId,
            "last_performance_metrics",
            metrics,
            false), Times.Once);

        Assert.Contains("LastMetricsUpdate", context.Metadata.Keys);
        Assert.Equal(metrics.ExecutionTimeMs, context.Metadata["ExecutionTimeMs"]);
        Assert.Equal(metrics.MemoryUsageMb, context.Metadata["MemoryUsageMb"]);
    }

    [Fact]
    public async Task GetLoggingStatisticsAsync_ShouldAggregateAllStats()
    {
        // Arrange
        var fromDate = DateTime.UtcNow.AddDays(-7);
        var toDate = DateTime.UtcNow;
        var userId = "test-user";

        var commandStats = new CommandExecutionStatistics
        {
            TotalExecutions = 100,
            SuccessfulExecutions = 85,
            FailedExecutions = 15,
            AverageExecutionTimeMs = 1500
        };

        var interactionStats = new UserInteractionAnalytics
        {
            TotalInteractions = 250,
            ErrorResolutionRate = 0.8,
            HelpEffectivenessRate = 0.75
        };

        var sessionStats = new SessionReport
        {
            TotalSessions = 25,
            AverageSessionDurationMs = 300000
        };

        _mockTelemetryService.Setup(t => t.GetCommandStatisticsAsync(null, fromDate, toDate))
            .ReturnsAsync(commandStats);
        _mockInteractionService.Setup(i => i.GetUserInteractionAnalyticsAsync(userId, fromDate, toDate))
            .ReturnsAsync(interactionStats);
        _mockSessionService.Setup(s => s.GenerateSessionReportAsync("statistics", fromDate, toDate, userId))
            .ReturnsAsync(sessionStats);

        // Act
        var loggingStats = await _orchestrator.GetLoggingStatisticsAsync(fromDate, toDate, userId);

        // Assert
        Assert.NotNull(loggingStats);
        Assert.Equal(fromDate, loggingStats.FromDate);
        Assert.Equal(toDate, loggingStats.ToDate);
        Assert.Equal(userId, loggingStats.UserId);
        Assert.Equal(commandStats, loggingStats.CommandStats);
        Assert.Equal(interactionStats, loggingStats.InteractionStats);
        Assert.Equal(sessionStats, loggingStats.SessionStats);
        Assert.Equal(commandStats.SuccessRate, loggingStats.OverallSuccessRate);
        Assert.True(loggingStats.UserSatisfactionScore > 0);
        Assert.True(loggingStats.SystemPerformanceScore > 0);
        Assert.NotEmpty(loggingStats.HealthMetrics);
    }

    [Fact]
    public async Task GenerateUsageReportAsync_ShouldCreateFormattedReport()
    {
        // Arrange
        var reportType = "summary";
        var fromDate = DateTime.UtcNow.AddDays(-30);
        var toDate = DateTime.UtcNow;
        var outputFormat = "json";

        // Setup mock statistics
        SetupMockStatistics();

        // Act
        var report = await _orchestrator.GenerateUsageReportAsync(reportType, fromDate, toDate, outputFormat);

        // Assert
        Assert.NotNull(report);
        Assert.NotEmpty(report);
        Assert.Contains("Summary", report); // Should contain report type
        Assert.True(report.Length > 100); // Should be a substantial report
    }

    [Fact]
    public async Task CleanupLoggingDataAsync_ShouldReturnCleanupSummary()
    {
        // Arrange
        var retentionPolicy = new LogRetentionPolicy
        {
            SessionDataRetention = TimeSpan.FromDays(30),
            CommandTelemetryRetention = TimeSpan.FromDays(90)
        };
        var dryRun = true;
        var expectedSessionsCleanedUp = 5;

        _mockSessionService.Setup(s => s.CleanupExpiredSessionsAsync(retentionPolicy.SessionDataRetention, dryRun))
            .ReturnsAsync(expectedSessionsCleanedUp);

        // Act
        var cleanupSummary = await _orchestrator.CleanupLoggingDataAsync(retentionPolicy, dryRun);

        // Assert
        Assert.NotNull(cleanupSummary);
        Assert.Equal(dryRun, cleanupSummary.DryRun);
        Assert.Equal(expectedSessionsCleanedUp, cleanupSummary.SessionRecordsCleaned);
        Assert.NotEmpty(cleanupSummary.CleanupActions);
        Assert.Contains("DRY RUN", cleanupSummary.CleanupActions[0]); // Should indicate dry run
        Assert.True(cleanupSummary.TotalSpaceFreedMb >= 0);
    }

    [Fact]
    public async Task InitializeCommandLoggingAsync_WithException_ShouldThrow()
    {
        // Arrange
        var commandName = "test-command";
        var commandArgs = Array.Empty<string>();

        _mockSessionService.Setup(s => s.StartSessionAsync(It.IsAny<string>(), It.IsAny<ClientInfo>()))
            .ThrowsAsync(new InvalidOperationException("Session creation failed"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _orchestrator.InitializeCommandLoggingAsync(commandName, commandArgs));
    }

    [Fact]
    public async Task FinalizeCommandLoggingAsync_WithException_ShouldNotThrow()
    {
        // Arrange
        var context = CreateTestContext();

        _mockTelemetryService.Setup(t => t.CompleteCommandExecutionAsync(
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("Telemetry service failed"));

        // Act & Assert (should not throw - error should be logged and handled)
        await _orchestrator.FinalizeCommandLoggingAsync(context, true, null, "test");
    }

    private CommandExecutionContext CreateTestContext()
    {
        return new CommandExecutionContext
        {
            CorrelationId = "test-correlation-id",
            SessionId = "test-session-id",
            CommandName = "test-command",
            CommandArgs = new[] { "--test" },
            StartTime = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            InitialMemoryUsage = 1024 * 1024
        };
    }

    private void SetupMockStatistics()
    {
        var commandStats = new CommandExecutionStatistics
        {
            TotalExecutions = 50,
            SuccessfulExecutions = 45,
            FailedExecutions = 5
        };

        var interactionStats = new UserInteractionAnalytics
        {
            TotalInteractions = 100,
            ErrorResolutionRate = 0.9,
            HelpEffectivenessRate = 0.8
        };

        var sessionStats = new SessionReport
        {
            TotalSessions = 10,
            AverageSessionDurationMs = 120000
        };

        _mockTelemetryService.Setup(t => t.GetCommandStatisticsAsync(null, It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(commandStats);
        _mockInteractionService.Setup(i => i.GetUserInteractionAnalyticsAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(interactionStats);
        _mockSessionService.Setup(s => s.GenerateSessionReportAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .ReturnsAsync(sessionStats);
    }
}