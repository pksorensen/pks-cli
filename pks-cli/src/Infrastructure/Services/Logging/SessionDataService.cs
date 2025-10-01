using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Globalization;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Implementation of session data service for managing user sessions and data persistence
/// </summary>
public class SessionDataService : ISessionDataService
{
    private readonly ILogger<SessionDataService> _logger;
    private readonly ConcurrentDictionary<string, SessionInfo> _activeSessions = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object>> _sessionData = new();
    private readonly ConcurrentQueue<SessionInfo> _sessionHistory = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromHours(2);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromMinutes(10);
    private readonly int _maxHistoryRecords = 10000;

    public SessionDataService(ILogger<SessionDataService> logger)
    {
        _logger = logger;

        // Start cleanup timer
        _cleanupTimer = new Timer(CleanupExpiredSessionsCallback, null, _cleanupInterval, _cleanupInterval);

        _logger.LogInformation("SessionDataService initialized with session timeout: {Timeout}", _sessionTimeout);
    }

    public async Task<string> StartSessionAsync(string? userId = null, ClientInfo? clientInfo = null)
    {
        var sessionId = GenerateSessionId(userId);
        var sessionInfo = new SessionInfo
        {
            SessionId = sessionId,
            UserId = userId,
            StartTime = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow,
            Status = "active",
            ClientInfo = clientInfo ?? await GetDefaultClientInfoAsync()
        };

        _activeSessions[sessionId] = sessionInfo;
        _sessionData[sessionId] = new ConcurrentDictionary<string, object>();

        _logger.LogInformation("Started new session: {SessionId} for user: {UserId}", sessionId, userId ?? "anonymous");

        await Task.CompletedTask;
        return sessionId;
    }

    public async Task EndSessionAsync(string sessionId, string reason = "normal")
    {
        if (_activeSessions.TryRemove(sessionId, out var sessionInfo))
        {
            sessionInfo.EndTime = DateTime.UtcNow;
            sessionInfo.Status = "ended";
            sessionInfo.EndReason = reason;
            sessionInfo.DurationMs = (long)(sessionInfo.EndTime.Value - sessionInfo.StartTime).TotalMilliseconds;

            // Copy session data to the session info for history
            if (_sessionData.TryGetValue(sessionId, out var data))
            {
                sessionInfo.SessionData = data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                _sessionData.TryRemove(sessionId, out _);
            }

            // Add to history
            _sessionHistory.Enqueue(sessionInfo);
            await TrimSessionHistoryAsync();

            _logger.LogInformation("Ended session: {SessionId} - Reason: {Reason}, Duration: {Duration}ms",
                sessionId, reason, sessionInfo.DurationMs);
        }
        else
        {
            _logger.LogWarning("Attempted to end unknown session: {SessionId}", sessionId);
        }

        await Task.CompletedTask;
    }

    public async Task UpdateSessionActivityAsync(string sessionId, string activityType)
    {
        if (_activeSessions.TryGetValue(sessionId, out var sessionInfo))
        {
            sessionInfo.LastActivity = DateTime.UtcNow;
            sessionInfo.ActivityLog.Add($"{DateTime.UtcNow:HH:mm:ss} - {activityType}");

            // Keep activity log reasonable size
            if (sessionInfo.ActivityLog.Count > 100)
            {
                sessionInfo.ActivityLog.RemoveRange(0, sessionInfo.ActivityLog.Count - 100);
            }

            // Update counters based on activity type
            switch (activityType.ToLower())
            {
                case "command":
                case "command_execution":
                    sessionInfo.CommandCount++;
                    break;
                case "interaction":
                case "user_input":
                case "prompt":
                    sessionInfo.InteractionCount++;
                    break;
            }

            _logger.LogTrace("Updated activity for session {SessionId}: {ActivityType}", sessionId, activityType);
        }
        else
        {
            _logger.LogWarning("Attempted to update activity for unknown session: {SessionId}", sessionId);
        }

        await Task.CompletedTask;
    }

    public async Task SetSessionDataAsync(string sessionId, string key, object value, bool persist = false)
    {
        if (!_sessionData.TryGetValue(sessionId, out var data))
        {
            _logger.LogWarning("Attempted to set data for unknown session: {SessionId}", sessionId);
            return;
        }

        data[key] = value;

        // If persist is true, also store in session info metadata
        if (persist && _activeSessions.TryGetValue(sessionId, out var sessionInfo))
        {
            sessionInfo.SessionData[key] = value;
        }

        _logger.LogTrace("Set session data for {SessionId}: {Key} (persist: {Persist})", sessionId, key, persist);

        await Task.CompletedTask;
    }

