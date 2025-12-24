using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PKS.Infrastructure.Services.Logging;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Services.Logging;

public class CommandTelemetryServiceTests
{
    private readonly CommandTelemetryService _service;
    private readonly ILogger<CommandTelemetryService> _logger;

    public CommandTelemetryServiceTests()
    {
        _logger = NullLogger<CommandTelemetryService>.Instance;
        _service = new CommandTelemetryService(_logger);
    }

    [Fact]
    public async Task StartCommandExecutionAsync_ShouldReturnCorrelationId()
    {
        // Arrange
        var commandName = "test-command";
        var commandArgs = new[] { "--arg1", "value1" };
        var userId = "test-user";

        // Act
        var correlationId = await _service.StartCommandExecutionAsync(commandName, commandArgs, userId);

        // Assert
        Assert.NotNull(correlationId);
        Assert.NotEmpty(correlationId);
        Assert.Equal(12, correlationId.Length); // Expected length based on implementation
    }

    [Fact]
    public async Task CompleteCommandExecutionAsync_WithValidCorrelationId_ShouldCompleteSuccessfully()
    {
        // Arrange
        var commandName = "test-command";
        var commandArgs = new[] { "--arg1", "value1" };
        var correlationId = await _service.StartCommandExecutionAsync(commandName, commandArgs);

        // Act & Assert (should not throw)
        await _service.CompleteCommandExecutionAsync(correlationId, true, null, "Test completed");
    }

    [Fact]
    public async Task CompleteCommandExecutionAsync_WithInvalidCorrelationId_ShouldHandleGracefully()
    {
        // Arrange
        var invalidCorrelationId = "invalid-id";

        // Act & Assert (should not throw)
        await _service.CompleteCommandExecutionAsync(invalidCorrelationId, false, "Error", "Failed");
    }

    [Fact]
    public async Task RecordFeatureUsageAsync_ShouldTrackFeatureUsage()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", Array.Empty<string>());
        var featureName = "test-feature";
        var featureData = new Dictionary<string, object> { ["key"] = "value" };

