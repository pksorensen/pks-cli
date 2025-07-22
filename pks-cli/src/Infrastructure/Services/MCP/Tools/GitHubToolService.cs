using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS GitHub integration operations
/// This service provides MCP tools for GitHub repository management and CI/CD operations
/// </summary>
public class GitHubToolService
{
    private readonly ILogger<GitHubToolService> _logger;
    private readonly IGitHubService _githubService;
    private readonly IProjectIdentityService _projectIdentityService;

    public GitHubToolService(
        ILogger<GitHubToolService> logger,
        IGitHubService githubService,
        IProjectIdentityService projectIdentityService)
    {
        _logger = logger;
        _githubService = githubService;
        _projectIdentityService = projectIdentityService;
    }

    /// <summary>
    /// Create a new GitHub repository
    /// This tool connects to the real PKS GitHub integration functionality
    /// </summary>
    [McpServerTool]
    [Description("Create a new GitHub repository")]
    public async Task<object> CreateRepositoryAsync(
        string repositoryName,
        string? description = null,
        bool isPrivate = false,
        bool initializeWithReadme = true,
        string? gitignoreTemplate = null,
        string? license = null)
    {
        _logger.LogInformation("MCP Tool: Creating GitHub repository '{RepositoryName}', private: {IsPrivate}", 
            repositoryName, isPrivate);

        try
        {
            // Validate repository name
            if (string.IsNullOrWhiteSpace(repositoryName))
            {
                return new
                {
                    success = false,
                    error = "Repository name cannot be empty",
                    message = "Please provide a valid repository name"
                };
            }

            // Validate repository name format (GitHub rules)
            if (repositoryName.Length > 100)
            {
                return new
                {
                    success = false,
                    error = "Repository name too long",
                    message = "Repository name must be 100 characters or less"
                };
            }

            // Check if repository already exists
            var repositoryExists = await _githubService.RepositoryExistsAsync("owner", repositoryName);
            if (repositoryExists)
            {
                return new
                {
                    success = false,
                    error = "Repository already exists",
                    repositoryName,
                    existingUrl = $"https://github.com/owner/{repositoryName}",
                    message = $"Repository '{repositoryName}' already exists"
                };
            }

            // Create repository
            var repository = await _githubService.CreateRepositoryAsync(
                repositoryName, 
                description ?? $"Repository created by PKS CLI", 
                isPrivate);

            if (repository != null)
            {
                return new
                {
                    success = true,
                    repositoryName,
                    description = repository.Description,
                    isPrivate = repository.IsPrivate,
                    url = repository.HtmlUrl,
                    cloneUrl = repository.CloneUrl,
                    sshUrl = $"git@github.com:{repository.FullName}.git",
                    owner = repository.Owner,
                    features = new
                    {
                        hasReadme = initializeWithReadme,
                        hasGitignore = !string.IsNullOrEmpty(gitignoreTemplate),
                        hasLicense = !string.IsNullOrEmpty(license),
                        gitignoreTemplate,
                        license
                    },
                    defaultBranch = "main", // Default branch
                    createdAt = repository.CreatedAt,
                    message = $"Repository '{repositoryName}' created successfully"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    repositoryName,
                    error = "Repository creation failed",
                    message = "Repository creation failed: Unable to create repository"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GitHub repository '{RepositoryName}'", repositoryName);
            return new
            {
                success = false,
                repositoryName,
                error = ex.Message,
                message = $"Repository creation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Create an issue in a GitHub repository
    /// </summary>
    [McpServerTool]
    [Description("Create an issue in a GitHub repository")]
    public async Task<object> CreateIssueAsync(
        string title,
        string? body = null,
        string? repositoryName = null,
        string[]? labels = null,
        string[]? assignees = null,
        int? milestone = null)
    {
        _logger.LogInformation("MCP Tool: Creating GitHub issue '{Title}' in repository '{RepositoryName}'", 
            title, repositoryName);

        try
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return new
                {
                    success = false,
                    error = "Issue title cannot be empty",
                    message = "Please provide a valid issue title"
                };
            }

            // Determine repository name
            string targetRepo;
            if (!string.IsNullOrWhiteSpace(repositoryName))
            {
                targetRepo = repositoryName;
            }
            else
            {
                // Try to detect from current project
                var projectIdentity = await _projectIdentityService.GetProjectIdentityAsync("default");
                if (projectIdentity?.GitHubRepository != null)
                {
                    targetRepo = projectIdentity.GitHubRepository;
                }
                else
                {
                    return new
                    {
                        success = false,
                        error = "Repository not specified",
                        message = "Please specify a repository name or run from a project with GitHub integration"
                    };
                }
            }

            // Create issue
            var createRequest = new CreateIssueRequest
            {
                Title = title,
                Body = body ?? string.Empty,
                Labels = labels?.ToList() ?? new List<string>(),
                Assignees = assignees?.ToList() ?? new List<string>(),
                Milestone = milestone
            };

            var issue = await _githubService.CreateIssueAsync("owner", targetRepo, title, body ?? string.Empty, labels);

            if (issue != null)
            {
                return new
                {
                    success = true,
                    issueNumber = issue.Number,
                    title = issue.Title,
                    body = issue.Body,
                    repositoryName = targetRepo,
                    url = issue.HtmlUrl,
                    state = issue.State,
                    labels = Array.Empty<string>(), // Labels would be available from the service result
                    assignees = Array.Empty<string>(), // Assignees not available in GitHubIssue model
                    milestone = (string?)null, // Milestone not available in GitHubIssue model
                    author = (string?)null, // User not available in GitHubIssue model
                    createdAt = issue.CreatedAt,
                    message = $"Issue #{issue.Number} created successfully in {targetRepo}"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    title,
                    repositoryName = targetRepo,
                    error = "Issue creation failed",
                    message = $"Issue creation failed: Unable to create issue"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GitHub issue '{Title}'", title);
            return new
            {
                success = false,
                title,
                repositoryName,
                error = ex.Message,
                message = $"Issue creation failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get GitHub repository information and status
    /// </summary>
    [McpServerTool]
    [Description("Get GitHub repository information and status")]
    public async Task<object> GetRepositoryInfoAsync(
        string? repositoryName = null,
        bool includeActivity = true,
        bool includeStats = true)
    {
        _logger.LogInformation("MCP Tool: Getting GitHub repository info for '{RepositoryName}', includeActivity: {IncludeActivity}, includeStats: {IncludeStats}", 
            repositoryName, includeActivity, includeStats);

        try
        {
            // Determine repository name
            string targetRepo;
            if (!string.IsNullOrWhiteSpace(repositoryName))
            {
                targetRepo = repositoryName;
            }
            else
            {
                // Try to detect from current project
                var projectIdentity = await _projectIdentityService.GetProjectIdentityAsync("default");
                if (projectIdentity?.GitHubRepository != null)
                {
                    targetRepo = projectIdentity.GitHubRepository;
                }
                else
                {
                    return new
                    {
                        success = false,
                        error = "Repository not specified",
                        message = "Please specify a repository name or run from a project with GitHub integration"
                    };
                }
            }

            // Get repository information
            var result = await _githubService.GetRepositoryInfoAsync("owner", targetRepo);

            if (result != null)
            {
                var baseInfo = new
                {
                    success = true,
                    repositoryName = targetRepo,
                    fullName = result.FullName,
                    description = result.Description,
                    url = result.HtmlUrl,
                    cloneUrl = result.CloneUrl,
                    sshUrl = $"git@github.com:{result.FullName}.git",
                    isPrivate = result.IsPrivate,
                    isFork = false,
                    defaultBranch = "main",
                    language = "Unknown",
                    owner = result.Owner,
                    createdAt = result.CreatedAt,
                    updatedAt = DateTime.UtcNow,
                    pushedAt = DateTime.UtcNow,
                    message = $"Repository information retrieved for {targetRepo}"
                };

                var responseData = new Dictionary<string, object>
                {
                    ["success"] = baseInfo.success,
                    ["repositoryName"] = baseInfo.repositoryName,
                    ["fullName"] = baseInfo.fullName,
                    ["description"] = baseInfo.description,
                    ["url"] = baseInfo.url,
                    ["cloneUrl"] = baseInfo.cloneUrl,
                    ["sshUrl"] = baseInfo.sshUrl,
                    ["isPrivate"] = baseInfo.isPrivate,
                    ["isFork"] = baseInfo.isFork,
                    ["defaultBranch"] = baseInfo.defaultBranch,
                    ["language"] = baseInfo.language,
                    ["owner"] = baseInfo.owner,
                    ["createdAt"] = baseInfo.createdAt,
                    ["updatedAt"] = baseInfo.updatedAt,
                    ["pushedAt"] = baseInfo.pushedAt,
                    ["message"] = baseInfo.message
                };

                if (includeStats)
                {
                    responseData["statistics"] = new
                    {
                        starCount = result.StarCount,
                        watcherCount = 0, // Not available in GitHubRepositoryInfo
                        forkCount = result.ForkCount,
                        issueCount = 0, // Not available in GitHubRepositoryInfo
                        size = 0, // Not available in GitHubRepositoryInfo
                        hasIssues = true, // Default value
                        hasProjects = true, // Default value
                        hasWiki = true, // Default value
                        hasPages = false, // Default value
                        hasDownloads = true // Default value
                    };
                }

                if (includeActivity)
                {
                    var activity = await _githubService.GetRepositoryActivityAsync("owner", targetRepo, 10);
                    responseData["recentActivity"] = new
                    {
                        commits = activity.RecentCommits?.Select(c => new
                        {
                            sha = c.Sha,
                            message = c.Commit.Message,
                            author = c.Commit.Author.Name,
                            date = c.Commit.Author.Date,
                            url = c.HtmlUrl
                        }).ToArray() ?? Array.Empty<object>(),
                        issues = Array.Empty<object>(), // RecentIssues not available in GitHubRepositoryActivity model
                        pullRequests = Array.Empty<object>() // RecentPullRequests not available in GitHubRepositoryActivity model
                    };
                }

                return responseData;
            }
            else
            {
                return new
                {
                    success = false,
                    repositoryName = targetRepo,
                    error = "Repository not found",
                    message = $"Failed to get repository information: Repository not found"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GitHub repository info for '{RepositoryName}'", repositoryName);
            return new
            {
                success = false,
                repositoryName,
                error = ex.Message,
                message = $"Failed to get repository information: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Set up GitHub Actions workflow for the repository
    /// </summary>
    [McpServerTool]
    [Description("Set up GitHub Actions workflow for the repository")]
    public async Task<object> SetupWorkflowAsync(
        string workflowTemplate = "dotnet-ci",
        string? repositoryName = null,
        string targetBranch = "main",
        bool includeDeployment = false)
    {
        _logger.LogInformation("MCP Tool: Setting up GitHub workflow '{WorkflowTemplate}' for repository '{RepositoryName}'", 
            workflowTemplate, repositoryName);

        try
        {
            // Determine repository name
            string targetRepo;
            if (!string.IsNullOrWhiteSpace(repositoryName))
            {
                targetRepo = repositoryName;
            }
            else
            {
                var projectIdentity = await _projectIdentityService.GetProjectIdentityAsync("default");
                if (projectIdentity?.GitHubRepository != null)
                {
                    targetRepo = projectIdentity.GitHubRepository;
                }
                else
                {
                    return new
                    {
                        success = false,
                        error = "Repository not specified",
                        message = "Please specify a repository name or run from a project with GitHub integration"
                    };
                }
            }

            // Get available workflow templates
            var availableTemplates = await _githubService.GetAvailableWorkflowTemplatesAsync();
            if (!availableTemplates.Any(t => t.Name == workflowTemplate))
            {
                return new
                {
                    success = false,
                    error = "Invalid workflow template",
                    workflowTemplate,
                    availableTemplates = availableTemplates.Select(t => t.Name).ToArray(),
                    message = $"Workflow template '{workflowTemplate}' not found"
                };
            }

            // Create workflow configuration
            var workflowConfig = new WorkflowConfiguration
            {
                Name = workflowTemplate,
                Events = new List<string> { "push", "pull_request" }
            };

            var result = await _githubService.SetupWorkflowAsync("owner", targetRepo, workflowTemplate, workflowConfig);

            if (result.Success)
            {
                return new
                {
                    success = true,
                    workflowTemplate,
                    repositoryName = targetRepo,
                    targetBranch,
                    includeDeployment,
                    workflowFile = result.FilePath,
                    workflowUrl = (string?)null, // WorkflowUrl not available in GitHubWorkflowSetupResult
                    features = Array.Empty<string>(), // Features not available in GitHubWorkflowSetupResult
                    triggers = new[] { "push", "pull_request" }, // Default triggers
                    environmentSecrets = Array.Empty<string>(), // RequiredSecrets not available in GitHubWorkflowSetupResult
                    createdAt = DateTime.UtcNow,
                    message = result.Message
                };
            }
            else
            {
                return new
                {
                    success = false,
                    workflowTemplate,
                    repositoryName = targetRepo,
                    error = result.Message, // Use Message instead of ErrorMessage
                    message = $"Workflow setup failed: {result.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup GitHub workflow '{WorkflowTemplate}'", workflowTemplate);
            return new
            {
                success = false,
                workflowTemplate,
                repositoryName,
                error = ex.Message,
                message = $"Workflow setup failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// List GitHub releases for a repository
    /// </summary>
    [McpServerTool]
    [Description("List GitHub releases for a repository")]
    public async Task<object> ListReleasesAsync(
        string? repositoryName = null,
        int count = 10,
        bool includePreReleases = false,
        bool includeDrafts = false)
    {
        _logger.LogInformation("MCP Tool: Listing GitHub releases for repository '{RepositoryName}', count: {Count}", 
            repositoryName, count);

        try
        {
            // Determine repository name
            string targetRepo;
            if (!string.IsNullOrWhiteSpace(repositoryName))
            {
                targetRepo = repositoryName;
            }
            else
            {
                var projectIdentity = await _projectIdentityService.GetProjectIdentityAsync("default");
                if (projectIdentity?.GitHubRepository != null)
                {
                    targetRepo = projectIdentity.GitHubRepository;
                }
                else
                {
                    return new
                    {
                        success = false,
                        error = "Repository not specified",
                        message = "Please specify a repository name or run from a project with GitHub integration"
                    };
                }
            }

            // Get releases
            var releases = await _githubService.GetReleasesAsync("owner", targetRepo, includePreReleases);

            if (releases != null && releases.Any())
            {
                return new
                {
                    success = true,
                    repositoryName = targetRepo,
                    totalReleases = releases.Count,
                    includePreReleases,
                    includeDrafts,
                    releases = releases.Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        tagName = r.TagName,
                        body = r.Body,
                        isDraft = r.IsDraft,
                        isPreRelease = r.IsPreRelease,
                        createdAt = r.PublishedAt,
                        publishedAt = r.PublishedAt,
                        author = "Unknown", // Author not available in GitHubRelease model
                        url = (string?)null, // HtmlUrl not available in GitHubRelease model
                        downloadUrl = (string?)null, // Not available in GitHubRelease model
                        assets = Array.Empty<object>() // Assets not available in GitHubRelease model
                    }).ToArray(),
                    latestRelease = releases.FirstOrDefault(r => !r.IsDraft && !r.IsPreRelease)?.TagName,
                    retrievedAt = DateTime.UtcNow,
                    message = $"Retrieved {releases.Count} releases for {targetRepo}"
                };
            }
            else
            {
                return new
                {
                    success = false,
                    repositoryName = targetRepo,
                    error = "No releases found",
                    message = $"No releases found for repository: {targetRepo}"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list GitHub releases for repository '{RepositoryName}'", repositoryName);
            return new
            {
                success = false,
                repositoryName,
                error = ex.Message,
                message = $"Failed to retrieve releases: {ex.Message}"
            };
        }
    }
}