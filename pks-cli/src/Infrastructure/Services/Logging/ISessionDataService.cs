using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Service for managing session data storage and reporting
/// </summary>
public interface ISessionDataService
{
    /// <summary>
    /// Start a new user session
    /// </summary>
    /// <param name="userId">Optional user identifier</param>
    /// <param name="clientInfo">Information about the client environment</param>
    /// <returns>Session identifier</returns>
    Task<string> StartSessionAsync(string? userId = null, ClientInfo? clientInfo = null);

    /// <summary>
    /// End a user session
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="reason">Reason for session end (normal, timeout, error, etc.)</param>
    Task EndSessionAsync(string sessionId, string reason = "normal");

    /// <summary>
    /// Update session activity to prevent timeout
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="activityType">Type of activity (command, interaction, etc.)</param>
    Task UpdateSessionActivityAsync(string sessionId, string activityType);

    /// <summary>
    /// Store session data
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="key">Data key</param>
    /// <param name="value">Data value</param>
    /// <param name="persist">Whether to persist data beyond session end</param>
    Task SetSessionDataAsync(string sessionId, string key, object value, bool persist = false);

    /// <summary>
    /// Retrieve session data
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="key">Data key</param>
    /// <returns>Data value or null if not found</returns>
    Task<T?> GetSessionDataAsync<T>(string sessionId, string key);

    /// <summary>
    /// Remove session data
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <param name="key">Data key</param>
    Task RemoveSessionDataAsync(string sessionId, string key);

    /// <summary>
    /// Get all active sessions
    /// </summary>
    /// <param name="includeExpired">Whether to include expired but not ended sessions</param>
    /// <returns>List of active sessions</returns>
    Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync(bool includeExpired = false);

    /// <summary>
    /// Get session details
    /// </summary>
    /// <param name="sessionId">Session identifier</param>
    /// <returns>Session information or null if not found</returns>
    Task<SessionInfo?> GetSessionAsync(string sessionId);

    /// <summary>
    /// Get session history for a user
    /// </summary>
    /// <param name="userId">User identifier</param>
    /// <param name="limit">Maximum number of sessions to return</param>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <returns>List of user sessions</returns>
    Task<IEnumerable<SessionInfo>> GetUserSessionHistoryAsync(string userId, int limit = 50, DateTime? fromDate = null, DateTime? toDate = null);

    /// <summary>
    /// Generate session report
    /// </summary>
    /// <param name="reportType">Type of report (summary, detailed, analytics)</param>
    /// <param name="fromDate">Start date for report</param>
    /// <param name="toDate">End date for report</param>
    /// <param name="userId">Optional user filter</param>
    /// <returns>Session report data</returns>
    Task<SessionReport> GenerateSessionReportAsync(string reportType, DateTime fromDate, DateTime toDate, string? userId = null);

    /// <summary>
    /// Clean up expired sessions and data
    /// </summary>
    /// <param name="maxAge">Maximum age for session data retention</param>
    /// <param name="dryRun">If true, return what would be cleaned without actually cleaning</param>
    /// <returns>Number of sessions cleaned up</returns>
    Task<int> CleanupExpiredSessionsAsync(TimeSpan maxAge, bool dryRun = false);

    /// <summary>
    /// Export session data for analysis
    /// </summary>
    /// <param name="format">Export format (json, csv, xml)</param>
    /// <param name="fromDate">Start date for export</param>
    /// <param name="toDate">End date for export</param>
    /// <param name="userId">Optional user filter</param>
    /// <returns>Exported data as string</returns>
    Task<string> ExportSessionDataAsync(string format, DateTime fromDate, DateTime toDate, string? userId = null);
}

/// <summary>
/// Information about the client environment
/// </summary>
public class ClientInfo
{
    public string OperatingSystem { get; set; } = string.Empty;
    public string DotNetVersion { get; set; } = string.Empty;
    public string CliVersion { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public string Culture { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Represents session information
/// </summary>
public class SessionInfo
{
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime LastActivity { get; set; }
    public string Status { get; set; } = "active"; // active, ended, expired, error
    public string EndReason { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int CommandCount { get; set; }
    public int InteractionCount { get; set; }
    public ClientInfo? ClientInfo { get; set; }
    public Dictionary<string, object> SessionData { get; set; } = new();
    public List<string> ActivityLog { get; set; } = new();
}

/// <summary>
/// Session report data
/// </summary>
public class SessionReport
{
    public string ReportType { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public DateTime FromDate { get; set; }
    public DateTime ToDate { get; set; }
    public string? UserId { get; set; }

    // Summary Statistics
    public int TotalSessions { get; set; }
    public int ActiveSessions { get; set; }
    public int CompletedSessions { get; set; }
    public int ExpiredSessions { get; set; }
    public int ErrorSessions { get; set; }

    // Time Analytics
    public long AverageSessionDurationMs { get; set; }
    public long MedianSessionDurationMs { get; set; }
    public long MaxSessionDurationMs { get; set; }
    public long MinSessionDurationMs { get; set; }

    // Activity Analytics
    public int TotalCommands { get; set; }
    public int TotalInteractions { get; set; }
    public double AverageCommandsPerSession { get; set; }
    public double AverageInteractionsPerSession { get; set; }

    // Platform Analytics
    public Dictionary<string, int> OperatingSystemDistribution { get; set; } = new();
    public Dictionary<string, int> DotNetVersionDistribution { get; set; } = new();
    public Dictionary<string, int> CliVersionDistribution { get; set; } = new();

    // Usage Patterns
    public Dictionary<int, int> SessionsByHourOfDay { get; set; } = new(); // Hour -> Count
    public Dictionary<string, int> SessionsByDayOfWeek { get; set; } = new(); // Day -> Count
    public Dictionary<string, int> MostActiveUsers { get; set; } = new(); // UserId -> SessionCount

    // Additional Data
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
    public string? ExportData { get; set; }
}