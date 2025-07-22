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

