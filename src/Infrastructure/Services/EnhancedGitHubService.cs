using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Enhanced GitHub service that integrates with the new authentication system
/// This service bridges the existing IGitHubService interface with the new authentication capabilities
/// </summary>
public class EnhancedGitHubService : IGitHubService
{
    private readonly IGitHubAuthenticationService _authService;
    private readonly IGitHubApiClient _apiClient;
    private readonly IGitHubIssuesService _issuesService;
    private readonly IConfigurationService _configurationService;

    public EnhancedGitHubService(
        IGitHubAuthenticationService authService,
        IGitHubApiClient apiClient,
        IGitHubIssuesService issuesService,
        IConfigurationService configurationService)
    {
        _authService = authService;
        _apiClient = apiClient;
        _issuesService = issuesService;
        _configurationService = configurationService;
    }

    public async Task<GitHubRepository> CreateRepositoryAsync(string repositoryName, string? description = null, bool isPrivate = false)
    {
        await EnsureAuthenticatedAsync();

        var requestBody = new
        {
            name = repositoryName,
            description = description ?? $"Project created with PKS CLI",
            @private = isPrivate,
            auto_init = true,
            license_template = "mit"
        };

        try
        {
            var response = await _apiClient.PostAsync<dynamic>("user/repos", requestBody);

            if (response == null)
            {
                throw new InvalidOperationException("Failed to create repository - no response from GitHub API");
            }

            return new GitHubRepository
            {
                Id = (long)response.id,
                Name = (string)response.name,
                FullName = (string)response.full_name,
                Description = response.description != null ? (string)response.description : null,
                CloneUrl = (string)response.clone_url,
                HtmlUrl = (string)response.html_url,
                IsPrivate = (bool)response.@private,
                Owner = (string)response.owner.login,
                CreatedAt = DateTime.Parse((string)response.created_at)
            };
        }
        catch (GitHubApiException ex)
        {
            throw new InvalidOperationException($"Failed to create repository: {ex.Message}", ex);
        }
    }

    public async Task<GitHubConfiguration> ConfigureProjectIntegrationAsync(string projectId, string repositoryUrl, string? personalAccessToken = null)
    {
        var config = new GitHubConfiguration
        {
            ProjectId = projectId,
            RepositoryUrl = repositoryUrl,
            ConfiguredAt = DateTime.UtcNow,
            IsValid = true
        };

        if (!string.IsNullOrEmpty(personalAccessToken))
        {
            // Validate the provided token
            var validation = await _authService.ValidateTokenAsync(personalAccessToken);
            config.IsValid = validation.IsValid;
            config.TokenScopes = validation.Scopes;

            if (validation.IsValid)
            {
                // Store the token using the authentication service
                var storedToken = new GitHubStoredToken
                {
                    AccessToken = personalAccessToken,
                    Scopes = validation.Scopes,
                    CreatedAt = DateTime.UtcNow,
                    IsValid = true,
                    LastValidated = DateTime.UtcNow,
                    AssociatedUser = projectId
                };

                await _authService.StoreTokenAsync(storedToken, projectId);

                // Update API client authentication
                _apiClient.SetAuthenticationToken(personalAccessToken);
            }
            else
            {
                config.ErrorMessage = validation.ErrorMessage;
            }
        }

        // Store project configuration
        await _configurationService.SetAsync($"github.{projectId}.repository", repositoryUrl);
        await _configurationService.SetAsync($"github.{projectId}.configured_at", config.ConfiguredAt.ToString("O"));

        return config;
    }

