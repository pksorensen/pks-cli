using System.Diagnostics;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Interface for comprehensive GitHub issue management
/// </summary>
public interface IGitHubIssuesService
{
    /// <summary>
    /// Creates a single issue in a repository
    /// </summary>
    Task<GitHubIssueDetailed> CreateIssueAsync(string owner, string repository, CreateIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates multiple issues in batch with progress reporting
    /// </summary>
    Task<GitHubBatchIssueResult> CreateIssuesBatchAsync(GitHubBatchIssueRequest request, IProgress<int>? progressCallback = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific issue by number
    /// </summary>
    Task<GitHubIssueDetailed?> GetIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists issues for a repository with filtering options
    /// </summary>
    Task<List<GitHubIssueDetailed>> ListIssuesAsync(string owner, string repository, GitHubIssueFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing issue
    /// </summary>
    Task<GitHubIssueDetailed> UpdateIssueAsync(string owner, string repository, int issueNumber, UpdateIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an issue
    /// </summary>
    Task<GitHubIssueDetailed> CloseIssueAsync(string owner, string repository, int issueNumber, string? closeReason = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reopens a closed issue
    /// </summary>
    Task<GitHubIssueDetailed> ReopenIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds labels to an issue
    /// </summary>
    Task<List<string>> AddLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes labels from an issue
    /// </summary>
    Task<List<string>> RemoveLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns users to an issue
    /// </summary>
    Task<List<GitHubUser>> AssignUsersAsync(string owner, string repository, int issueNumber, List<string> assignees, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches issues across repositories or within a specific repository
    /// </summary>
    Task<GitHubIssueSearchResult> SearchIssuesAsync(GitHubIssueSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets issue statistics for a repository
    /// </summary>
    Task<GitHubIssueStatistics> GetIssueStatisticsAsync(string owner, string repository, CancellationToken cancellationToken = default);
}

/// <summary>
/// Comprehensive GitHub issues service implementation
/// </summary>
public class GitHubIssuesService : IGitHubIssuesService
{
    private readonly IGitHubApiClient _apiClient;
    private readonly IGitHubAuthenticationService _authService;

    public GitHubIssuesService(IGitHubApiClient apiClient, IGitHubAuthenticationService authService)
    {
        _apiClient = apiClient;
        _authService = authService;
    }

    public async Task<GitHubIssueDetailed> CreateIssueAsync(string owner, string repository, CreateIssueRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var requestBody = new
        {
            title = request.Title,
            body = request.Body,
            assignees = request.Assignees,
            labels = request.Labels,
            milestone = request.Milestone
        };

        var endpoint = $"repos/{owner}/{repository}/issues";
        var response = await _apiClient.PostAsync<dynamic>(endpoint, requestBody, cancellationToken);

        if (response == null)
        {
            throw new GitHubApiException("Failed to create issue", System.Net.HttpStatusCode.BadRequest);
        }

        return MapToDetailedIssue(response, $"https://github.com/{owner}/{repository}");
    }

    public async Task<GitHubBatchIssueResult> CreateIssuesBatchAsync(GitHubBatchIssueRequest request, IProgress<int>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var result = new GitHubBatchIssueResult
        {
            TotalRequested = request.Issues.Count
        };

        var semaphore = new SemaphoreSlim(5, 5); // Limit concurrent requests to avoid rate limiting
        var tasks = new List<Task>();

        for (int i = 0; i < request.Issues.Count; i++)
        {
            var index = i;
            var issue = request.Issues[i];

            var task = ProcessBatchIssueAsync(
                semaphore,
                request.Owner,
                request.Repository,
                issue,
                index,
                result,
                request.ContinueOnError,
                cancellationToken);

            tasks.Add(task);

            // Report progress
            progressCallback?.Report(i + 1);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        result.TotalTime = stopwatch.Elapsed;
        result.Failed = result.TotalRequested - result.SuccessfullyCreated;

        return result;
    }

    public async Task<GitHubIssueDetailed?> GetIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var endpoint = $"repos/{owner}/{repository}/issues/{issueNumber}";
        var response = await _apiClient.GetAsync<dynamic>(endpoint, cancellationToken);

        return response != null
            ? MapToDetailedIssue(response, $"https://github.com/{owner}/{repository}")
            : null;
    }

    public async Task<List<GitHubIssueDetailed>> ListIssuesAsync(string owner, string repository, GitHubIssueFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var queryParams = BuildIssueFilterQuery(filter);
        var endpoint = $"repos/{owner}/{repository}/issues{queryParams}";

        var response = await _apiClient.GetAsync<List<dynamic>>(endpoint, cancellationToken);

        if (response == null)
        {
            return new List<GitHubIssueDetailed>();
        }

        var repositoryUrl = $"https://github.com/{owner}/{repository}";
        var issues = new List<GitHubIssueDetailed>();
        foreach (var issue in response)
        {
            issues.Add(MapToDetailedIssue(issue, repositoryUrl));
        }
        return issues;
    }

    public async Task<GitHubIssueDetailed> UpdateIssueAsync(string owner, string repository, int issueNumber, UpdateIssueRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var requestBody = new
        {
            title = request.Title,
            body = request.Body,
            state = request.State,
            assignees = request.Assignees,
            labels = request.Labels,
            milestone = request.Milestone
        };

        var endpoint = $"repos/{owner}/{repository}/issues/{issueNumber}";
        var response = await _apiClient.PatchAsync<dynamic>(endpoint, requestBody, cancellationToken);

        if (response == null)
        {
            throw new GitHubApiException("Failed to update issue", System.Net.HttpStatusCode.BadRequest);
        }

        return MapToDetailedIssue(response, $"https://github.com/{owner}/{repository}");
    }

    public async Task<GitHubIssueDetailed> CloseIssueAsync(string owner, string repository, int issueNumber, string? closeReason = null, CancellationToken cancellationToken = default)
    {
        var updateRequest = new UpdateIssueRequest
        {
            State = "closed"
        };

        return await UpdateIssueAsync(owner, repository, issueNumber, updateRequest, cancellationToken);
    }

    public async Task<GitHubIssueDetailed> ReopenIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default)
    {
        var updateRequest = new UpdateIssueRequest
        {
            State = "open"
        };

        return await UpdateIssueAsync(owner, repository, issueNumber, updateRequest, cancellationToken);
    }

    public async Task<List<string>> AddLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var endpoint = $"repos/{owner}/{repository}/issues/{issueNumber}/labels";
        var response = await _apiClient.PostAsync<List<dynamic>>(endpoint, labels, cancellationToken);

        return response?.Select(label => (string)label.name).ToList() ?? new List<string>();
    }

    public async Task<List<string>> RemoveLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var tasks = labels.Select(async label =>
        {
            var endpoint = $"repos/{owner}/{repository}/issues/{issueNumber}/labels/{label}";
            await _apiClient.DeleteAsync(endpoint, cancellationToken);
        });

        await Task.WhenAll(tasks);

        // Return remaining labels
        var remainingEndpoint = $"repos/{owner}/{repository}/issues/{issueNumber}/labels";
        var response = await _apiClient.GetAsync<List<dynamic>>(remainingEndpoint, cancellationToken);

        return response?.Select(label => (string)label.name).ToList() ?? new List<string>();
    }

    public async Task<List<GitHubUser>> AssignUsersAsync(string owner, string repository, int issueNumber, List<string> assignees, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var requestBody = new { assignees };
        var endpoint = $"repos/{owner}/{repository}/issues/{issueNumber}/assignees";
        var response = await _apiClient.PostAsync<dynamic>(endpoint, requestBody, cancellationToken);

        if (response?.assignees != null)
        {
            return ((IEnumerable<dynamic>)response.assignees)
                .Select(user => new GitHubUser
                {
                    Login = (string)user.login,
                    Name = (string?)user.name ?? (string)user.login
                }).ToList();
        }

        return new List<GitHubUser>();
    }

    public async Task<GitHubIssueSearchResult> SearchIssuesAsync(GitHubIssueSearchQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var searchQuery = BuildSearchQuery(query);
        var endpoint = $"search/issues?q={Uri.EscapeDataString(searchQuery)}&sort={query.Sort}&order={query.Order}&per_page={query.PerPage}&page={query.Page}";

        var response = await _apiClient.GetAsync<dynamic>(endpoint, cancellationToken);

        if (response == null)
        {
            return new GitHubIssueSearchResult();
        }

        var issues = new List<GitHubIssueDetailed>();
        if (response.items != null)
        {
            foreach (var item in response.items)
            {
                var repositoryUrl = (string)item.repository_url;
                issues.Add(MapToDetailedIssue(item, repositoryUrl));
            }
        }

        return new GitHubIssueSearchResult
        {
            TotalCount = (int)response.total_count,
            IncompleteResults = (bool)response.incomplete_results,
            Issues = issues
        };
    }

    public async Task<GitHubIssueStatistics> GetIssueStatisticsAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        // Get open issues
        var openIssues = await ListIssuesAsync(owner, repository, new GitHubIssueFilter { State = "open" }, cancellationToken);

        // Get closed issues (limited sample for performance)
        var closedIssues = await ListIssuesAsync(owner, repository, new GitHubIssueFilter { State = "closed", PerPage = 100 }, cancellationToken);

        var statistics = new GitHubIssueStatistics
        {
            OpenCount = openIssues.Count,
            ClosedCount = closedIssues.Count,
            TotalCount = openIssues.Count + closedIssues.Count,
            LastUpdated = DateTime.UtcNow
        };

        // Calculate label statistics
        var allLabels = openIssues.SelectMany(i => i.Labels).Concat(closedIssues.SelectMany(i => i.Labels));
        statistics.LabelDistribution = allLabels
            .GroupBy(label => label)
            .ToDictionary(g => g.Key, g => g.Count());

        // Calculate assignee statistics
        var allAssignees = openIssues.SelectMany(i => i.Assignees).Concat(closedIssues.SelectMany(i => i.Assignees));
        statistics.AssigneeDistribution = allAssignees
            .GroupBy(user => user.Login)
            .ToDictionary(g => g.Key, g => g.Count());

        return statistics;
    }

    private async Task ProcessBatchIssueAsync(
        SemaphoreSlim semaphore,
        string owner,
        string repository,
        CreateIssueRequest issue,
        int index,
        GitHubBatchIssueResult result,
        bool continueOnError,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var createdIssue = await CreateIssueAsync(owner, repository, issue, cancellationToken);

            lock (result)
            {
                result.CreatedIssues.Add(createdIssue);
                result.SuccessfullyCreated++;
            }
        }
        catch (Exception ex) when (continueOnError)
        {
            lock (result)
            {
                result.Errors.Add(new GitHubBatchError
                {
                    Index = index,
                    RequestTitle = issue.Title,
                    ErrorMessage = ex.Message,
                    StatusCode = ex is GitHubApiException apiEx ? (int)apiEx.StatusCode : null
                });
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!_apiClient.IsAuthenticated)
        {
            var storedToken = await _authService.GetStoredTokenAsync();
            if (storedToken != null && storedToken.IsValid)
            {
                _apiClient.SetAuthenticationToken(storedToken.AccessToken);
            }
            else
            {
                throw new UnauthorizedAccessException("GitHub authentication required. Please run authentication flow first.");
            }
        }
    }

    private static GitHubIssueDetailed MapToDetailedIssue(dynamic issue, string repositoryUrl)
    {
        var result = new GitHubIssueDetailed
        {
            Id = (long)issue.id,
            Number = (int)issue.number,
            Title = (string)issue.title,
            Body = (string?)issue.body ?? string.Empty,
            State = (string)issue.state,
            HtmlUrl = (string)issue.html_url,
            CreatedAt = DateTime.Parse((string)issue.created_at),
            UpdatedAt = issue.updated_at != null ? DateTime.Parse((string)issue.updated_at) : null,
            ClosedAt = issue.closed_at != null ? DateTime.Parse((string)issue.closed_at) : null,
            Comments = (int)issue.comments,
            Locked = (bool)issue.locked,
            RepositoryUrl = repositoryUrl
        };

        // Map labels
        if (issue.labels != null)
        {
            result.Labels = ((IEnumerable<dynamic>)issue.labels)
                .Select(label => (string)label.name)
                .ToList();
        }

        // Map assignees
        if (issue.assignees != null)
        {
            result.Assignees = ((IEnumerable<dynamic>)issue.assignees)
                .Select(user => new GitHubUser
                {
                    Login = (string)user.login,
                    Name = (string?)user.name ?? (string)user.login
                }).ToList();
        }

        // Map user
        if (issue.user != null)
        {
            result.User = new GitHubUser
            {
                Login = (string)issue.user.login,
                Name = (string?)issue.user.name ?? (string)issue.user.login
            };
        }

        // Map milestone
        if (issue.milestone != null)
        {
            result.Milestone = new GitHubMilestone
            {
                Title = (string)issue.milestone.title,
                Number = (int)issue.milestone.number
            };
        }

        return result;
    }

    private static string BuildIssueFilterQuery(GitHubIssueFilter? filter)
    {
        if (filter == null)
        {
            return string.Empty;
        }

        var queryParams = new List<string>();

        if (!string.IsNullOrEmpty(filter.State))
        {
            queryParams.Add($"state={filter.State}");
        }

        if (filter.Labels?.Any() == true)
        {
            queryParams.Add($"labels={string.Join(",", filter.Labels)}");
        }

        if (!string.IsNullOrEmpty(filter.Sort))
        {
            queryParams.Add($"sort={filter.Sort}");
        }

        if (!string.IsNullOrEmpty(filter.Direction))
        {
            queryParams.Add($"direction={filter.Direction}");
        }

        if (filter.Since.HasValue)
        {
            queryParams.Add($"since={filter.Since.Value:yyyy-MM-ddTHH:mm:ssZ}");
        }

        if (filter.PerPage.HasValue)
        {
            queryParams.Add($"per_page={filter.PerPage.Value}");
        }

        if (filter.Page.HasValue)
        {
            queryParams.Add($"page={filter.Page.Value}");
        }

        return queryParams.Any() ? "?" + string.Join("&", queryParams) : string.Empty;
    }

    private static string BuildSearchQuery(GitHubIssueSearchQuery query)
    {
        var queryParts = new List<string>();

        if (!string.IsNullOrEmpty(query.Keywords))
        {
            queryParts.Add(query.Keywords);
        }

        if (!string.IsNullOrEmpty(query.Repository))
        {
            queryParts.Add($"repo:{query.Repository}");
        }

        if (!string.IsNullOrEmpty(query.Author))
        {
            queryParts.Add($"author:{query.Author}");
        }

        if (!string.IsNullOrEmpty(query.Assignee))
        {
            queryParts.Add($"assignee:{query.Assignee}");
        }

        if (query.Labels?.Any() == true)
        {
            foreach (var label in query.Labels)
            {
                queryParts.Add($"label:\"{label}\"");
            }
        }

        if (!string.IsNullOrEmpty(query.State))
        {
            queryParts.Add($"state:{query.State}");
        }

        if (query.CreatedAfter.HasValue)
        {
            queryParts.Add($"created:>={query.CreatedAfter.Value:yyyy-MM-dd}");
        }

        if (query.CreatedBefore.HasValue)
        {
            queryParts.Add($"created:<={query.CreatedBefore.Value:yyyy-MM-dd}");
        }

        queryParts.Add("is:issue"); // Ensure we only get issues, not pull requests

        return string.Join(" ", queryParts);
    }
}

// Additional model classes for the enhanced issues service

/// <summary>
/// Filter options for listing issues
/// </summary>
public class GitHubIssueFilter
{
    public string? State { get; set; } = "open";
    public List<string>? Labels { get; set; }
    public string? Sort { get; set; } = "created";
    public string? Direction { get; set; } = "desc";
    public DateTime? Since { get; set; }
    public int? PerPage { get; set; } = 30;
    public int? Page { get; set; } = 1;
}

/// <summary>
/// Update request for modifying existing issues
/// </summary>
public class UpdateIssueRequest
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? State { get; set; }
    public List<string>? Assignees { get; set; }
    public List<string>? Labels { get; set; }
    public int? Milestone { get; set; }
}

/// <summary>
/// Search query for issues
/// </summary>
public class GitHubIssueSearchQuery
{
    public string? Keywords { get; set; }
    public string? Repository { get; set; }
    public string? Author { get; set; }
    public string? Assignee { get; set; }
    public List<string>? Labels { get; set; }
    public string? State { get; set; } = "open";
    public DateTime? CreatedAfter { get; set; }
    public DateTime? CreatedBefore { get; set; }
    public string Sort { get; set; } = "created";
    public string Order { get; set; } = "desc";
    public int PerPage { get; set; } = 30;
    public int Page { get; set; } = 1;
}

/// <summary>
/// Result of issue search operation
/// </summary>
public class GitHubIssueSearchResult
{
    public int TotalCount { get; set; }
    public bool IncompleteResults { get; set; }
    public List<GitHubIssueDetailed> Issues { get; set; } = new();
}

/// <summary>
/// Issue statistics for a repository
/// </summary>
public class GitHubIssueStatistics
{
    public int OpenCount { get; set; }
    public int ClosedCount { get; set; }
    public int TotalCount { get; set; }
    public Dictionary<string, int> LabelDistribution { get; set; } = new();
    public Dictionary<string, int> AssigneeDistribution { get; set; } = new();
    public DateTime LastUpdated { get; set; }
}