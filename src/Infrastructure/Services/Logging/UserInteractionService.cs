using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Implementation of user interaction service for tracking user behavior and interactions
/// </summary>
public class UserInteractionService : IUserInteractionService
{
    private readonly ILogger<UserInteractionService> _logger;
    private readonly ConcurrentQueue<UserInteractionRecord> _interactions = new();
    private readonly ConcurrentDictionary<string, List<string>> _navigationPaths = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, object>> _userPreferences = new();
    private readonly object _analyticsLock = new();
    private readonly int _maxRetainedRecords = 50000;

    public UserInteractionService(ILogger<UserInteractionService> logger)
    {
        _logger = logger;
        _logger.LogInformation("UserInteractionService initialized with max retained records: {MaxRecords}", _maxRetainedRecords);
    }

    public async Task TrackUserInputAsync(string sessionId, string interactionType, string promptText, string userResponse, long responseTimeMs)
    {
        var interaction = new UserInteractionRecord
        {
            SessionId = sessionId,
            Timestamp = DateTime.UtcNow,
            InteractionType = interactionType,
            Context = promptText,
            UserInput = userResponse,
            ResponseTimeMs = responseTimeMs,
            Metadata = new Dictionary<string, object>
            {
                ["PromptLength"] = promptText.Length,
                ["ResponseLength"] = userResponse.Length,
                ["InteractionId"] = Guid.NewGuid().ToString("N")[..8]
            }
        };

        _interactions.Enqueue(interaction);
        await TrimInteractionsAsync();

        _logger.LogDebug("Tracked user input for session {SessionId}: {InteractionType} - Response time: {ResponseTime}ms",
            sessionId, interactionType, responseTimeMs);

        await Task.CompletedTask;
    }

    public async Task TrackNavigationAsync(string sessionId, string fromCommand, string toCommand, string[] navigationPath)
    {
        // Store navigation path for session
        _navigationPaths.AddOrUpdate(sessionId,
            new List<string>(navigationPath),
            (key, existing) =>
            {
                existing.AddRange(navigationPath);
                // Keep only last 50 navigation steps per session
                if (existing.Count > 50)
                {
                    existing.RemoveRange(0, existing.Count - 50);
                }
                return existing;
            });

        var interaction = new UserInteractionRecord
        {
            SessionId = sessionId,
            Timestamp = DateTime.UtcNow,
            InteractionType = "navigation",
            Context = $"From: {fromCommand}",
            UserInput = $"To: {toCommand}",
            SystemResponse = string.Join(" -> ", navigationPath),
            Metadata = new Dictionary<string, object>
            {
                ["FromCommand"] = fromCommand,
                ["ToCommand"] = toCommand,
                ["NavigationDepth"] = navigationPath.Length,
                ["FullPath"] = navigationPath
            }
        };

        _interactions.Enqueue(interaction);
        await TrimInteractionsAsync();

        _logger.LogDebug("Tracked navigation for session {SessionId}: {FromCommand} -> {ToCommand}",
            sessionId, fromCommand, toCommand);

        await Task.CompletedTask;
    }

    public async Task TrackPreferenceChangeAsync(string sessionId, string settingName, string? oldValue, string newValue, string source = "user")
    {
        // Update user preferences tracking
        var userId = ExtractUserIdFromSession(sessionId);
        if (!string.IsNullOrEmpty(userId))
        {
            _userPreferences.AddOrUpdate(userId,
                new Dictionary<string, object> { [settingName] = newValue },
                (key, existing) =>
                {
                    existing[settingName] = newValue;
                    return existing;
                });
        }

        var interaction = new UserInteractionRecord
        {
            SessionId = sessionId,
            UserId = userId,
            Timestamp = DateTime.UtcNow,
            InteractionType = "preference_change",
            Context = $"Setting: {settingName}",
            UserInput = newValue,
            SystemResponse = $"Changed from: {oldValue ?? "null"}",
            Metadata = new Dictionary<string, object>
            {
                ["SettingName"] = settingName,
                ["OldValue"] = oldValue ?? string.Empty,
                ["NewValue"] = newValue,
                ["Source"] = source,
                ["PreferenceCategory"] = CategorizePreference(settingName)
            }
        };

        _interactions.Enqueue(interaction);
        await TrimInteractionsAsync();

        _logger.LogDebug("Tracked preference change for session {SessionId}: {SettingName} = {NewValue} (source: {Source})",
            sessionId, settingName, newValue, source);

        await Task.CompletedTask;
    }

