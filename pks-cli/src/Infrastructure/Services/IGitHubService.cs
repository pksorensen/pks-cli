using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for GitHub integration services including repository management and PAT generation
/// </summary>
public interface IGitHubService
{
    /// <summary>
    /// Creates a new repository in the authenticated user's account
    /// </summary>
    /// <param name="repositoryName">Name of the repository to create</param>
    /// <param name="description">Optional description for the repository</param>
    /// <param name="isPrivate">Whether the repository should be private</param>
    /// <returns>Repository information including clone URL and web URL</returns>
    Task<GitHubRepository> CreateRepositoryAsync(string repositoryName, string? description = null, bool isPrivate = false);

    /// <summary>
    /// Configures GitHub integration for a project including PAT setup
    /// </summary>
    /// <param name="projectId">Unique project identifier</param>
    /// <param name="repositoryUrl">GitHub repository URL</param>
    /// <param name="personalAccessToken">Optional PAT for project-specific operations</param>
    /// <returns>Configuration result with status and any generated tokens</returns>
    Task<GitHubConfiguration> ConfigureProjectIntegrationAsync(string projectId, string repositoryUrl, string? personalAccessToken = null);

    /// <summary>
    /// Retrieves repository information from GitHub API
    /// </summary>
    /// <param name="owner">Repository owner (username or organization)</param>
    /// <param name="repositoryName">Repository name</param>
    /// <returns>Repository details including metadata and permissions</returns>
    Task<GitHubRepository?> GetRepositoryAsync(string owner, string repositoryName);

    /// <summary>
    /// Validates a GitHub personal access token
    /// </summary>
    /// <param name="personalAccessToken">PAT to validate</param>
    /// <returns>Token validation result with scope information</returns>
    Task<GitHubTokenValidation> ValidateTokenAsync(string personalAccessToken);

    /// <summary>
    /// Creates issues in the repository for project tracking
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <param name="title">Issue title</param>
    /// <param name="body">Issue description</param>
    /// <param name="labels">Optional labels to apply</param>
    /// <returns>Created issue information</returns>
    Task<GitHubIssue> CreateIssueAsync(string owner, string repositoryName, string title, string body, string[]? labels = null);

    /// <summary>
    /// Initializes a local git repository and sets up GitHub remote
    /// </summary>
    /// <param name="projectPath">Local project directory path</param>
    /// <param name="repositoryUrl">GitHub repository URL</param>
    /// <param name="initialCommitMessage">Message for the initial commit</param>
    /// <returns>Git initialization result</returns>
    Task<GitInitializationResult> InitializeGitRepositoryAsync(string projectPath, string repositoryUrl, string initialCommitMessage = "Initial commit");

    /// <summary>
    /// Generates project-scoped configuration for GitHub integration
    /// Note: GitHub API doesn't support project-scoped PATs directly, 
    /// this method provides a configuration approach for organizing access
    /// </summary>
    /// <param name="projectId">Unique project identifier</param>
    /// <param name="repositoryUrl">Repository URL</param>
    /// <param name="scopes">Required API scopes for the project</param>
    /// <returns>Project-specific GitHub configuration</returns>
    Task<ProjectGitHubConfig> GenerateProjectConfigurationAsync(string projectId, string repositoryUrl, string[] scopes);

    /// <summary>
    /// Checks if the current user has access to the specified repository
    /// </summary>
    /// <param name="repositoryUrl">Repository URL to check</param>
    /// <returns>Access level and permissions information</returns>
    Task<GitHubAccessLevel> CheckRepositoryAccessAsync(string repositoryUrl);

    /// <summary>
    /// Checks if a repository exists
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <returns>True if repository exists</returns>
    Task<bool> RepositoryExistsAsync(string owner, string repositoryName);

    /// <summary>
    /// Gets comprehensive repository information
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <returns>Repository information including metadata</returns>
    Task<GitHubRepositoryInfo> GetRepositoryInfoAsync(string owner, string repositoryName);

    /// <summary>
    /// Gets repository activity information
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <param name="days">Number of days to look back for activity</param>
    /// <returns>Repository activity data</returns>
    Task<GitHubRepositoryActivity> GetRepositoryActivityAsync(string owner, string repositoryName, int days = 30);

    /// <summary>
    /// Gets available workflow templates
    /// </summary>
    /// <returns>List of available workflow templates</returns>
    Task<List<GitHubWorkflowTemplate>> GetAvailableWorkflowTemplatesAsync();

    /// <summary>
    /// Sets up a workflow in the repository
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <param name="workflowTemplate">Workflow template to setup</param>
    /// <param name="configuration">Workflow configuration</param>
    /// <returns>Workflow setup result</returns>
    Task<GitHubWorkflowSetupResult> SetupWorkflowAsync(string owner, string repositoryName, string workflowTemplate, WorkflowConfiguration configuration);

    /// <summary>
    /// Gets repository releases
    /// </summary>
    /// <param name="owner">Repository owner</param>
    /// <param name="repositoryName">Repository name</param>
    /// <param name="includePreReleases">Include pre-release versions</param>
    /// <returns>List of releases</returns>
    Task<List<GitHubRelease>> GetReleasesAsync(string owner, string repositoryName, bool includePreReleases = false);
}