namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for collecting anonymous telemetry data
/// </summary>
public interface ITelemetryService
{
    /// <summary>
    /// Gets anonymous telemetry data for inclusion in reports
    /// </summary>
    /// <returns>Telemetry data</returns>
    Task<TelemetryData> GetTelemetryDataAsync();

    /// <summary>
    /// Records a command usage event
    /// </summary>
    /// <param name="commandName">Name of the command executed</param>
    /// <param name="success">Whether the command succeeded</param>
    /// <param name="duration">How long the command took</param>
    Task RecordCommandUsageAsync(string commandName, bool success, TimeSpan duration);

    /// <summary>
    /// Records an error event
    /// </summary>
    /// <param name="errorType">Type of error</param>
    /// <param name="commandName">Command where error occurred</param>
    Task RecordErrorAsync(string errorType, string commandName);

    /// <summary>
    /// Gets usage statistics
    /// </summary>
    /// <returns>Usage statistics</returns>
    Task<UsageStatistics> GetUsageStatisticsAsync();

    /// <summary>
    /// Checks if telemetry is enabled by user preference
    /// </summary>
    /// <returns>True if telemetry is enabled</returns>
    Task<bool> IsTelemetryEnabledAsync();

    /// <summary>
    /// Sets telemetry preference
    /// </summary>
    /// <param name="enabled">Whether to enable telemetry</param>
    Task SetTelemetryEnabledAsync(bool enabled);
}