    public async Task TrackErrorInteractionAsync(string sessionId, string errorType, string errorMessage, string userAction, bool resolved)
    {
        var interaction = new UserInteractionRecord
        {
            SessionId = sessionId,
            Timestamp = DateTime.UtcNow,
            InteractionType = "error_interaction",
            Context = $"Error: {errorType}",
            UserInput = userAction,
            SystemResponse = errorMessage,
            Metadata = new Dictionary<string, object>
            {
                ["ErrorType"] = errorType,
                ["UserAction"] = userAction,
                ["Resolved"] = resolved,
                ["ErrorSeverity"] = ClassifyErrorSeverity(errorType, errorMessage),
                ["ErrorCategory"] = CategorizeError(errorType)
            }
        };

        _interactions.Enqueue(interaction);
        await TrimInteractionsAsync();

        _logger.LogDebug("Tracked error interaction for session {SessionId}: {ErrorType} - Action: {UserAction}, Resolved: {Resolved}",
            sessionId, errorType, userAction, resolved);

        await Task.CompletedTask;
    }

    public async Task TrackHelpUsageAsync(string sessionId, string helpType, string helpTopic, long viewDurationMs, bool? helpful = null)
    {
        var interaction = new UserInteractionRecord
        {
            SessionId = sessionId,
            Timestamp = DateTime.UtcNow,
            InteractionType = "help_usage",
            Context = $"Help: {helpType}",
            UserInput = helpTopic,
            SystemResponse = $"Viewed for {viewDurationMs}ms",
            ResponseTimeMs = viewDurationMs,
            Metadata = new Dictionary<string, object>
            {
                ["HelpType"] = helpType,
                ["HelpTopic"] = helpTopic,
                ["ViewDurationMs"] = viewDurationMs,
                ["Helpful"] = helpful ?? false,
                ["HelpCategory"] = CategorizeHelpTopic(helpTopic)
            }
        };

        _interactions.Enqueue(interaction);
        await TrimInteractionsAsync();

        _logger.LogDebug("Tracked help usage for session {SessionId}: {HelpType}/{HelpTopic} - Duration: {ViewDuration}ms, Helpful: {Helpful}",
            sessionId, helpType, helpTopic, viewDurationMs, helpful);

        await Task.CompletedTask;
    }