    public async Task<GitHubRepository?> GetRepositoryAsync(string owner, string repositoryName)
    {
        await EnsureAuthenticatedAsync();

        try
        {
            var response = await _apiClient.GetAsync<dynamic>($"repos/{owner}/{repositoryName}");

            if (response == null)
            {
                return null;
            }

            return new GitHubRepository
            {
                Id = (long)response.id,
                Name = (string)response.name,
                FullName = (string)response.full_name,
                Description = response.description != null ? (string)response.description : null,
                CloneUrl = (string)response.clone_url,
                HtmlUrl = (string)response.html_url,
                IsPrivate = (bool)response.@private,
                Owner = (string)response.owner.login,
                CreatedAt = DateTime.Parse((string)response.created_at)
            };
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<GitHubTokenValidation> ValidateTokenAsync(string personalAccessToken)
    {
        return await _authService.ValidateTokenAsync(personalAccessToken);
    }

    public async Task<GitHubIssue> CreateIssueAsync(string owner, string repositoryName, string title, string body, string[]? labels = null)
    {
        var request = new CreateIssueRequest
        {
            Title = title,
            Body = body,
            Labels = labels?.ToList() ?? new List<string>()
        };

        var detailedIssue = await _issuesService.CreateIssueAsync(owner, repositoryName, request);

        // Convert to the legacy GitHubIssue format
        return new GitHubIssue
        {
            Id = detailedIssue.Id,
            Number = detailedIssue.Number,
            Title = detailedIssue.Title,
            Body = detailedIssue.Body,
            State = detailedIssue.State,
            HtmlUrl = detailedIssue.HtmlUrl,
            CreatedAt = detailedIssue.CreatedAt,
            Assignees = detailedIssue.Assignees,
            User = detailedIssue.User,
            Milestone = detailedIssue.Milestone
        };
    }

    public async Task<GitInitializationResult> InitializeGitRepositoryAsync(string projectPath, string repositoryUrl, string initialCommitMessage = "Initial commit")
    {
        var result = new GitInitializationResult
        {
            ProjectPath = projectPath,
            RepositoryUrl = repositoryUrl,
            InitialCommitMessage = initialCommitMessage
        };

        try
        {
            // Check if git is available
            var gitAvailable = await CheckGitAvailabilityAsync();
            if (!gitAvailable)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Git is not available or not installed";
                return result;
            }

            // Initialize git repository
            await ExecuteGitCommandAsync(projectPath, "init");
            result.Steps.Add("Initialized git repository");

            // Add remote origin
            await ExecuteGitCommandAsync(projectPath, $"remote add origin {repositoryUrl}");
            result.Steps.Add("Added remote origin");

            // Create initial commit if files exist
            var hasFiles = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories).Any();
            if (hasFiles)
            {
                await ExecuteGitCommandAsync(projectPath, "add .");
                await ExecuteGitCommandAsync(projectPath, $"commit -m \"{initialCommitMessage}\"");
                result.Steps.Add("Created initial commit");
            }

            result.IsSuccess = true;
            result.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            result.IsSuccess = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<ProjectGitHubConfig> GenerateProjectConfigurationAsync(string projectId, string repositoryUrl, string[] scopes)
    {
        var config = new ProjectGitHubConfig
        {
            ProjectId = projectId,
            RepositoryUrl = repositoryUrl,
            RequiredScopes = scopes,
            ConfigurationName = $"PKS-CLI-{projectId}",
            Description = $"GitHub configuration for PKS CLI project {projectId}",
            CreatedAt = DateTime.UtcNow,
            Instructions = new[]
            {
                "1. Use 'pks github auth' to authenticate via device code flow",
                "2. Or create a Personal Access Token at https://github.com/settings/tokens",
                $"3. Grant the following scopes: {string.Join(", ", scopes)}",
                "4. Use 'pks github config' to configure the token for this project",
                "5. The token will be securely stored and associated with this project"
            }
        };

        // Store project configuration
        await _configurationService.SetAsync($"github.{projectId}.scopes", string.Join(",", scopes));
        await _configurationService.SetAsync($"github.{projectId}.repository", repositoryUrl);
        await _configurationService.SetAsync($"github.{projectId}.config_name", config.ConfigurationName);

        return config;
    }

    public async Task<GitHubAccessLevel> CheckRepositoryAccessAsync(string repositoryUrl)
    {
        var accessLevel = new GitHubAccessLevel
        {
            RepositoryUrl = repositoryUrl,
            HasAccess = false,
            CheckedAt = DateTime.UtcNow
        };

        try
        {
            await EnsureAuthenticatedAsync();

            // Extract owner/repo from URL
            var (owner, repo) = ParseRepositoryUrl(repositoryUrl);
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
            {
                accessLevel.ErrorMessage = "Invalid repository URL format";
                return accessLevel;
            }

            var response = await _apiClient.GetAsync<dynamic>($"repos/{owner}/{repo}");

            if (response != null)
            {
                accessLevel.HasAccess = true;

                // Check permissions if available
                if (response.permissions != null)
                {
                    accessLevel.AccessLevel = GetAccessLevelFromPermissions(response.permissions);
                }
                else
                {
                    accessLevel.AccessLevel = "read"; // Default assumption
                }

                accessLevel.CanWrite = accessLevel.AccessLevel is "write" or "admin";
                accessLevel.CanAdmin = accessLevel.AccessLevel == "admin";
            }
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            accessLevel.ErrorMessage = "Repository not found or no access";
        }
        catch (Exception ex)
        {
            accessLevel.ErrorMessage = ex.Message;
        }

        return accessLevel;
    }

    public async Task<bool> RepositoryExistsAsync(string owner, string repo)
    {
        var repository = await GetRepositoryAsync(owner, repo);
        return repository != null;
    }

    public async Task<GitHubRepositoryInfo> GetRepositoryInfoAsync(string owner, string repositoryName)
    {
        await EnsureAuthenticatedAsync();

        try
        {
            var response = await _apiClient.GetAsync<dynamic>($"repos/{owner}/{repositoryName}");

            if (response == null)
            {
                throw new InvalidOperationException($"Repository {owner}/{repositoryName} not found");
            }

            return new GitHubRepositoryInfo
            {
                Id = (long)response.id,
                Name = (string)response.name,
                FullName = (string)response.full_name,
                Description = response.description != null ? (string)response.description : null,
                CloneUrl = (string)response.clone_url,
                HtmlUrl = (string)response.html_url,
                IsPrivate = (bool)response.@private,
                Owner = (string)response.owner.login,
                CreatedAt = DateTime.Parse((string)response.created_at),
                UpdatedAt = DateTime.Parse((string)response.updated_at),
                StarCount = (int)response.stargazers_count,
                ForkCount = (int)response.forks_count,
                Language = response.language != null ? (string)response.language : "Unknown",
                Topics = response.topics != null ?
                    ((IEnumerable<dynamic>)response.topics).Select(t => (string)t).ToList() :
                    new List<string>()
            };
        }
        catch (GitHubApiException ex)
        {
            throw new InvalidOperationException($"Failed to get repository info: {ex.Message}", ex);
        }
    }

    public async Task<GitHubRepositoryActivity> GetRepositoryActivityAsync(string owner, string repositoryName, int days = 30)
    {
        await EnsureAuthenticatedAsync();

        try
        {
            // Get recent commits
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var commitsResponse = await _apiClient.GetAsync<List<dynamic>>($"repos/{owner}/{repositoryName}/commits?since={since}&per_page=100");

            // Get recent issues and PRs
            var issuesResponse = await _apiClient.GetAsync<List<dynamic>>($"repos/{owner}/{repositoryName}/issues?state=all&since={since}&per_page=50");

            // Get branches
            var branchesResponse = await _apiClient.GetAsync<List<dynamic>>($"repos/{owner}/{repositoryName}/branches?per_page=20");

            var activity = new GitHubRepositoryActivity
            {
                CommitCount = commitsResponse?.Count ?? 0,
                RecentCommits = MapCommits(commitsResponse ?? new List<dynamic>()),
                ActiveBranches = branchesResponse?.Select(b => (string)b.name).ToList() ?? new List<string>(),
                LastActivity = DateTime.UtcNow
            };

            // Separate issues and PRs
            var issues = issuesResponse?.Where(i => i.pull_request == null).ToList() ?? new List<dynamic>();
            var pullRequests = issuesResponse?.Where(i => i.pull_request != null).ToList() ?? new List<dynamic>();

            activity.IssueCount = issues.Count;
            activity.PullRequestCount = pullRequests.Count;
            activity.RecentIssues = MapIssues(issues);
            activity.RecentPullRequests = MapPullRequests(pullRequests);

            if (commitsResponse?.Any() == true)
            {
                activity.LastActivity = DateTime.Parse((string)commitsResponse.First().commit.author.date);
            }

            return activity;
        }
        catch (GitHubApiException ex)
        {
            throw new InvalidOperationException($"Failed to get repository activity: {ex.Message}", ex);
        }
    }

    public async Task<List<GitHubWorkflowTemplate>> GetAvailableWorkflowTemplatesAsync()
    {
        // Return predefined workflow templates
        // In a real implementation, this might fetch from GitHub's template repository
        return new List<GitHubWorkflowTemplate>
        {
            new()
            {
                Id = "dotnet",
                Name = ".NET Core CI/CD",
                Description = "Build and test .NET Core applications",
                Language = "C#",
                Content = GenerateDotNetWorkflowTemplate(),
                RequiredSecrets = new List<string> { "NUGET_API_KEY" }
            },
            new()
            {
                Id = "node",
                Name = "Node.js CI/CD",
                Description = "Build and test Node.js applications",
                Language = "JavaScript",
                Content = GenerateNodeWorkflowTemplate(),
                RequiredSecrets = new List<string> { "NPM_TOKEN" }
            },
            new()
            {
                Id = "docker",
                Name = "Docker CI/CD",
                Description = "Build and push Docker images",
                Language = "Docker",
                Content = GenerateDockerWorkflowTemplate(),
                RequiredSecrets = new List<string> { "DOCKER_USERNAME", "DOCKER_PASSWORD" }
            }
        };
    }

    public async Task<GitHubWorkflowSetupResult> SetupWorkflowAsync(string owner, string repositoryName, string workflowTemplate, WorkflowConfiguration configuration)
    {
        await EnsureAuthenticatedAsync();

        var templates = await GetAvailableWorkflowTemplatesAsync();
        var template = templates.FirstOrDefault(t => t.Id == workflowTemplate);

        if (template == null)
        {
            return new GitHubWorkflowSetupResult
            {
                Success = false,
                ErrorMessage = $"Workflow template '{workflowTemplate}' not found"
            };
        }

        try
        {
            var workflowContent = ProcessTemplateContent(template.Content, configuration);
            var fileName = $"{configuration.Name}.yml";
            var filePath = $".github/workflows/{fileName}";

            // Create or update the workflow file
            var requestBody = new
            {
                message = $"Add {configuration.Name} workflow",
                content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(workflowContent)),
                branch = "main"
            };

            var response = await _apiClient.PutAsync<dynamic>($"repos/{owner}/{repositoryName}/contents/{filePath}", requestBody);

            return new GitHubWorkflowSetupResult
            {
                Success = true,
                WorkflowName = configuration.Name,
                FilePath = filePath,
                WorkflowFile = workflowContent,
                WorkflowUrl = $"https://github.com/{owner}/{repositoryName}/actions",
                Features = new List<string> { "Continuous Integration", "Automatic Testing" },
                Triggers = configuration.Events,
                RequiredSecrets = template.RequiredSecrets,
                Message = $"Workflow '{configuration.Name}' created successfully"
            };
        }
        catch (GitHubApiException ex)
        {
            return new GitHubWorkflowSetupResult
            {
                Success = false,
                ErrorMessage = $"Failed to setup workflow: {ex.Message}"
            };
        }
    }

    public async Task<List<GitHubRelease>> GetReleasesAsync(string owner, string repositoryName, bool includePreReleases = false)
    {
        await EnsureAuthenticatedAsync();

        try
        {
            var response = await _apiClient.GetAsync<List<dynamic>>($"repos/{owner}/{repositoryName}/releases?per_page=50");

            if (response == null)
            {
                return new List<GitHubRelease>();
            }

            var releases = response.Select(r => new GitHubRelease
            {
                Id = (long)r.id,
                Name = (string)r.name,
                TagName = (string)r.tag_name,
                Body = r.body != null ? (string)r.body : string.Empty,
                IsPreRelease = (bool)r.prerelease,
                IsDraft = (bool)r.draft,
                PublishedAt = r.published_at != null ? DateTime.Parse((string)r.published_at) : DateTime.MinValue,
                HtmlUrl = (string)r.html_url
            }).ToList();

            if (!includePreReleases)
            {
                releases = releases.Where(r => !r.IsPreRelease).ToList();
            }

            return releases;
        }
        catch (GitHubApiException ex)
        {
            throw new InvalidOperationException($"Failed to get releases: {ex.Message}", ex);
        }
    }

    #region Authentication Helpers

    private async Task EnsureAuthenticatedAsync()
    {
        if (!_apiClient.IsAuthenticated)
        {
            // Try to get stored token
            var storedToken = await _authService.GetStoredTokenAsync();
            if (storedToken != null && storedToken.IsValid)
            {
                _apiClient.SetAuthenticationToken(storedToken.AccessToken);
            }
            else
            {
                throw new UnauthorizedAccessException("GitHub authentication required. Please authenticate using 'pks github auth' command.");
            }
        }
    }

    #endregion

    #region Helper Methods

    private static async Task<bool> CheckGitAvailabilityAsync()
    {
        try
        {
            // In a real implementation, this would check if git is installed
            await Task.Delay(50);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ExecuteGitCommandAsync(string workingDirectory, string command)
    {
        // In a real implementation, this would execute git commands using Process.Start
        await Task.Delay(100);
    }

    private static (string owner, string repo) ParseRepositoryUrl(string repositoryUrl)
    {
        try
        {
            var uri = new Uri(repositoryUrl);
            var pathParts = uri.AbsolutePath.Trim('/').Split('/');

            if (pathParts.Length >= 2)
            {
                var owner = pathParts[0];
                var repo = pathParts[1].EndsWith(".git")
                    ? pathParts[1][..^4]
                    : pathParts[1];
                return (owner, repo);
            }
        }
        catch
        {
            // Invalid URL format
        }

        return (string.Empty, string.Empty);
    }

    private static string GetAccessLevelFromPermissions(dynamic permissions)
    {
        try
        {
            if (permissions.admin == true) return "admin";
            if (permissions.push == true) return "write";
            if (permissions.pull == true) return "read";
        }
        catch
        {
            // Ignore parsing errors
        }

        return "none";
    }

    private static List<GitHubCommit> MapCommits(List<dynamic> commits)
    {
        return commits.Select(c => new GitHubCommit
        {
            Sha = (string)c.sha,
            HtmlUrl = (string)c.html_url,
            Commit = new GitHubCommitDetail
            {
                Message = (string)c.commit.message,
                Author = new GitHubCommitAuthor
                {
                    Name = (string)c.commit.author.name,
                    Date = DateTime.Parse((string)c.commit.author.date)
                }
            }
        }).ToList();
    }

    private static List<GitHubIssue> MapIssues(List<dynamic> issues)
    {
        return issues.Select(i => new GitHubIssue
        {
            Id = (long)i.id,
            Number = (int)i.number,
            Title = (string)i.title,
            Body = i.body != null ? (string)i.body : string.Empty,
            State = (string)i.state,
            HtmlUrl = (string)i.html_url,
            CreatedAt = DateTime.Parse((string)i.created_at),
            User = new GitHubUser
            {
                Login = (string)i.user.login,
                Name = i.user.name != null ? (string)i.user.name : (string)i.user.login
            }
        }).ToList();
    }

    private static List<GitHubPullRequest> MapPullRequests(List<dynamic> pullRequests)
    {
        return pullRequests.Select(pr => new GitHubPullRequest
        {
            Number = (int)pr.number,
            Title = (string)pr.title,
            State = (string)pr.state,
            HtmlUrl = (string)pr.html_url,
            CreatedAt = DateTime.Parse((string)pr.created_at),
            User = new GitHubUser
            {
                Login = (string)pr.user.login,
                Name = pr.user.name != null ? (string)pr.user.name : (string)pr.user.login
            }
        }).ToList();
    }

    private static string GenerateDotNetWorkflowTemplate()
    {
        return @"name: .NET Core CI/CD

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v3
      with:
        dotnet-version: 8.0.x
        
    - name: Restore dependencies
      run: dotnet restore
      
    - name: Build
      run: dotnet build --no-restore
      
    - name: Test
      run: dotnet test --no-build --verbosity normal
      
    - name: Publish
      run: dotnet publish -c Release -o ./publish
      
    - name: Pack
      run: dotnet pack -c Release --no-build
      
    - name: Push to NuGet
      if: github.ref == 'refs/heads/main'
      run: dotnet nuget push **/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json
";
    }

    private static string GenerateNodeWorkflowTemplate()
    {
        return @"name: Node.js CI/CD

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    strategy:
      matrix:
        node-version: [16.x, 18.x, 20.x]
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Use Node.js ${{ matrix.node-version }}
      uses: actions/setup-node@v3
      with:
        node-version: ${{ matrix.node-version }}
        cache: 'npm'
        
    - run: npm ci
    - run: npm run build --if-present
    - run: npm test
    
    - name: Publish to NPM
      if: github.ref == 'refs/heads/main' && matrix.node-version == '18.x'
      run: |
        npm config set //registry.npmjs.org/:_authToken ${{ secrets.NPM_TOKEN }}
        npm publish
";
    }

    private static string GenerateDockerWorkflowTemplate()
    {
        return @"name: Docker CI/CD

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
    - uses: actions/checkout@v3
    
    - name: Set up Docker Buildx
      uses: docker/setup-buildx-action@v2
      
    - name: Login to Docker Hub
      if: github.event_name != 'pull_request'
      uses: docker/login-action@v2
      with:
        username: ${{ secrets.DOCKER_USERNAME }}
        password: ${{ secrets.DOCKER_PASSWORD }}
        
    - name: Extract metadata
      id: meta
      uses: docker/metadata-action@v4
      with:
        images: ${{ github.repository }}
        
    - name: Build and push
      uses: docker/build-push-action@v4
      with:
        context: .
        push: ${{ github.event_name != 'pull_request' }}
        tags: ${{ steps.meta.outputs.tags }}
        labels: ${{ steps.meta.outputs.labels }}
";
    }

    private static string ProcessTemplateContent(string templateContent, WorkflowConfiguration configuration)
    {
        var content = templateContent;

        // Replace placeholders with configuration values
        content = content.Replace("{{WORKFLOW_NAME}}", configuration.Name);

        if (configuration.Events.Any())
        {
            var eventsYaml = string.Join("\n    - ", configuration.Events);
            content = content.Replace("[ main, develop ]", $"[ {string.Join(", ", configuration.Events)} ]");
        }

        // Add environment variables if provided
        if (configuration.Environment.Any())
        {
            var envSection = "\n    env:\n" + string.Join("\n", configuration.Environment.Select(kv => $"      {kv.Key}: {kv.Value}"));
            content = content.Replace("    steps:", envSection + "\n    steps:");
        }

        return content;
    }

    #endregion
}