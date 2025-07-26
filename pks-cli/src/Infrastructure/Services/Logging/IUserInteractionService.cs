using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Logging;

/// <summary>
/// Service for tracking user interactions and behavior patterns
/// </summary>
public interface IUserInteractionService
{
    /// <summary>
    /// Track user input events (prompts, selections, etc.)
    /// </summary>
    /// <param name="sessionId">Current session identifier</param>
    /// <param name="interactionType">Type of interaction (prompt, selection, input, etc.)</param>
    /// <param name="promptText">The text shown to the user</param>
    /// <param name="userResponse">User's response</param>
    /// <param name="responseTimeMs">Time taken for user to respond</param>
    Task TrackUserInputAsync(string sessionId, string interactionType, string promptText, string userResponse, long responseTimeMs);

    /// <summary>
    /// Track user navigation patterns
    /// </summary>
    /// <param name="sessionId">Current session identifier</param>
    /// <param name="fromCommand">Previous command or state</param>
    /// <param name="toCommand">Current command or state</param>
    /// <param name="navigationPath">Full navigation path</param>
    Task TrackNavigationAsync(string sessionId, string fromCommand, string toCommand, string[] navigationPath);

    /// <summary>
    /// Track user preferences and settings changes
    /// </summary>
    /// <param name="sessionId">Current session identifier</param>
    /// <param name="settingName">Name of the setting changed</param>
    /// <param name="oldValue">Previous value</param>
    /// <param name="newValue">New value</param>
    /// <param name="source">Source of the change (user, auto, default)</param>
    Task TrackPreferenceChangeAsync(string sessionId, string settingName, string? oldValue, string newValue, string source = "user");

    /// <summary>
    /// Track error interactions (what users do when errors occur)
    /// </summary>
    /// <param name="sessionId">Current session identifier</param>
    /// <param name="errorType">Type of error encountered</param>
    /// <param name="errorMessage">Error message shown</param>
    /// <param name="userAction">What the user did (retry, cancel, help, etc.)</param>
    /// <param name="resolved">Whether the user resolved the error</param>
    Task TrackErrorInteractionAsync(string sessionId, string errorType, string errorMessage, string userAction, bool resolved);

    /// <summary>
    /// Track help and documentation usage
    /// </summary>
    /// <param name="sessionId">Current session identifier</param>
    /// <param name="helpType">Type of help requested (command, feature, general)</param>
    /// <param name="helpTopic">Specific topic or command</param>
    /// <param name="viewDurationMs">Time spent viewing help</param>
    /// <param name="helpful">Whether user found the help useful</param>
    Task TrackHelpUsageAsync(string sessionId, string helpType, string helpTopic, long viewDurationMs, bool? helpful = null);

    /// <summary>
    /// Get user interaction patterns and insights
    /// </summary>
    /// <param name="userId">Optional user identifier filter</param>
    /// <param name="fromDate">Optional start date filter</param>
    /// <param name="toDate">Optional end date filter</param>
    /// <returns>User interaction analytics</returns>
    Task<UserInteractionAnalytics> GetUserInteractionAnalyticsAsync(string? userId = null, DateTime? fromDate = null, DateTime? toDate = null);

    /// <summary>
    /// Get most common user workflows
    /// </summary>
    /// <param name="limit">Maximum number of workflows to return</param>
    /// <param name="minOccurrences">Minimum occurrences for a workflow to be included</param>
    /// <returns>List of common workflows</returns>
    Task<IEnumerable<UserWorkflow>> GetCommonWorkflowsAsync(int limit = 10, int minOccurrences = 3);

    /// <summary>
    /// Get user pain points and frequently encountered errors
    /// </summary>
    /// <param name="limit">Maximum number of pain points to return</param>
    /// <returns>List of user pain points</returns>
    Task<IEnumerable<UserPainPoint>> GetUserPainPointsAsync(int limit = 10);
}

/// <summary>
/// Analytics data for user interactions
/// </summary>
public class UserInteractionAnalytics
{
    public int TotalInteractions { get; set; }
    public int UniqueUsers { get; set; }
    public int UniqueSessions { get; set; }
    public long AverageResponseTimeMs { get; set; }
    public long AverageSessionDurationMs { get; set; }
    public Dictionary<string, int> InteractionTypeCount { get; set; } = new();
    public Dictionary<string, int> MostUsedCommands { get; set; } = new();
    public Dictionary<string, int> MostUsedFeatures { get; set; } = new();
    public Dictionary<string, int> CommonErrorTypes { get; set; } = new();
    public Dictionary<string, int> HelpTopicRequests { get; set; } = new();
    public double ErrorResolutionRate { get; set; }
    public double HelpEffectivenessRate { get; set; }
    public List<string> MostCommonNavigationPaths { get; set; } = new();
}

/// <summary>
/// Represents a common user workflow pattern
/// </summary>
public class UserWorkflow
{
    public string WorkflowName { get; set; } = string.Empty;
    public List<string> CommandSequence { get; set; } = new();
    public int Occurrences { get; set; }
    public long AverageDurationMs { get; set; }
    public double SuccessRate { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Represents a user pain point or common issue
/// </summary>
public class UserPainPoint
{
    public string PainPointType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Occurrences { get; set; }
    public double ImpactScore { get; set; }
    public string MostCommonContext { get; set; } = string.Empty;
    public string SuggestedImprovement { get; set; } = string.Empty;
    public List<string> AffectedCommands { get; set; } = new();
}

/// <summary>
/// Represents a single user interaction record
/// </summary>
public class UserInteractionRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public DateTime Timestamp { get; set; }
    public string InteractionType { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string UserInput { get; set; } = string.Empty;
    public string SystemResponse { get; set; } = string.Empty;
    public long ResponseTimeMs { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}