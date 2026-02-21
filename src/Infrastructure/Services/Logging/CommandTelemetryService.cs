using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Implementation of command telemetry service for tracking command executions
/// </summary>
public class CommandTelemetryService : ICommandTelemetryService
{
    private readonly ILogger<CommandTelemetryService> _logger;
    private readonly ConcurrentDictionary<string, CommandExecutionRecord> _activeCommands = new();
    private readonly ConcurrentQueue<CommandExecutionRecord> _completedCommands = new();
    private readonly ConcurrentDictionary<string, List<FeatureUsageRecord>> _featureUsage = new();
    private readonly object _statsLock = new();
    private readonly int _maxRetainedRecords = 10000;

    public CommandTelemetryService(ILogger<CommandTelemetryService> logger)
    {
        _logger = logger;
        _logger.LogInformation("CommandTelemetryService initialized with max retained records: {MaxRecords}", _maxRetainedRecords);
    }

    public async Task<string> StartCommandExecutionAsync(string commandName, string[] commandArgs, string? userId = null)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12]; // Short ID for easier tracking
        var record = new CommandExecutionRecord
        {
            CorrelationId = correlationId,
            CommandName = commandName,
            CommandArgs = commandArgs,
            UserId = userId,
            StartTime = DateTime.UtcNow,
            Metadata = new Dictionary<string, object>
            {
                ["Environment"] = Environment.OSVersion.ToString(),
                ["WorkingDirectory"] = Directory.GetCurrentDirectory(),
                ["ProcessId"] = Environment.ProcessId,
                ["MachineName"] = Environment.MachineName
            }
        };

        _activeCommands[correlationId] = record;
        _featureUsage[correlationId] = new List<FeatureUsageRecord>();

        _logger.LogInformation("Started command execution tracking: {CorrelationId} - {CommandName} {Args}",
            correlationId, commandName, string.Join(" ", commandArgs));

        await Task.CompletedTask;
        return correlationId;
    }

    public async Task CompleteCommandExecutionAsync(string correlationId, bool success, string? error = null, string? outputSummary = null)
    {
        if (!_activeCommands.TryRemove(correlationId, out var record))
        {
            _logger.LogWarning("Attempted to complete unknown command execution: {CorrelationId}", correlationId);
            return;
        }

        record.EndTime = DateTime.UtcNow;
        record.ExecutionTimeMs = (long)(record.EndTime.Value - record.StartTime).TotalMilliseconds;
        record.Success = success;
        record.Error = error;
        record.OutputSummary = outputSummary;

        // Move feature usage to the record
        if (_featureUsage.TryRemove(correlationId, out var features))
        {
            record.FeaturesUsed = features;
        }

        // Add to completed commands queue
        _completedCommands.Enqueue(record);

        // Maintain queue size
        await TrimCompletedCommandsAsync();

        _logger.LogInformation("Completed command execution tracking: {CorrelationId} - Success: {Success}, Duration: {Duration}ms",
            correlationId, success, record.ExecutionTimeMs);

        await Task.CompletedTask;
    }

    public async Task RecordPerformanceMetricsAsync(string correlationId, long executionTimeMs, double memoryUsageMb = 0, double cpuUsagePercent = 0)
    {
        if (_activeCommands.TryGetValue(correlationId, out var record))
        {
            record.ExecutionTimeMs = executionTimeMs;
            record.MemoryUsageMb = memoryUsageMb;
            record.CpuUsagePercent = cpuUsagePercent;
            record.Metadata["LastMetricsUpdate"] = DateTime.UtcNow;

            _logger.LogDebug("Updated performance metrics for {CorrelationId}: {ExecutionTime}ms, {Memory}MB, {CPU}%",
                correlationId, executionTimeMs, memoryUsageMb, cpuUsagePercent);
        }
        else
        {
            _logger.LogWarning("Attempted to record performance metrics for unknown command: {CorrelationId}", correlationId);
        }

        await Task.CompletedTask;
    }

    public async Task RecordFeatureUsageAsync(string correlationId, string featureName, Dictionary<string, object>? featureData = null)
    {
        if (_featureUsage.TryGetValue(correlationId, out var features))
        {
            var featureRecord = new FeatureUsageRecord
            {
                FeatureName = featureName,
                UsageTime = DateTime.UtcNow,
                FeatureData = featureData ?? new Dictionary<string, object>()
            };

            features.Add(featureRecord);

            _logger.LogDebug("Recorded feature usage for {CorrelationId}: {FeatureName}", correlationId, featureName);
        }
        else
        {
            _logger.LogWarning("Attempted to record feature usage for unknown command: {CorrelationId}", correlationId);
        }

        await Task.CompletedTask;
    }

    public async Task<CommandExecutionStatistics> GetCommandStatisticsAsync(string? commandName = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var allRecords = _completedCommands.ToArray().AsEnumerable();

        // Apply filters
        if (!string.IsNullOrEmpty(commandName))
        {
            allRecords = allRecords.Where(r => r.CommandName.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        }

        if (fromDate.HasValue)
        {
            allRecords = allRecords.Where(r => r.StartTime >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            allRecords = allRecords.Where(r => r.StartTime <= toDate.Value);
        }

        var records = allRecords.ToArray();

        if (records.Length == 0)
        {
            return new CommandExecutionStatistics();
        }

        var executionTimes = records.Select(r => r.ExecutionTimeMs).Where(t => t > 0).ToArray();
        Array.Sort(executionTimes);

        var stats = new CommandExecutionStatistics
        {
            TotalExecutions = records.Length,
            SuccessfulExecutions = records.Count(r => r.Success),
            FailedExecutions = records.Count(r => !r.Success),
            AverageExecutionTimeMs = executionTimes.Length > 0 ? (long)executionTimes.Average() : 0,
            MedianExecutionTimeMs = executionTimes.Length > 0 ? executionTimes[executionTimes.Length / 2] : 0,
            MaxExecutionTimeMs = executionTimes.Length > 0 ? executionTimes.Max() : 0,
            MinExecutionTimeMs = executionTimes.Length > 0 ? executionTimes.Min() : 0,
            FirstExecution = records.Min(r => r.StartTime),
            LastExecution = records.Max(r => r.StartTime)
        };

        // Command usage statistics
        stats.CommandUsageCount = records
            .GroupBy(r => r.CommandName)
            .ToDictionary(g => g.Key, g => g.Count());

        // Feature usage statistics
        stats.FeatureUsageCount = records
            .SelectMany(r => r.FeaturesUsed)
            .GroupBy(f => f.FeatureName)
            .ToDictionary(g => g.Key, g => g.Count());

        // Error type statistics
        stats.ErrorTypeCount = records
            .Where(r => !r.Success && !string.IsNullOrEmpty(r.Error))
            .GroupBy(r => ExtractErrorType(r.Error!))
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogDebug("Generated command statistics: {TotalExecutions} total, {SuccessRate:P2} success rate",
            stats.TotalExecutions, stats.SuccessRate);

        await Task.CompletedTask;
        return stats;
    }

    public async Task<IEnumerable<CommandExecutionRecord>> GetRecentCommandExecutionsAsync(int limit = 50, string? commandName = null)
    {
        var allRecords = _completedCommands.ToArray().AsEnumerable();

        if (!string.IsNullOrEmpty(commandName))
        {
            allRecords = allRecords.Where(r => r.CommandName.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        }

        var recentRecords = allRecords
            .OrderByDescending(r => r.StartTime)
            .Take(limit)
            .ToArray();

        _logger.LogDebug("Retrieved {Count} recent command executions (limit: {Limit}, filter: {Filter})",
            recentRecords.Length, limit, commandName ?? "none");

        await Task.CompletedTask;
        return recentRecords;
    }

    private async Task TrimCompletedCommandsAsync()
    {
        while (_completedCommands.Count > _maxRetainedRecords)
        {
            if (_completedCommands.TryDequeue(out var oldRecord))
            {
                _logger.LogTrace("Trimmed old command record: {CorrelationId} from {StartTime}",
                    oldRecord.CorrelationId, oldRecord.StartTime);
            }
        }
        await Task.CompletedTask;
    }

    private static string ExtractErrorType(string error)
    {
        // Simple error type extraction - can be made more sophisticated
        if (string.IsNullOrEmpty(error))
            return "Unknown";

        // Look for exception type names
        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var firstLine = lines[0].Trim();

        // Common patterns
        if (firstLine.Contains("ArgumentException"))
            return "ArgumentException";
        if (firstLine.Contains("FileNotFoundException"))
            return "FileNotFoundException";
        if (firstLine.Contains("DirectoryNotFoundException"))
            return "DirectoryNotFoundException";
        if (firstLine.Contains("UnauthorizedAccessException"))
            return "UnauthorizedAccessException";
        if (firstLine.Contains("TimeoutException"))
            return "TimeoutException";
        if (firstLine.Contains("InvalidOperationException"))
            return "InvalidOperationException";

        // Extract first word if it ends with "Exception"
        var firstWord = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (firstWord.EndsWith("Exception"))
            return firstWord;

        // Generic classification
        if (firstLine.ToLower().Contains("timeout"))
            return "Timeout";
        if (firstLine.ToLower().Contains("not found"))
            return "NotFound";
        if (firstLine.ToLower().Contains("access denied") || firstLine.ToLower().Contains("unauthorized"))
            return "AccessDenied";
        if (firstLine.ToLower().Contains("invalid"))
            return "Invalid";
        if (firstLine.ToLower().Contains("network") || firstLine.ToLower().Contains("connection"))
            return "NetworkError";

        return "Other";
    }
}