        // Act & Assert (should not throw)
        await _service.RecordFeatureUsageAsync(correlationId, featureName, featureData);
    }

    [Fact]
    public async Task RecordPerformanceMetricsAsync_ShouldUpdateMetrics()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", Array.Empty<string>());
        var executionTime = 1000L;
        var memoryUsage = 50.0;
        var cpuUsage = 25.0;

        // Act & Assert (should not throw)
        await _service.RecordPerformanceMetricsAsync(correlationId, executionTime, memoryUsage, cpuUsage);
    }

    [Fact]
    public async Task GetCommandStatisticsAsync_WithNoData_ShouldReturnEmptyStats()
    {
        // Act
        var stats = await _service.GetCommandStatisticsAsync();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalExecutions);
        Assert.Equal(0, stats.SuccessfulExecutions);
        Assert.Equal(0, stats.FailedExecutions);
    }

    [Fact]
    public async Task GetCommandStatisticsAsync_WithCompletedCommands_ShouldReturnStats()
    {
        // Arrange
        var correlationId1 = await _service.StartCommandExecutionAsync("command1", Array.Empty<string>());
        var correlationId2 = await _service.StartCommandExecutionAsync("command2", Array.Empty<string>());

        await _service.RecordPerformanceMetricsAsync(correlationId1, 1000, 10, 20);
        await _service.RecordPerformanceMetricsAsync(correlationId2, 2000, 15, 30);

        await _service.CompleteCommandExecutionAsync(correlationId1, true);
        await _service.CompleteCommandExecutionAsync(correlationId2, false, "Test error");

        // Act
        var stats = await _service.GetCommandStatisticsAsync();

        // Assert
        Assert.Equal(2, stats.TotalExecutions);
        Assert.Equal(1, stats.SuccessfulExecutions);
        Assert.Equal(1, stats.FailedExecutions);
        Assert.Equal(0.5, stats.SuccessRate);
        Assert.True(stats.AverageExecutionTimeMs > 0);
        Assert.Contains("command1", stats.CommandUsageCount.Keys);
        Assert.Contains("command2", stats.CommandUsageCount.Keys);
    }

    [Fact]
    public async Task GetRecentCommandExecutionsAsync_ShouldReturnRecentCommands()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", new[] { "--test" });
        await _service.CompleteCommandExecutionAsync(correlationId, true);

        // Act
        var recentCommands = await _service.GetRecentCommandExecutionsAsync(10);

        // Assert
        Assert.NotNull(recentCommands);
        var commandList = recentCommands.ToList();
        Assert.Single(commandList);
        Assert.Equal("test-command", commandList[0].CommandName);
        Assert.True(commandList[0].Success);
    }

    [Fact]
    public async Task GetCommandStatisticsAsync_WithCommandNameFilter_ShouldFilterResults()
    {
        // Arrange
        var correlationId1 = await _service.StartCommandExecutionAsync("command1", Array.Empty<string>());
        var correlationId2 = await _service.StartCommandExecutionAsync("command2", Array.Empty<string>());

        await _service.CompleteCommandExecutionAsync(correlationId1, true);
        await _service.CompleteCommandExecutionAsync(correlationId2, true);

        // Act
        var stats = await _service.GetCommandStatisticsAsync("command1");

        // Assert
        Assert.Equal(1, stats.TotalExecutions);
        Assert.Single(stats.CommandUsageCount);
        Assert.Contains("command1", stats.CommandUsageCount.Keys);
        Assert.DoesNotContain("command2", stats.CommandUsageCount.Keys);
    }

    [Fact]
    public async Task GetCommandStatisticsAsync_WithDateFilter_ShouldFilterByDate()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", Array.Empty<string>());
        await _service.CompleteCommandExecutionAsync(correlationId, true);

        var futureDate = DateTime.UtcNow.AddDays(1);

        // Act
        var stats = await _service.GetCommandStatisticsAsync(null, futureDate);

        // Assert
        Assert.Equal(0, stats.TotalExecutions); // Should filter out commands before future date
    }

    [Fact]
    public async Task FeatureUsage_ShouldBeIncludedInCompletedCommand()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", Array.Empty<string>());
        await _service.RecordFeatureUsageAsync(correlationId, "feature1", new Dictionary<string, object> { ["test"] = "data" });
        await _service.RecordFeatureUsageAsync(correlationId, "feature2");
        await _service.CompleteCommandExecutionAsync(correlationId, true);

        // Act
        var recentCommands = await _service.GetRecentCommandExecutionsAsync(1);
        var command = recentCommands.First();

        // Assert
        Assert.Equal(2, command.FeaturesUsed.Count);
        Assert.Contains(command.FeaturesUsed, f => f.FeatureName == "feature1");
        Assert.Contains(command.FeaturesUsed, f => f.FeatureName == "feature2");

        var feature1 = command.FeaturesUsed.First(f => f.FeatureName == "feature1");
        Assert.Contains("test", feature1.FeatureData.Keys);
        Assert.Equal("data", feature1.FeatureData["test"]);
    }

    [Fact]
    public async Task ErrorTypeExtraction_ShouldCategorizeErrors()
    {
        // Arrange
        var correlationId = await _service.StartCommandExecutionAsync("test-command", Array.Empty<string>());
        await _service.CompleteCommandExecutionAsync(correlationId, false, "ArgumentException: Invalid argument");

        // Act
        var stats = await _service.GetCommandStatisticsAsync();

        // Assert
        Assert.Single(stats.ErrorTypeCount);
        Assert.Contains("ArgumentException", stats.ErrorTypeCount.Keys);
    }

    [Fact]
    public async Task MultipleCommandExecutions_ShouldCalculateCorrectStatistics()
    {
        // Arrange - Create multiple commands with varying execution times
        var executionTimes = new[] { 100L, 200L, 300L, 400L, 500L };
        var correlationIds = new List<string>();

        foreach (var time in executionTimes)
        {
            var correlationId = await _service.StartCommandExecutionAsync("perf-test", Array.Empty<string>());
            correlationIds.Add(correlationId);
            await _service.RecordPerformanceMetricsAsync(correlationId, time);
            await _service.CompleteCommandExecutionAsync(correlationId, true);
        }

        // Act
        var stats = await _service.GetCommandStatisticsAsync();

        // Assert
        Assert.Equal(5, stats.TotalExecutions);
        Assert.Equal(5, stats.SuccessfulExecutions);
        Assert.Equal(300L, stats.AverageExecutionTimeMs); // Average of 100,200,300,400,500
        Assert.Equal(300L, stats.MedianExecutionTimeMs); // Median of sorted array
        Assert.Equal(500L, stats.MaxExecutionTimeMs);
        Assert.Equal(100L, stats.MinExecutionTimeMs);
    }
}