    public async Task<T?> GetSessionDataAsync<T>(string sessionId, string key)
    {
        if (_sessionData.TryGetValue(sessionId, out var data) && data.TryGetValue(key, out var value))
        {
            try
            {
                if (value is T directValue)
                    return directValue;

                if (value is JsonElement jsonElement)
                    return JsonSerializer.Deserialize<T>(jsonElement.GetRawText());

                // Try to convert
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert session data for {SessionId}/{Key} to type {Type}",
                    sessionId, key, typeof(T).Name);
                return default;
            }
        }

        _logger.LogTrace("Session data not found for {SessionId}/{Key}", sessionId, key);
        await Task.CompletedTask;
        return default;
    }

    public async Task RemoveSessionDataAsync(string sessionId, string key)
    {
        if (_sessionData.TryGetValue(sessionId, out var data))
        {
            data.TryRemove(key, out _);
            _logger.LogTrace("Removed session data for {SessionId}: {Key}", sessionId, key);
        }
        else
        {
            _logger.LogWarning("Attempted to remove data from unknown session: {SessionId}", sessionId);
        }

        await Task.CompletedTask;
    }

    public async Task<IEnumerable<SessionInfo>> GetActiveSessionsAsync(bool includeExpired = false)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(_sessionTimeout);
        var sessions = _activeSessions.Values.AsEnumerable();

        if (!includeExpired)
        {
            sessions = sessions.Where(s => s.LastActivity >= cutoffTime);
        }

        var result = sessions.OrderByDescending(s => s.LastActivity).ToArray();

        _logger.LogDebug("Retrieved {Count} active sessions (includeExpired: {IncludeExpired})",
            result.Length, includeExpired);

        await Task.CompletedTask;
        return result;
    }

    public async Task<SessionInfo?> GetSessionAsync(string sessionId)
    {
        // Check active sessions first
        if (_activeSessions.TryGetValue(sessionId, out var activeSession))
        {
            await Task.CompletedTask;
            return activeSession;
        }

        // Check session history
        var historicalSession = _sessionHistory.FirstOrDefault(s => s.SessionId == sessionId);

        _logger.LogTrace("Retrieved session {SessionId}: {Found}", sessionId, historicalSession != null ? "found" : "not found");

        await Task.CompletedTask;
        return historicalSession;
    }

    public async Task<IEnumerable<SessionInfo>> GetUserSessionHistoryAsync(string userId, int limit = 50, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var allSessions = _sessionHistory.ToArray()
            .Concat(_activeSessions.Values.Where(s => s.UserId == userId))
            .Where(s => s.UserId == userId);

        if (fromDate.HasValue)
        {
            allSessions = allSessions.Where(s => s.StartTime >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            allSessions = allSessions.Where(s => s.StartTime <= toDate.Value);
        }

        var result = allSessions
            .OrderByDescending(s => s.StartTime)
            .Take(limit)
            .ToArray();

        _logger.LogDebug("Retrieved {Count} session history records for user {UserId}", result.Length, userId);

        await Task.CompletedTask;
        return result;
    }

    public async Task<SessionReport> GenerateSessionReportAsync(string reportType, DateTime fromDate, DateTime toDate, string? userId = null)
    {
        var allSessions = _sessionHistory.ToArray()
            .Concat(_activeSessions.Values)
            .Where(s => s.StartTime >= fromDate && s.StartTime <= toDate);

        if (!string.IsNullOrEmpty(userId))
        {
            allSessions = allSessions.Where(s => s.UserId == userId);
        }

        var sessions = allSessions.ToArray();

        var report = new SessionReport
        {
            ReportType = reportType,
            GeneratedAt = DateTime.UtcNow,
            FromDate = fromDate,
            ToDate = toDate,
            UserId = userId,
            TotalSessions = sessions.Length,
            ActiveSessions = sessions.Count(s => s.Status == "active"),
            CompletedSessions = sessions.Count(s => s.Status == "ended"),
            ExpiredSessions = sessions.Count(s => s.Status == "expired"),
            ErrorSessions = sessions.Count(s => s.Status == "error")
        };

        if (sessions.Length > 0)
        {
            var completedSessions = sessions.Where(s => s.EndTime.HasValue).ToArray();
            var durations = completedSessions.Select(s => s.DurationMs).Where(d => d > 0).ToArray();

            if (durations.Length > 0)
            {
                Array.Sort(durations);
                report.AverageSessionDurationMs = (long)durations.Average();
                report.MedianSessionDurationMs = durations[durations.Length / 2];
                report.MaxSessionDurationMs = durations.Max();
                report.MinSessionDurationMs = durations.Min();
            }

            report.TotalCommands = sessions.Sum(s => s.CommandCount);
            report.TotalInteractions = sessions.Sum(s => s.InteractionCount);
            report.AverageCommandsPerSession = sessions.Average(s => s.CommandCount);
            report.AverageInteractionsPerSession = sessions.Average(s => s.InteractionCount);

            // Platform analytics
            report.OperatingSystemDistribution = sessions
                .Where(s => s.ClientInfo?.OperatingSystem != null)
                .GroupBy(s => s.ClientInfo!.OperatingSystem)
                .ToDictionary(g => g.Key, g => g.Count());

            report.DotNetVersionDistribution = sessions
                .Where(s => s.ClientInfo?.DotNetVersion != null)
                .GroupBy(s => s.ClientInfo!.DotNetVersion)
                .ToDictionary(g => g.Key, g => g.Count());

            report.CliVersionDistribution = sessions
                .Where(s => s.ClientInfo?.CliVersion != null)
                .GroupBy(s => s.ClientInfo!.CliVersion)
                .ToDictionary(g => g.Key, g => g.Count());

            // Usage patterns
            report.SessionsByHourOfDay = sessions
                .GroupBy(s => s.StartTime.Hour)
                .ToDictionary(g => g.Key, g => g.Count());

            report.SessionsByDayOfWeek = sessions
                .GroupBy(s => s.StartTime.DayOfWeek.ToString())
                .ToDictionary(g => g.Key, g => g.Count());

            report.MostActiveUsers = sessions
                .Where(s => !string.IsNullOrEmpty(s.UserId))
                .GroupBy(s => s.UserId!)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        _logger.LogInformation("Generated {ReportType} session report: {TotalSessions} sessions from {FromDate} to {ToDate}",
            reportType, report.TotalSessions, fromDate, toDate);

        await Task.CompletedTask;
        return report;
    }

    public async Task<int> CleanupExpiredSessionsAsync(TimeSpan maxAge, bool dryRun = false)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
        var expiredSessions = _activeSessions.Values
            .Where(s => s.LastActivity < cutoffTime)
            .ToArray();

        var cleanupCount = 0;

        foreach (var session in expiredSessions)
        {
            if (!dryRun)
            {
                await EndSessionAsync(session.SessionId, "expired");
            }
            cleanupCount++;
        }

        // Also clean up old session history
        var historyArray = _sessionHistory.ToArray();
        var oldHistorySessions = historyArray
            .Where(s => s.StartTime < cutoffTime)
            .ToArray();

        if (!dryRun)
        {
            // Rebuild queue without old sessions
            var newQueue = new ConcurrentQueue<SessionInfo>();
            foreach (var session in historyArray.Except(oldHistorySessions))
            {
                newQueue.Enqueue(session);
            }

            // Replace the queue (this is a bit hacky but works for this implementation)
            while (_sessionHistory.TryDequeue(out _)) { }
            foreach (var session in newQueue)
            {
                _sessionHistory.Enqueue(session);
            }
        }

        cleanupCount += oldHistorySessions.Length;

        _logger.LogInformation("Cleaned up {Count} expired sessions (dryRun: {DryRun}, maxAge: {MaxAge})",
            cleanupCount, dryRun, maxAge);

        await Task.CompletedTask;
        return cleanupCount;
    }

    public async Task<string> ExportSessionDataAsync(string format, DateTime fromDate, DateTime toDate, string? userId = null)
    {
        var allSessions = _sessionHistory.ToArray()
            .Concat(_activeSessions.Values)
            .Where(s => s.StartTime >= fromDate && s.StartTime <= toDate);

        if (!string.IsNullOrEmpty(userId))
        {
            allSessions = allSessions.Where(s => s.UserId == userId);
        }

        var sessions = allSessions.OrderBy(s => s.StartTime).ToArray();

        var exportData = format.ToLower() switch
        {
            "json" => JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }),
            "csv" => ExportToCsv(sessions),
            "xml" => ExportToXml(sessions),
            _ => throw new ArgumentException($"Unsupported export format: {format}")
        };

        _logger.LogInformation("Exported {Count} sessions in {Format} format ({Length} characters)",
            sessions.Length, format, exportData.Length);

        await Task.CompletedTask;
        return exportData;
    }

    private static string GenerateSessionId(string? userId = null)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var randomPart = Guid.NewGuid().ToString("N")[..8];
        var userPart = !string.IsNullOrEmpty(userId) ? $"{userId}-" : "";
        return $"{userPart}{timestamp}-{randomPart}";
    }

    private async Task<ClientInfo> GetDefaultClientInfoAsync()
    {
        var clientInfo = new ClientInfo
        {
            OperatingSystem = Environment.OSVersion.ToString(),
            DotNetVersion = Environment.Version.ToString(),
            CliVersion = "1.0.0", // This would come from assembly version in real implementation
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UserAgent = "PKS-CLI",
            TimeZone = TimeZoneInfo.Local.Id,
            Culture = CultureInfo.CurrentCulture.Name
        };

        // Add some environment variables that might be useful
        var envVars = new[] { "USERNAME", "USER", "COMPUTERNAME", "HOSTNAME", "HOME", "USERPROFILE" };
        foreach (var envVar in envVars)
        {
            var value = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(value))
            {
                clientInfo.EnvironmentVariables[envVar] = value;
            }
        }

        await Task.CompletedTask;
        return clientInfo;
    }

    private async Task TrimSessionHistoryAsync()
    {
        while (_sessionHistory.Count > _maxHistoryRecords)
        {
            if (_sessionHistory.TryDequeue(out var oldSession))
            {
                _logger.LogTrace("Trimmed old session from history: {SessionId} from {StartTime}",
                    oldSession.SessionId, oldSession.StartTime);
            }
        }
        await Task.CompletedTask;
    }

    private void CleanupExpiredSessionsCallback(object? state)
    {
        try
        {
            _ = Task.Run(async () => await CleanupExpiredSessionsAsync(_sessionTimeout, false));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during automatic session cleanup");
        }
    }

    private static string ExportToCsv(SessionInfo[] sessions)
    {
        var csv = new System.Text.StringBuilder();
        csv.AppendLine("SessionId,UserId,StartTime,EndTime,DurationMs,Status,EndReason,CommandCount,InteractionCount,OperatingSystem,DotNetVersion,CliVersion");

        foreach (var session in sessions)
        {
            csv.AppendLine($"{session.SessionId}," +
                          $"{session.UserId ?? ""}," +
                          $"{session.StartTime:yyyy-MM-dd HH:mm:ss}," +
                          $"{session.EndTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""}," +
                          $"{session.DurationMs}," +
                          $"{session.Status}," +
                          $"{session.EndReason}," +
                          $"{session.CommandCount}," +
                          $"{session.InteractionCount}," +
                          $"{session.ClientInfo?.OperatingSystem ?? ""}," +
                          $"{session.ClientInfo?.DotNetVersion ?? ""}," +
                          $"{session.ClientInfo?.CliVersion ?? ""}");
        }

        return csv.ToString();
    }

    private static string ExportToXml(SessionInfo[] sessions)
    {
        var xml = new System.Text.StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.AppendLine("<Sessions>");

        foreach (var session in sessions)
        {
            xml.AppendLine($"  <Session>");
            xml.AppendLine($"    <SessionId>{session.SessionId}</SessionId>");
            xml.AppendLine($"    <UserId>{session.UserId ?? ""}</UserId>");
            xml.AppendLine($"    <StartTime>{session.StartTime:yyyy-MM-ddTHH:mm:ss}</StartTime>");
            xml.AppendLine($"    <EndTime>{session.EndTime?.ToString("yyyy-MM-ddTHH:mm:ss") ?? ""}</EndTime>");
            xml.AppendLine($"    <DurationMs>{session.DurationMs}</DurationMs>");
            xml.AppendLine($"    <Status>{session.Status}</Status>");
            xml.AppendLine($"    <EndReason>{session.EndReason}</EndReason>");
            xml.AppendLine($"    <CommandCount>{session.CommandCount}</CommandCount>");
            xml.AppendLine($"    <InteractionCount>{session.InteractionCount}</InteractionCount>");

            if (session.ClientInfo != null)
            {
                xml.AppendLine($"    <ClientInfo>");
                xml.AppendLine($"      <OperatingSystem>{session.ClientInfo.OperatingSystem}</OperatingSystem>");
                xml.AppendLine($"      <DotNetVersion>{session.ClientInfo.DotNetVersion}</DotNetVersion>");
                xml.AppendLine($"      <CliVersion>{session.ClientInfo.CliVersion}</CliVersion>");
                xml.AppendLine($"    </ClientInfo>");
            }

            xml.AppendLine($"  </Session>");
        }

        xml.AppendLine("</Sessions>");
        return xml.ToString();
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}