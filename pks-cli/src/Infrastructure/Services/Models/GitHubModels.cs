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