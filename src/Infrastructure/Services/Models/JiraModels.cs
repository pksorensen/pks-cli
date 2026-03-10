namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Configuration for Jira Cloud OAuth2 authentication
/// </summary>
public class JiraAuthConfig
{
    public string CloudOAuthAuthorizeUrl { get; set; } = "https://auth.atlassian.com/authorize";
    public string CloudOAuthTokenUrl { get; set; } = "https://auth.atlassian.com/oauth/token";
    public string CloudApiBaseUrl { get; set; } = "https://api.atlassian.com";
    public int CallbackTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Supported Jira authentication methods
/// </summary>
public enum JiraAuthMethod
{
    ApiToken,
    OAuth
}

/// <summary>
/// Jira deployment type — Cloud vs Server/Data Center (on-premise)
/// </summary>
public enum JiraDeploymentType
{
    Cloud,
    Server // Jira Server or Data Center (on-premise)
}

/// <summary>
/// Persisted Jira credentials
/// </summary>
public class JiraStoredCredentials
{
    public JiraAuthMethod AuthMethod { get; set; }
    public JiraDeploymentType DeploymentType { get; set; } = JiraDeploymentType.Cloud;
    public string BaseUrl { get; set; } = string.Empty; // e.g. https://mycompany.atlassian.net
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty; // for Server basic auth (username, not email)
    public string ApiToken { get; set; } = string.Empty; // for token auth
    public string AccessToken { get; set; } = string.Empty; // for OAuth
    public string RefreshToken { get; set; } = string.Empty; // for OAuth
    public string CloudId { get; set; } = string.Empty; // Atlassian Cloud ID
    public DateTime CreatedAt { get; set; }
    public DateTime LastRefreshedAt { get; set; }
}

/// <summary>
/// Jira project summary
/// </summary>
public class JiraProject
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Jira issue with optional parent and children
/// </summary>
public class JiraIssue
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string? ParentKey { get; set; }
    public string? ParentSummary { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string? Assignee { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Labels { get; set; } = new();
    public List<string> Components { get; set; } = new();
    public string? OriginalEstimate { get; set; } // e.g. "2d", "8h"
    public int? OriginalEstimateSeconds { get; set; }
    public string? TimeSpent { get; set; } // e.g. "1d 4h"
    public int? TimeSpentSeconds { get; set; }
    public double? StoryPoints { get; set; }
    public string? Reporter { get; set; }
    public string? Resolution { get; set; }
    public DateTime? Created { get; set; }
    public DateTime? Updated { get; set; }
    public List<JiraIssue> Children { get; set; } = new();
}

/// <summary>
/// Result from a Jira JQL search
/// </summary>
public class JiraSearchResult
{
    public int Total { get; set; }
    public List<JiraIssue> Issues { get; set; } = new();
}

/// <summary>
/// A saved JQL filter with a human-readable label
/// </summary>
public class JiraSavedFilter
{
    public string Label { get; set; } = string.Empty;
    public string Jql { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public DateTime SavedAt { get; set; }
}