/// <summary>
/// Anonymous telemetry data
/// </summary>
public class TelemetryData
{
    public UsageStatistics Usage { get; set; } = new();
    public ErrorStatistics Errors { get; set; } = new();
    public PerformanceMetrics Performance { get; set; } = new();
    public FeatureUsage Features { get; set; } = new();
    public DateTime CollectedAt { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Usage statistics
/// </summary>
public class UsageStatistics
{
    public Dictionary<string, int> CommandCounts { get; set; } = new();
    public int TotalCommands { get; set; }
    public TimeSpan TotalUsageTime { get; set; }
    public DateTime FirstUsed { get; set; }
    public DateTime LastUsed { get; set; }
    public int DaysActive { get; set; }
    public string MostUsedCommand { get; set; } = string.Empty;
}

/// <summary>
/// Error statistics
/// </summary>
public class ErrorStatistics
{
    public Dictionary<string, int> ErrorTypes { get; set; } = new();
    public Dictionary<string, int> ErrorsByCommand { get; set; } = new();
    public int TotalErrors { get; set; }
    public string MostCommonError { get; set; } = string.Empty;
    public DateTime LastError { get; set; }
}

/// <summary>
/// Performance metrics
/// </summary>
public class PerformanceMetrics
{
    public Dictionary<string, TimeSpan> AverageCommandDuration { get; set; } = new();
    public TimeSpan FastestCommand { get; set; }
    public TimeSpan SlowestCommand { get; set; }
    public string FastestCommandName { get; set; } = string.Empty;
    public string SlowestCommandName { get; set; } = string.Empty;
}

/// <summary>
/// Feature usage tracking
/// </summary>
public class FeatureUsage
{
    public bool UsedAgenticFeatures { get; set; }
    public bool UsedMcpIntegration { get; set; }
    public bool UsedDevcontainers { get; set; }
    public bool UsedGitHubIntegration { get; set; }
    public bool UsedKubernetesDeployment { get; set; }
    public Dictionary<string, int> TemplateUsage { get; set; } = new();
    public Dictionary<string, bool> ExperimentalFeatures { get; set; } = new();
}

/// <summary>
/// Implementation of telemetry service
/// </summary>
public class TelemetryService : ITelemetryService
{
    private readonly IConfigurationService _configurationService;
    private readonly string _sessionId;
    private readonly Dictionary<string, int> _commandCounts = new();
    private readonly Dictionary<string, int> _errorCounts = new();
    private readonly Dictionary<string, List<TimeSpan>> _commandDurations = new();
    private readonly Dictionary<string, int> _errorsByCommand = new();

    public TelemetryService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
        _sessionId = Guid.NewGuid().ToString("N")[..8]; // Short session ID
    }

    public async Task<TelemetryData> GetTelemetryDataAsync()
    {
        var isEnabled = await IsTelemetryEnabledAsync();

        if (!isEnabled)
        {
            return new TelemetryData
            {
                IsEnabled = false,
                CollectedAt = DateTime.UtcNow,
                SessionId = _sessionId
            };
        }

        var usage = await GetUsageStatisticsAsync();

        return new TelemetryData
        {
            Usage = usage,
            Errors = new ErrorStatistics
            {
                ErrorTypes = new Dictionary<string, int>(_errorCounts),
                ErrorsByCommand = new Dictionary<string, int>(_errorsByCommand),
                TotalErrors = _errorCounts.Values.Sum(),
                MostCommonError = _errorCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "",
                LastError = DateTime.UtcNow // This would be tracked properly in a real implementation
            },
            Performance = CalculatePerformanceMetrics(),
            Features = await GetFeatureUsageAsync(),
            CollectedAt = DateTime.UtcNow,
            SessionId = _sessionId,
            IsEnabled = true
        };
    }

    public async Task RecordCommandUsageAsync(string commandName, bool success, TimeSpan duration)
    {
        if (!await IsTelemetryEnabledAsync()) return;

        _commandCounts[commandName] = _commandCounts.GetValueOrDefault(commandName, 0) + 1;

        if (!_commandDurations.ContainsKey(commandName))
        {
            _commandDurations[commandName] = new List<TimeSpan>();
        }
        _commandDurations[commandName].Add(duration);

        if (!success)
        {
            _errorsByCommand[commandName] = _errorsByCommand.GetValueOrDefault(commandName, 0) + 1;
        }

        // In a real implementation, this would persist to storage
        await Task.CompletedTask;
    }

    public async Task RecordErrorAsync(string errorType, string commandName)
    {
        if (!await IsTelemetryEnabledAsync()) return;

        _errorCounts[errorType] = _errorCounts.GetValueOrDefault(errorType, 0) + 1;
        _errorsByCommand[commandName] = _errorsByCommand.GetValueOrDefault(commandName, 0) + 1;

        // In a real implementation, this would persist to storage
        await Task.CompletedTask;
    }

    public async Task<UsageStatistics> GetUsageStatisticsAsync()
    {
        // In a real implementation, this would load from persistent storage
        await Task.Delay(10);

        return new UsageStatistics
        {
            CommandCounts = new Dictionary<string, int>(_commandCounts),
            TotalCommands = _commandCounts.Values.Sum(),
            TotalUsageTime = TimeSpan.FromMinutes(_commandCounts.Values.Sum() * 2), // Estimated
            FirstUsed = DateTime.UtcNow.AddDays(-30), // Mock data
            LastUsed = DateTime.UtcNow,
            DaysActive = 15, // Mock data
            MostUsedCommand = _commandCounts.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "init"
        };
    }

    public async Task<bool> IsTelemetryEnabledAsync()
    {
        var setting = await _configurationService.GetAsync("telemetry.enabled");
        return setting?.ToLowerInvariant() != "false"; // Default to enabled unless explicitly disabled
    }

    public async Task SetTelemetryEnabledAsync(bool enabled)
    {
        await _configurationService.SetAsync("telemetry.enabled", enabled.ToString().ToLowerInvariant());
    }

    private PerformanceMetrics CalculatePerformanceMetrics()
    {
        var metrics = new PerformanceMetrics();

        foreach (var command in _commandDurations)
        {
            if (command.Value.Count > 0)
            {
                var average = TimeSpan.FromTicks((long)command.Value.Average(t => t.Ticks));
                metrics.AverageCommandDuration[command.Key] = average;

                var fastest = command.Value.Min();
                var slowest = command.Value.Max();

                if (metrics.FastestCommand == TimeSpan.Zero || fastest < metrics.FastestCommand)
                {
                    metrics.FastestCommand = fastest;
                    metrics.FastestCommandName = command.Key;
                }

                if (slowest > metrics.SlowestCommand)
                {
                    metrics.SlowestCommand = slowest;
                    metrics.SlowestCommandName = command.Key;
                }
            }
        }

        return metrics;
    }

    private async Task<FeatureUsage> GetFeatureUsageAsync()
    {
        // In a real implementation, this would track actual feature usage
        await Task.Delay(5);

        return new FeatureUsage
        {
            UsedAgenticFeatures = _commandCounts.ContainsKey("init") && _commandCounts["init"] > 0,
            UsedMcpIntegration = _commandCounts.ContainsKey("mcp"),
            UsedDevcontainers = _commandCounts.ContainsKey("devcontainer"),
            UsedGitHubIntegration = _commandCounts.ContainsKey("report"),
            UsedKubernetesDeployment = _commandCounts.ContainsKey("deploy"),
            TemplateUsage = new Dictionary<string, int>
            {
                { "console", 3 },
                { "api", 2 },
                { "web", 1 }
            },
            ExperimentalFeatures = new Dictionary<string, bool>
            {
                { "claude-hooks", false },
                { "advanced-mcp", false }
            }
        };
    }
}