    public async Task<UserInteractionAnalytics> GetUserInteractionAnalyticsAsync(string? userId = null, DateTime? fromDate = null, DateTime? toDate = null)
    {
        var allInteractions = _interactions.ToArray().AsEnumerable();

        // Apply filters
        if (!string.IsNullOrEmpty(userId))
        {
            allInteractions = allInteractions.Where(i => i.UserId == userId);
        }

        if (fromDate.HasValue)
        {
            allInteractions = allInteractions.Where(i => i.Timestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            allInteractions = allInteractions.Where(i => i.Timestamp <= toDate.Value);
        }

        var interactions = allInteractions.ToArray();

        if (interactions.Length == 0)
        {
            return new UserInteractionAnalytics();
        }

        var analytics = new UserInteractionAnalytics
        {
            TotalInteractions = interactions.Length,
            UniqueUsers = interactions.Where(i => !string.IsNullOrEmpty(i.UserId)).Select(i => i.UserId).Distinct().Count(),
            UniqueSessions = interactions.Select(i => i.SessionId).Distinct().Count(),
            AverageResponseTimeMs = (long)interactions.Where(i => i.ResponseTimeMs > 0).DefaultIfEmpty().Average(i => i?.ResponseTimeMs ?? 0)
        };

        // Calculate session duration (approximate)
        var sessionGroups = interactions.GroupBy(i => i.SessionId);
        var sessionDurations = sessionGroups
            .Where(g => g.Count() > 1)
            .Select(g => (g.Max(i => i.Timestamp) - g.Min(i => i.Timestamp)).TotalMilliseconds)
            .ToArray();

        analytics.AverageSessionDurationMs = sessionDurations.Length > 0 ? (long)sessionDurations.Average() : 0;

        // Interaction type statistics
        analytics.InteractionTypeCount = interactions
            .GroupBy(i => i.InteractionType)
            .ToDictionary(g => g.Key, g => g.Count());

        // Most used commands (from navigation data)
        analytics.MostUsedCommands = interactions
            .Where(i => i.InteractionType == "navigation" && i.Metadata.ContainsKey("ToCommand"))
            .GroupBy(i => i.Metadata["ToCommand"].ToString()!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Feature usage from metadata
        analytics.MostUsedFeatures = interactions
            .Where(i => i.Metadata.ContainsKey("FeatureName"))
            .GroupBy(i => i.Metadata["FeatureName"].ToString()!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Error types
        analytics.CommonErrorTypes = interactions
            .Where(i => i.InteractionType == "error_interaction" && i.Metadata.ContainsKey("ErrorType"))
            .GroupBy(i => i.Metadata["ErrorType"].ToString()!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Help topics
        analytics.HelpTopicRequests = interactions
            .Where(i => i.InteractionType == "help_usage" && i.Metadata.ContainsKey("HelpTopic"))
            .GroupBy(i => i.Metadata["HelpTopic"].ToString()!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Error resolution rate
        var errorInteractions = interactions.Where(i => i.InteractionType == "error_interaction").ToArray();
        analytics.ErrorResolutionRate = errorInteractions.Length > 0
            ? errorInteractions.Count(i => i.Metadata.ContainsKey("Resolved") && (bool)i.Metadata["Resolved"]) / (double)errorInteractions.Length
            : 0;

        // Help effectiveness rate
        var helpInteractions = interactions.Where(i => i.InteractionType == "help_usage" && i.Metadata.ContainsKey("Helpful")).ToArray();
        analytics.HelpEffectivenessRate = helpInteractions.Length > 0
            ? helpInteractions.Count(i => (bool)i.Metadata["Helpful"]) / (double)helpInteractions.Length
            : 0;

        // Common navigation paths
        analytics.MostCommonNavigationPaths = _navigationPaths.Values
            .SelectMany(paths => ExtractCommonSequences(paths, 3))
            .GroupBy(seq => string.Join(" -> ", seq))
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => g.Key)
            .ToList();

        _logger.LogDebug("Generated user interaction analytics: {TotalInteractions} interactions across {UniqueSessions} sessions",
            analytics.TotalInteractions, analytics.UniqueSessions);

        await Task.CompletedTask;
        return analytics;
    }

    public async Task<IEnumerable<UserWorkflow>> GetCommonWorkflowsAsync(int limit = 10, int minOccurrences = 3)
    {
        var workflows = new List<UserWorkflow>();

        // Analyze navigation patterns to identify workflows
        var allPaths = _navigationPaths.Values.ToArray();

        // Extract command sequences of different lengths
        for (int sequenceLength = 3; sequenceLength <= 5; sequenceLength++)
        {
            var sequences = allPaths
                .SelectMany(path => ExtractCommonSequences(path, sequenceLength))
                .GroupBy(seq => string.Join("|", seq))
                .Where(g => g.Count() >= minOccurrences)
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .ToArray();

            foreach (var sequenceGroup in sequences)
            {
                var commandSequence = sequenceGroup.Key.Split('|').ToList();
                var workflow = new UserWorkflow
                {
                    WorkflowName = GenerateWorkflowName(commandSequence),
                    CommandSequence = commandSequence,
                    Occurrences = sequenceGroup.Count(),
                    AverageDurationMs = CalculateAverageWorkflowDuration(commandSequence),
                    SuccessRate = CalculateWorkflowSuccessRate(commandSequence),
                    Description = GenerateWorkflowDescription(commandSequence)
                };

                workflows.Add(workflow);
            }
        }

        var result = workflows
            .OrderByDescending(w => w.Occurrences)
            .ThenByDescending(w => w.SuccessRate)
            .Take(limit)
            .ToArray();

        _logger.LogDebug("Identified {Count} common workflows (min occurrences: {MinOccurrences})",
            result.Length, minOccurrences);

        await Task.CompletedTask;
        return result;
    }

    public async Task<IEnumerable<UserPainPoint>> GetUserPainPointsAsync(int limit = 10)
    {
        var interactions = _interactions.ToArray();
        var painPoints = new List<UserPainPoint>();

        // Analyze error interactions
        var errorInteractions = interactions
            .Where(i => i.InteractionType == "error_interaction")
            .GroupBy(i => i.Metadata.GetValueOrDefault("ErrorType", "Unknown").ToString()!)
            .ToArray();

        foreach (var errorGroup in errorInteractions)
        {
            var errorType = errorGroup.Key;
            var occurrences = errorGroup.Count();
            var resolvedCount = errorGroup.Count(i => i.Metadata.ContainsKey("Resolved") && (bool)i.Metadata["Resolved"]);
            var impactScore = CalculateErrorImpactScore(errorType, occurrences, resolvedCount);

            painPoints.Add(new UserPainPoint
            {
                PainPointType = "Error",
                Description = $"Users frequently encounter {errorType} errors",
                Occurrences = occurrences,
                ImpactScore = impactScore,
                MostCommonContext = GetMostCommonErrorContext(errorGroup),
                SuggestedImprovement = GenerateErrorImprovement(errorType),
                AffectedCommands = GetAffectedCommands(errorGroup)
            });
        }

        // Analyze help usage patterns (frequent requests might indicate UX issues)
        var helpInteractions = interactions
            .Where(i => i.InteractionType == "help_usage")
            .GroupBy(i => i.Metadata.GetValueOrDefault("HelpTopic", "Unknown").ToString()!)
            .Where(g => g.Count() >= 5) // Topics requested frequently
            .ToArray();

        foreach (var helpGroup in helpInteractions)
        {
            var helpTopic = helpGroup.Key;
            var occurrences = helpGroup.Count();
            var helpfulCount = helpGroup.Count(i => i.Metadata.ContainsKey("Helpful") && (bool)i.Metadata["Helpful"]);
            var helpfulnessRate = helpfulCount / (double)occurrences;

            if (helpfulnessRate < 0.7) // Low helpfulness indicates a pain point
            {
                painPoints.Add(new UserPainPoint
                {
                    PainPointType = "Usability",
                    Description = $"Users frequently need help with {helpTopic} (low helpfulness: {helpfulnessRate:P0})",
                    Occurrences = occurrences,
                    ImpactScore = occurrences * (1 - helpfulnessRate),
                    MostCommonContext = helpTopic,
                    SuggestedImprovement = GenerateUsabilityImprovement(helpTopic),
                    AffectedCommands = new List<string> { helpTopic }
                });
            }
        }

        // Analyze response time patterns (very slow responses might indicate UX issues)
        var slowInteractions = interactions
            .Where(i => i.ResponseTimeMs > 10000) // More than 10 seconds
            .GroupBy(i => i.InteractionType)
            .ToArray();

        foreach (var slowGroup in slowInteractions)
        {
            var interactionType = slowGroup.Key;
            var occurrences = slowGroup.Count();
            var averageTime = slowGroup.Average(i => i.ResponseTimeMs);

            painPoints.Add(new UserPainPoint
            {
                PainPointType = "Performance",
                Description = $"Users experience slow response times with {interactionType} interactions",
                Occurrences = occurrences,
                ImpactScore = occurrences * (averageTime / 1000.0), // Impact based on time delay
                MostCommonContext = $"Average response time: {averageTime:F0}ms",
                SuggestedImprovement = GeneratePerformanceImprovement(interactionType),
                AffectedCommands = GetAffectedCommandsFromSlow(slowGroup)
            });
        }

        var result = painPoints
            .OrderByDescending(p => p.ImpactScore)
            .Take(limit)
            .ToArray();

        _logger.LogDebug("Identified {Count} user pain points", result.Length);

        await Task.CompletedTask;
        return result;
    }

    private async Task TrimInteractionsAsync()
    {
        while (_interactions.Count > _maxRetainedRecords)
        {
            if (_interactions.TryDequeue(out var oldInteraction))
            {
                _logger.LogTrace("Trimmed old interaction record from {Timestamp}", oldInteraction.Timestamp);
            }
        }
        await Task.CompletedTask;
    }

    private static string ExtractUserIdFromSession(string sessionId)
    {
        // Simple extraction - in reality this might be more complex
        // For now, assume session format includes user info
        return sessionId.Contains('-') ? sessionId.Split('-')[0] : string.Empty;
    }

    private static string CategorizePreference(string settingName)
    {
        var lower = settingName.ToLower();
        if (lower.Contains("theme") || lower.Contains("color") || lower.Contains("ui"))
            return "UI";
        if (lower.Contains("auth") || lower.Contains("token") || lower.Contains("credential"))
            return "Security";
        if (lower.Contains("timeout") || lower.Contains("retry") || lower.Contains("cache"))
            return "Performance";
        if (lower.Contains("format") || lower.Contains("output") || lower.Contains("display"))
            return "Display";
        return "General";
    }

    private static string ClassifyErrorSeverity(string errorType, string errorMessage)
    {
        var lower = errorType.ToLower();
        if (lower.Contains("critical") || lower.Contains("fatal") || lower.Contains("security"))
            return "Critical";
        if (lower.Contains("error") || lower.Contains("exception") || lower.Contains("fail"))
            return "High";
        if (lower.Contains("warn") || lower.Contains("timeout") || lower.Contains("retry"))
            return "Medium";
        return "Low";
    }

    private static string CategorizeError(string errorType)
    {
        var lower = errorType.ToLower();
        if (lower.Contains("auth") || lower.Contains("unauthorized") || lower.Contains("forbidden"))
            return "Authentication";
        if (lower.Contains("network") || lower.Contains("connection") || lower.Contains("timeout"))
            return "Network";
        if (lower.Contains("file") || lower.Contains("directory") || lower.Contains("path"))
            return "FileSystem";
        if (lower.Contains("argument") || lower.Contains("parameter") || lower.Contains("invalid"))
            return "Input";
        return "System";
    }

    private static string CategorizeHelpTopic(string helpTopic)
    {
        var lower = helpTopic.ToLower();
        if (lower.Contains("command") || lower.Contains("cli"))
            return "Commands";
        if (lower.Contains("config") || lower.Contains("setting") || lower.Contains("preference"))
            return "Configuration";
        if (lower.Contains("auth") || lower.Contains("login") || lower.Contains("token"))
            return "Authentication";
        if (lower.Contains("deploy") || lower.Contains("build") || lower.Contains("publish"))
            return "Deployment";
        if (lower.Contains("debug") || lower.Contains("error") || lower.Contains("troubleshoot"))
            return "Troubleshooting";
        return "General";
    }

    private static List<List<string>> ExtractCommonSequences(List<string> path, int sequenceLength)
    {
        var sequences = new List<List<string>>();
        for (int i = 0; i <= path.Count - sequenceLength; i++)
        {
            sequences.Add(path.GetRange(i, sequenceLength));
        }
        return sequences;
    }

    private static string GenerateWorkflowName(List<string> commandSequence)
    {
        if (commandSequence.Count == 0) return "Unknown Workflow";

        var first = commandSequence[0];
        var last = commandSequence[^1];

        if (commandSequence.Count == 2)
            return $"{first} → {last}";

        return $"{first} → ... → {last} ({commandSequence.Count} steps)";
    }

    private long CalculateAverageWorkflowDuration(List<string> commandSequence)
    {
        // Simplified calculation - in reality would analyze actual timing data
        return commandSequence.Count * 5000; // Assume 5 seconds per command on average
    }

    private double CalculateWorkflowSuccessRate(List<string> commandSequence)
    {
        // Simplified calculation - in reality would analyze success/failure data
        return 0.85; // Assume 85% success rate as baseline
    }

    private static string GenerateWorkflowDescription(List<string> commandSequence)
    {
        if (commandSequence.Count < 2) return "Single command execution";

        var description = $"User workflow involving {commandSequence.Count} commands: ";
        description += string.Join(" → ", commandSequence.Take(3));
        if (commandSequence.Count > 3)
            description += " → ...";

        return description;
    }

    private double CalculateErrorImpactScore(string errorType, int occurrences, int resolvedCount)
    {
        var resolutionRate = resolvedCount / (double)occurrences;
        var severityMultiplier = ClassifyErrorSeverity(errorType, "").ToLower() switch
        {
            "critical" => 5.0,
            "high" => 3.0,
            "medium" => 2.0,
            _ => 1.0
        };

        return occurrences * (1 - resolutionRate) * severityMultiplier;
    }

    private static string GetMostCommonErrorContext(IGrouping<string, UserInteractionRecord> errorGroup)
    {
        return errorGroup
            .GroupBy(i => i.Context)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "Unknown context";
    }

    private static string GenerateErrorImprovement(string errorType)
    {
        return errorType.ToLower() switch
        {
            var et when et.Contains("auth") => "Improve authentication flow and error messages",
            var et when et.Contains("network") => "Add retry mechanisms and better network error handling",
            var et when et.Contains("file") => "Enhance file validation and provide clearer path guidance",
            var et when et.Contains("argument") => "Improve input validation and provide better parameter examples",
            _ => "Enhance error messages and provide actionable guidance"
        };
    }

    private static string GenerateUsabilityImprovement(string helpTopic)
    {
        return $"Improve UI/UX for {helpTopic} to reduce need for help requests";
    }

    private static string GeneratePerformanceImprovement(string interactionType)
    {
        return $"Optimize {interactionType} interactions to reduce response time";
    }

    private static List<string> GetAffectedCommands(IGrouping<string, UserInteractionRecord> errorGroup)
    {
        return errorGroup
            .Where(i => i.Metadata.ContainsKey("Command"))
            .Select(i => i.Metadata["Command"].ToString()!)
            .Distinct()
            .ToList();
    }

    private static List<string> GetAffectedCommandsFromSlow(IGrouping<string, UserInteractionRecord> slowGroup)
    {
        return slowGroup
            .Where(i => i.Metadata.ContainsKey("Command"))
            .Select(i => i.Metadata["Command"].ToString()!)
            .Distinct()
            .ToList();
    }
}