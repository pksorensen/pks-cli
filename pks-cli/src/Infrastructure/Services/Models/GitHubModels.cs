namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Represents a GitHub repository
/// </summary>
public class GitHubRepository
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string Owner { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Configuration result for GitHub project integration
/// </summary>
public class GitHubConfiguration
{
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public DateTime ConfiguredAt { get; set; }
    public bool IsValid { get; set; }
    public string[] TokenScopes { get; set; } = Array.Empty<string>();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of GitHub personal access token validation
/// </summary>
public class GitHubTokenValidation
{
    public bool IsValid { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTime ValidatedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Represents a GitHub issue
/// </summary>
public class GitHubIssue
{
    public long Id { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<GitHubUser> Assignees { get; set; } = new();
    public GitHubMilestone? Milestone { get; set; }
    public GitHubUser? User { get; set; }
}

/// <summary>
/// Represents a GitHub user
/// </summary>
public class GitHubUser
{
    public string Login { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Represents a GitHub milestone
/// </summary>
public class GitHubMilestone
{
    public string Title { get; set; } = string.Empty;
    public int Number { get; set; }
}

/// <summary>
/// Result of git repository initialization
/// </summary>
public class GitInitializationResult
{
    public string ProjectPath { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string InitialCommitMessage { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Steps { get; set; } = new();
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// Project-specific GitHub configuration (simulates project-scoped access)
/// </summary>
public class ProjectGitHubConfig
{
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string[] RequiredScopes { get; set; } = Array.Empty<string>();
    public string ConfigurationName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string[] Instructions { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Repository access level information
/// </summary>
public class GitHubAccessLevel
{
    public string RepositoryUrl { get; set; } = string.Empty;
    public bool HasAccess { get; set; }
    public string AccessLevel { get; set; } = "none"; // none, read, write, admin
    public bool CanWrite { get; set; }
    public bool CanAdmin { get; set; }
    public DateTime CheckedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Request to create a new GitHub repository
/// </summary>
public class CreateRepositoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsPrivate { get; set; } = false;
    public bool AutoInit { get; set; } = true;
    public string? GitignoreTemplate { get; set; }
    public string? LicenseTemplate { get; set; }
    public bool AllowSquashMerge { get; set; } = true;
    public bool AllowMergeCommit { get; set; } = true;
    public bool AllowRebaseMerge { get; set; } = true;
}

/// <summary>
/// Request to create a new GitHub issue
/// </summary>
public class CreateIssueRequest
{
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<string> Assignees { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    public int? Milestone { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
}

/// <summary>
/// Request to setup a workflow in a GitHub repository
/// </summary>
public class WorkflowSetupRequest
{
    public string RepositoryName { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = "main";
    public bool IncludeDeployment { get; set; } = false;
    public string WorkflowName { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string? BranchName { get; set; }
    public Dictionary<string, object> Variables { get; set; } = new();
    public List<string> Triggers { get; set; } = new();
    public Dictionary<string, string> Secrets { get; set; } = new();
}

/// <summary>
/// Comprehensive repository information
/// </summary>
public class GitHubRepositoryInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CloneUrl { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public string Owner { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int StarCount { get; set; }
    public int ForkCount { get; set; }
    public string Language { get; set; } = string.Empty;
    public List<string> Topics { get; set; } = new();
}

/// <summary>
/// Repository activity information
/// </summary>
public class GitHubRepositoryActivity
{
    public int CommitCount { get; set; }
    public int PullRequestCount { get; set; }
    public int IssueCount { get; set; }
    public List<GitHubCommit> RecentCommits { get; set; } = new();
    public List<GitHubIssue> RecentIssues { get; set; } = new();
    public List<GitHubPullRequest> RecentPullRequests { get; set; } = new();
    public List<string> ActiveBranches { get; set; } = new();
    public DateTime LastActivity { get; set; }
}

/// <summary>
/// Represents a GitHub commit
/// </summary>
public class GitHubCommit
{
    public string Sha { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public GitHubCommitDetail Commit { get; set; } = new();
}

/// <summary>
/// Represents GitHub commit details
/// </summary>
public class GitHubCommitDetail
{
    public string Message { get; set; } = string.Empty;
    public GitHubCommitAuthor Author { get; set; } = new();
}

/// <summary>
/// Represents a GitHub commit author
/// </summary>
public class GitHubCommitAuthor
{
    public string Name { get; set; } = string.Empty;
    public DateTime Date { get; set; }
}

/// <summary>
/// Represents a GitHub pull request
/// </summary>
public class GitHubPullRequest
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public GitHubUser? User { get; set; }
}

/// <summary>
/// GitHub workflow template
/// </summary>
public class GitHubWorkflowTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> RequiredSecrets { get; set; } = new();
}

/// <summary>
/// Workflow configuration
/// </summary>
public class WorkflowConfiguration
{
    public string Name { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
    public Dictionary<string, string> Environment { get; set; } = new();
    public List<string> Secrets { get; set; } = new();
}

/// <summary>
/// Result of workflow setup operation
/// </summary>
public class GitHubWorkflowSetupResult
{
    public bool Success { get; set; }
    public string WorkflowName { get; set; } = string.Empty;
    public string WorkflowFile { get; set; } = string.Empty;
    public string WorkflowUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public List<string> Features { get; set; } = new();
    public List<string> Triggers { get; set; } = new();
    public List<string> RequiredSecrets { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// GitHub release information
/// </summary>
public class GitHubRelease
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPreRelease { get; set; }
    public bool IsDraft { get; set; }
    public DateTime PublishedAt { get; set; }
    public string HtmlUrl { get; set; } = string.Empty;
}

/// <summary>
/// Result of creating a repository
/// </summary>
public class CreateRepositoryResult
{
    public bool Success { get; set; }
    public GitHubRepository Repository { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of creating an issue
/// </summary>
public class CreateIssueResult
{
    public bool Success { get; set; }
    public GitHubIssue Issue { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// GitHub workflow template with ID
/// </summary>
public class GitHubWorkflowTemplateInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> RequiredSecrets { get; set; } = new();
}

// === GitHub Device Code Flow Authentication Models ===

/// <summary>
/// Device code flow authentication request
/// </summary>
public class GitHubDeviceCodeRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Device code flow response from GitHub
/// </summary>
public class GitHubDeviceCodeResponse
{
    public string DeviceCode { get; set; } = string.Empty;
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public string VerificationUriComplete { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

/// <summary>
/// Device code authentication status
/// </summary>
public class GitHubDeviceAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTime? ExpiresAt { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public DateTime CheckedAt { get; set; }
}

/// <summary>
/// Token polling request for device code flow
/// </summary>
public class GitHubTokenPollRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string DeviceCode { get; set; } = string.Empty;
    public string GrantType { get; set; } = "urn:ietf:params:oauth:grant-type:device_code";
}

/// <summary>
/// Token response from GitHub OAuth
/// </summary>
public class GitHubTokenResponse
{
    public string AccessToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public string? RefreshToken { get; set; }
    public int? ExpiresIn { get; set; }
}

/// <summary>
/// Comprehensive GitHub authentication configuration
/// </summary>
public class GitHubAuthConfig
{
    public string ClientId { get; set; } = string.Empty;
    public string[] DefaultScopes { get; set; } = { "repo", "user:email", "write:packages" };
    public string DeviceCodeUrl { get; set; } = "https://github.com/login/device/code";
    public string TokenUrl { get; set; } = "https://github.com/login/oauth/access_token";
    public string ApiBaseUrl { get; set; } = "https://api.github.com";
    public int PollingIntervalSeconds { get; set; } = 5;
    public int MaxPollingAttempts { get; set; } = 120; // 10 minutes max
    public string UserAgent { get; set; } = "PKS-CLI/1.0.0";
}

/// <summary>
/// Stored authentication token with metadata
/// </summary>
public class GitHubStoredToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? AssociatedUser { get; set; }
    public bool IsValid { get; set; }
    public DateTime LastValidated { get; set; }
}

/// <summary>
/// GitHub API error response
/// </summary>
public class GitHubApiError
{
    public string Message { get; set; } = string.Empty;
    public string DocumentationUrl { get; set; } = string.Empty;
    public List<GitHubApiErrorDetail> Errors { get; set; } = new();
}

/// <summary>
/// Detailed GitHub API error information
/// </summary>
public class GitHubApiErrorDetail
{
    public string Resource { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Retry policy configuration for GitHub API calls
/// </summary>
public class GitHubRetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public double BackoffMultiplier { get; set; } = 2.0;
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public bool HandleRateLimiting { get; set; } = true;
}

/// <summary>
/// Rate limiting information from GitHub API
/// </summary>
public class GitHubRateLimit
{
    public int Limit { get; set; }
    public int Remaining { get; set; }
    public DateTime ResetTime { get; set; }
    public int Used { get; set; }
    public string Resource { get; set; } = string.Empty;
}

/// <summary>
/// Enhanced GitHub issue with additional metadata
/// </summary>
public class GitHubIssueDetailed : GitHubIssue
{
    public List<string> Labels { get; set; } = new();
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public GitHubUser? ClosedBy { get; set; }
    public int Comments { get; set; }
    public bool Locked { get; set; }
    public string? ActiveLockReason { get; set; }
    public string RepositoryUrl { get; set; } = string.Empty;
}

/// <summary>
/// Batch issue creation request
/// </summary>
public class GitHubBatchIssueRequest
{
    public string Owner { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public List<CreateIssueRequest> Issues { get; set; } = new();
    public bool ContinueOnError { get; set; } = true;
}

/// <summary>
/// Result of batch issue creation
/// </summary>
public class GitHubBatchIssueResult
{
    public int TotalRequested { get; set; }
    public int SuccessfullyCreated { get; set; }
    public int Failed { get; set; }
    public List<GitHubIssueDetailed> CreatedIssues { get; set; } = new();
    public List<GitHubBatchError> Errors { get; set; } = new();
    public TimeSpan TotalTime { get; set; }
}

/// <summary>
/// Error information for batch operations
/// </summary>
public class GitHubBatchError
{
    public int Index { get; set; }
    public string RequestTitle { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
}

/// <summary>
/// Authentication flow progress information
/// </summary>
public class GitHubAuthProgress
{
    public GitHubAuthStep CurrentStep { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUrl { get; set; }
    public TimeSpan? TimeRemaining { get; set; }
    public string? StatusMessage { get; set; }
    public bool IsComplete { get; set; }
    public bool HasError { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Authentication flow step enumeration
/// </summary>
public enum GitHubAuthStep
{
    Initializing,
    RequestingDeviceCode,
    WaitingForUserAuthorization,
    PollingForToken,
    ValidatingToken,
    Complete,
    Error
}

