using System.Net;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Mock implementation of GitHubAuthenticationService for testing and development
/// </summary>
public class MockGitHubAuthenticationService : IGitHubAuthenticationService
{
    private readonly Dictionary<string, GitHubStoredToken> _storedTokens = new();
    private readonly Random _random = new();

    public async Task<GitHubDeviceCodeResponse> InitiateDeviceCodeFlowAsync(string[]? scopes = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(500, cancellationToken); // Simulate network delay

        return new GitHubDeviceCodeResponse
        {
            DeviceCode = Guid.NewGuid().ToString("N"),
            UserCode = GenerateUserCode(),
            VerificationUri = "https://github.com/login/device",
            VerificationUriComplete = $"https://github.com/login/device?user_code={GenerateUserCode()}",
            ExpiresIn = 900, // 15 minutes
            Interval = 5
        };
    }

    public async Task<GitHubDeviceAuthStatus> PollForAuthenticationAsync(string deviceCode, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1000, cancellationToken); // Simulate network delay

        // Simulate different authentication states
        var randomValue = _random.Next(1, 11);

        if (randomValue <= 7) // 70% chance of pending
        {
            return new GitHubDeviceAuthStatus
            {
                IsAuthenticated = false,
                Error = "authorization_pending",
                ErrorDescription = "The user has not yet entered the user code",
                CheckedAt = DateTime.UtcNow
            };
        }

        if (randomValue <= 9) // 20% chance of success
        {
            var accessToken = GenerateAccessToken();
            return new GitHubDeviceAuthStatus
            {
                IsAuthenticated = true,
                AccessToken = accessToken,
                Scopes = new[] { "repo", "user:email", "write:packages" },
                ExpiresAt = DateTime.UtcNow.AddHours(8),
                CheckedAt = DateTime.UtcNow
            };
        }

        // 10% chance of denied
        return new GitHubDeviceAuthStatus
        {
            IsAuthenticated = false,
            Error = "access_denied",
            ErrorDescription = "The user denied the authorization request",
            CheckedAt = DateTime.UtcNow
        };
    }

    public async Task<GitHubDeviceAuthStatus> AuthenticateAsync(
        string[]? scopes = null,
        IProgress<GitHubAuthProgress>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var progress = new GitHubAuthProgress
        {
            CurrentStep = GitHubAuthStep.Initializing,
            StatusMessage = "Initializing mock authentication..."
        };
        progressCallback?.Report(progress);

        await Task.Delay(300, cancellationToken);

        // Step 1: Request device code
        progress.CurrentStep = GitHubAuthStep.RequestingDeviceCode;
        progress.StatusMessage = "Requesting device code...";
        progressCallback?.Report(progress);

        var deviceCodeResponse = await InitiateDeviceCodeFlowAsync(scopes, cancellationToken);

        // Step 2: Show user instructions
        progress.CurrentStep = GitHubAuthStep.WaitingForUserAuthorization;
        progress.UserCode = deviceCodeResponse.UserCode;
        progress.VerificationUrl = deviceCodeResponse.VerificationUriComplete;
        progress.TimeRemaining = TimeSpan.FromSeconds(deviceCodeResponse.ExpiresIn);
        progress.StatusMessage = $"Mock: Visit {deviceCodeResponse.VerificationUri} and enter code: {deviceCodeResponse.UserCode}";
        progressCallback?.Report(progress);

        await Task.Delay(2000, cancellationToken); // Simulate user action time

        // Step 3: Poll for authentication (simulate quick success)
        progress.CurrentStep = GitHubAuthStep.PollingForToken;
        progress.StatusMessage = "Polling for authentication...";
        progressCallback?.Report(progress);

        await Task.Delay(1000, cancellationToken);

        // Step 4: Validate token
        progress.CurrentStep = GitHubAuthStep.ValidatingToken;
        progress.StatusMessage = "Validating authentication token...";
        progressCallback?.Report(progress);

        await Task.Delay(500, cancellationToken);

        var accessToken = GenerateAccessToken();
        var storedToken = new GitHubStoredToken
        {
            AccessToken = accessToken,
            Scopes = scopes ?? new[] { "repo", "user:email", "write:packages" },
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(8),
            IsValid = true,
            LastValidated = DateTime.UtcNow,
            AssociatedUser = "mock-user"
        };

        await StoreTokenAsync(storedToken);

        progress.CurrentStep = GitHubAuthStep.Complete;
        progress.IsComplete = true;
        progress.StatusMessage = "Mock authentication completed successfully!";
        progressCallback?.Report(progress);

        return new GitHubDeviceAuthStatus
        {
            IsAuthenticated = true,
            AccessToken = accessToken,
            Scopes = storedToken.Scopes,
            ExpiresAt = storedToken.ExpiresAt,
            CheckedAt = DateTime.UtcNow
        };
    }

    public async Task<GitHubTokenValidation> ValidateTokenAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        await Task.Delay(200, cancellationToken);

        // Simulate validation based on token format
        var isValid = accessToken.StartsWith("ghp_mock_") && accessToken.Length > 20;

        return new GitHubTokenValidation
        {
            IsValid = isValid,
            Scopes = isValid ? new[] { "repo", "user:email", "write:packages" } : Array.Empty<string>(),
            ValidatedAt = DateTime.UtcNow,
            ErrorMessage = isValid ? null : "Invalid mock token format"
        };
    }

    public async Task<bool> StoreTokenAsync(GitHubStoredToken token, string? associatedUser = null)
    {
        await Task.Delay(50);
        var key = associatedUser ?? "default";
        _storedTokens[key] = token;
        return true;
    }

    public async Task<GitHubStoredToken?> GetStoredTokenAsync(string? associatedUser = null)
    {
        await Task.Delay(50);
        var key = associatedUser ?? "default";
        return _storedTokens.TryGetValue(key, out var token) ? token : null;
    }

    public async Task<bool> ClearStoredTokenAsync(string? associatedUser = null)
    {
        await Task.Delay(50);
        var key = associatedUser ?? "default";
        return _storedTokens.Remove(key);
    }

    public async Task<bool> IsAuthenticatedAsync(string? associatedUser = null)
    {
        var token = await GetStoredTokenAsync(associatedUser);
        return token != null && token.IsValid &&
               (!token.ExpiresAt.HasValue || token.ExpiresAt.Value > DateTime.UtcNow);
    }

    public Task<GitHubStoredToken?> RefreshTokenAsync(string? associatedUser = null)
    {
        return Task.FromResult<GitHubStoredToken?>(null);
    }

    private static string GenerateUserCode()
    {
        var random = new Random();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, 8)
            .Select(s => s[random.Next(s.Length)]).ToArray())
            .Insert(4, "-"); // Format: ABCD-1234
    }

    private static string GenerateAccessToken()
    {
        return $"ghp_mock_{Guid.NewGuid():N}";
    }
}

/// <summary>
/// Mock implementation of GitHubApiClient for testing and development
/// </summary>
public class MockGitHubApiClient : IGitHubApiClient, IDisposable
{
    private readonly Dictionary<string, object> _mockData = new();
    private readonly Random _random = new();
    private string? _accessToken;
    private int _requestCount;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

    public MockGitHubApiClient()
    {
        InitializeMockData();
    }

    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken cancellationToken = default) where T : class
    {
        await SimulateNetworkDelay();
        _requestCount++;

        if (endpoint.Contains("rate_limit"))
        {
            return CreateMockRateLimit() as T;
        }

        if (endpoint.Contains("/issues/"))
        {
            var issueId = ExtractIssueNumber(endpoint);
            return CreateMockIssue(issueId) as T;
        }

        if (endpoint.Contains("/issues"))
        {
            return CreateMockIssueList() as T;
        }

        if (endpoint.Contains("/repos/"))
        {
            return CreateMockRepository() as T;
        }

        return default(T);
    }

    public async Task<T?> PostAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        await SimulateNetworkDelay();
        _requestCount++;

        if (endpoint.Contains("/issues"))
        {
            return CreateMockIssue(_random.Next(1, 1000)) as T;
        }

        if (endpoint.Contains("/assignees"))
        {
            return CreateMockAssignmentResult() as T;
        }

        return default(T);
    }

    public async Task<T?> PutAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        await SimulateNetworkDelay();
        _requestCount++;
        return CreateMockIssue(_random.Next(1, 1000)) as T;
    }

    public async Task<T?> PatchAsync<T>(string endpoint, object? body = null, CancellationToken cancellationToken = default) where T : class
    {
        await SimulateNetworkDelay();
        _requestCount++;
        return CreateMockIssue(_random.Next(1, 1000)) as T;
    }

    public async Task<bool> DeleteAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        await SimulateNetworkDelay();
        _requestCount++;
        return true;
    }

    public async Task<GitHubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default)
    {
        await SimulateNetworkDelay();
        return new GitHubRateLimit
        {
            Limit = 5000,
            Remaining = Math.Max(0, 5000 - _requestCount),
            Used = _requestCount,
            ResetTime = DateTime.UtcNow.AddHours(1),
            Resource = "core"
        };
    }

    public void SetAuthenticationToken(string accessToken)
    {
        _accessToken = accessToken;
    }

    public void ClearAuthenticationToken()
    {
        _accessToken = null;
    }

    private void InitializeMockData()
    {
        // Initialize with some mock data
        _mockData["mock_user"] = new
        {
            login = "mock-user",
            name = "Mock User",
            id = 12345
        };
    }

    private async Task SimulateNetworkDelay()
    {
        var delay = _random.Next(50, 300); // 50-300ms delay
        await Task.Delay(delay);
    }

    private object CreateMockRateLimit()
    {
        return new
        {
            resources = new
            {
                core = new
                {
                    limit = 5000,
                    remaining = Math.Max(0, 5000 - _requestCount),
                    reset = ((DateTimeOffset)DateTime.UtcNow.AddHours(1)).ToUnixTimeSeconds(),
                    used = _requestCount
                }
            }
        };
    }

    private object CreateMockIssue(int issueNumber)
    {
        return new
        {
            id = _random.Next(100000, 999999),
            number = issueNumber,
            title = $"Mock Issue #{issueNumber}",
            body = $"This is a mock issue body for issue #{issueNumber}",
            state = _random.Next(0, 2) == 0 ? "open" : "closed",
            html_url = $"https://github.com/mock-owner/mock-repo/issues/{issueNumber}",
            created_at = DateTime.UtcNow.AddDays(-_random.Next(1, 30)).ToString("O"),
            updated_at = DateTime.UtcNow.AddHours(-_random.Next(1, 24)).ToString("O"),
            closed_at = (string?)null,
            comments = _random.Next(0, 10),
            locked = false,
            labels = new[]
            {
                new { name = "bug" },
                new { name = "enhancement" }
            },
            assignees = new[]
            {
                new { login = "mock-user", name = "Mock User" }
            },
            user = new { login = "issue-creator", name = "Issue Creator" },
            milestone = new { title = "v1.0.0", number = 1 }
        };
    }

    private object CreateMockIssueList()
    {
        var issues = new List<object>();
        var count = _random.Next(3, 10);

        for (int i = 1; i <= count; i++)
        {
            issues.Add(CreateMockIssue(i));
        }

        return issues;
    }

    private object CreateMockRepository()
    {
        return new
        {
            id = _random.Next(100000, 999999),
            name = "mock-repo",
            full_name = "mock-owner/mock-repo",
            description = "Mock repository for testing",
            clone_url = "https://github.com/mock-owner/mock-repo.git",
            html_url = "https://github.com/mock-owner/mock-repo",
            @private = false,
            owner = new { login = "mock-owner" },
            created_at = DateTime.UtcNow.AddMonths(-6).ToString("O")
        };
    }

    private object CreateMockAssignmentResult()
    {
        return new
        {
            assignees = new[]
            {
                new { login = "assignee1", name = "Assignee One" },
                new { login = "assignee2", name = "Assignee Two" }
            }
        };
    }

    private static int ExtractIssueNumber(string endpoint)
    {
        var parts = endpoint.Split('/');
        var issuesIndex = Array.IndexOf(parts, "issues");

        if (issuesIndex >= 0 && issuesIndex + 1 < parts.Length &&
            int.TryParse(parts[issuesIndex + 1], out var issueNumber))
        {
            return issueNumber;
        }

        return new Random().Next(1, 1000);
    }

    public void Dispose()
    {
        // Nothing to dispose in mock implementation
    }
}

/// <summary>
/// Mock implementation of GitHubIssuesService for testing and development
/// </summary>
public class MockGitHubIssuesService : IGitHubIssuesService
{
    private readonly Dictionary<string, List<GitHubIssueDetailed>> _repositoryIssues = new();
    private readonly Random _random = new();
    private int _nextIssueId = 1;

    public async Task<GitHubIssueDetailed> CreateIssueAsync(string owner, string repository, CreateIssueRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(200, 500), cancellationToken);

        var issue = new GitHubIssueDetailed
        {
            Id = _nextIssueId++,
            Number = _nextIssueId,
            Title = request.Title,
            Body = request.Body,
            State = "open",
            HtmlUrl = $"https://github.com/{owner}/{repository}/issues/{_nextIssueId}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Comments = 0,
            Locked = false,
            Labels = request.Labels.ToList(),
            RepositoryUrl = $"https://github.com/{owner}/{repository}",
            Assignees = request.Assignees.Select(a => new GitHubUser { Login = a, Name = a }).ToList(),
            User = new GitHubUser { Login = "mock-creator", Name = "Mock Creator" }
        };

        var repoKey = $"{owner}/{repository}";
        if (!_repositoryIssues.ContainsKey(repoKey))
        {
            _repositoryIssues[repoKey] = new List<GitHubIssueDetailed>();
        }
        _repositoryIssues[repoKey].Add(issue);

        return issue;
    }

    public async Task<GitHubBatchIssueResult> CreateIssuesBatchAsync(GitHubBatchIssueRequest request, IProgress<int>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = new GitHubBatchIssueResult
        {
            TotalRequested = request.Issues.Count
        };

        for (int i = 0; i < request.Issues.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var issue = await CreateIssueAsync(request.Owner, request.Repository, request.Issues[i], cancellationToken);
                result.CreatedIssues.Add(issue);
                result.SuccessfullyCreated++;
            }
            catch (Exception ex)
            {
                result.Errors.Add(new GitHubBatchError
                {
                    Index = i,
                    RequestTitle = request.Issues[i].Title,
                    ErrorMessage = ex.Message
                });
            }

            progressCallback?.Report(i + 1);

            // Simulate processing time
            await Task.Delay(_random.Next(50, 200), cancellationToken);
        }

        stopwatch.Stop();
        result.TotalTime = stopwatch.Elapsed;
        result.Failed = result.TotalRequested - result.SuccessfullyCreated;

        return result;
    }

    public async Task<GitHubIssueDetailed?> GetIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(100, 300), cancellationToken);

        var repoKey = $"{owner}/{repository}";
        if (_repositoryIssues.TryGetValue(repoKey, out var issues))
        {
            return issues.FirstOrDefault(i => i.Number == issueNumber);
        }

        // Return a mock issue if not found in our store
        return CreateMockIssue(owner, repository, issueNumber);
    }

    public async Task<List<GitHubIssueDetailed>> ListIssuesAsync(string owner, string repository, GitHubIssueFilter? filter = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(200, 500), cancellationToken);

        var repoKey = $"{owner}/{repository}";
        if (_repositoryIssues.TryGetValue(repoKey, out var issues))
        {
            var filteredIssues = issues.AsEnumerable();

            if (filter != null)
            {
                if (!string.IsNullOrEmpty(filter.State))
                {
                    filteredIssues = filteredIssues.Where(i => i.State == filter.State);
                }

                if (filter.Labels?.Any() == true)
                {
                    filteredIssues = filteredIssues.Where(i =>
                        filter.Labels.Any(label => i.Labels.Contains(label)));
                }
            }

            return filteredIssues.Take(filter?.PerPage ?? 30).ToList();
        }

        // Return mock issues if repository not found
        return GenerateMockIssues(owner, repository, filter?.PerPage ?? 5);
    }

    public async Task<GitHubIssueDetailed> UpdateIssueAsync(string owner, string repository, int issueNumber, UpdateIssueRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(200, 400), cancellationToken);

        var repoKey = $"{owner}/{repository}";
        if (_repositoryIssues.TryGetValue(repoKey, out var issues))
        {
            var issue = issues.FirstOrDefault(i => i.Number == issueNumber);
            if (issue != null)
            {
                if (!string.IsNullOrEmpty(request.Title))
                    issue.Title = request.Title;
                if (!string.IsNullOrEmpty(request.Body))
                    issue.Body = request.Body;
                if (!string.IsNullOrEmpty(request.State))
                    issue.State = request.State;
                if (request.Labels != null)
                    issue.Labels = request.Labels;

                issue.UpdatedAt = DateTime.UtcNow;
                return issue;
            }
        }

        throw new GitHubApiException($"Issue #{issueNumber} not found", HttpStatusCode.NotFound);
    }

    public async Task<GitHubIssueDetailed> CloseIssueAsync(string owner, string repository, int issueNumber, string? closeReason = null, CancellationToken cancellationToken = default)
    {
        var updateRequest = new UpdateIssueRequest { State = "closed" };
        var issue = await UpdateIssueAsync(owner, repository, issueNumber, updateRequest, cancellationToken);
        issue.ClosedAt = DateTime.UtcNow;
        return issue;
    }

    public async Task<GitHubIssueDetailed> ReopenIssueAsync(string owner, string repository, int issueNumber, CancellationToken cancellationToken = default)
    {
        var updateRequest = new UpdateIssueRequest { State = "open" };
        var issue = await UpdateIssueAsync(owner, repository, issueNumber, updateRequest, cancellationToken);
        issue.ClosedAt = null;
        return issue;
    }

    public async Task<List<string>> AddLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(100, 200), cancellationToken);

        var issue = await GetIssueAsync(owner, repository, issueNumber, cancellationToken);
        if (issue != null)
        {
            var existingLabels = issue.Labels.ToHashSet();
            existingLabels.UnionWith(labels);
            issue.Labels = existingLabels.ToList();
            return issue.Labels;
        }

        return labels;
    }

    public async Task<List<string>> RemoveLabelsAsync(string owner, string repository, int issueNumber, List<string> labels, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(100, 200), cancellationToken);

        var issue = await GetIssueAsync(owner, repository, issueNumber, cancellationToken);
        if (issue != null)
        {
            var existingLabels = issue.Labels.ToHashSet();
            existingLabels.ExceptWith(labels);
            issue.Labels = existingLabels.ToList();
            return issue.Labels;
        }

        return new List<string>();
    }

    public async Task<List<GitHubUser>> AssignUsersAsync(string owner, string repository, int issueNumber, List<string> assignees, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(100, 200), cancellationToken);

        return assignees.Select(a => new GitHubUser { Login = a, Name = a }).ToList();
    }

    public async Task<GitHubIssueSearchResult> SearchIssuesAsync(GitHubIssueSearchQuery query, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(300, 700), cancellationToken);

        var mockIssues = GenerateMockIssues("search-owner", "search-repo", query.PerPage);

        return new GitHubIssueSearchResult
        {
            TotalCount = mockIssues.Count + _random.Next(10, 100),
            IncompleteResults = false,
            Issues = mockIssues
        };
    }

    public async Task<GitHubIssueStatistics> GetIssueStatisticsAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_random.Next(500, 1000), cancellationToken);

        return new GitHubIssueStatistics
        {
            OpenCount = _random.Next(5, 25),
            ClosedCount = _random.Next(10, 50),
            TotalCount = _random.Next(15, 75),
            LabelDistribution = new Dictionary<string, int>
            {
                ["bug"] = _random.Next(3, 12),
                ["enhancement"] = _random.Next(2, 8),
                ["documentation"] = _random.Next(1, 5),
                ["question"] = _random.Next(1, 6)
            },
            AssigneeDistribution = new Dictionary<string, int>
            {
                ["developer1"] = _random.Next(2, 10),
                ["developer2"] = _random.Next(1, 8),
                ["maintainer"] = _random.Next(3, 15)
            },
            LastUpdated = DateTime.UtcNow
        };
    }

    private GitHubIssueDetailed CreateMockIssue(string owner, string repository, int issueNumber)
    {
        return new GitHubIssueDetailed
        {
            Id = _nextIssueId++,
            Number = issueNumber,
            Title = $"Mock Issue #{issueNumber}",
            Body = $"This is a mock issue body for issue #{issueNumber} in {owner}/{repository}",
            State = _random.Next(0, 2) == 0 ? "open" : "closed",
            HtmlUrl = $"https://github.com/{owner}/{repository}/issues/{issueNumber}",
            CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 30)),
            UpdatedAt = DateTime.UtcNow.AddHours(-_random.Next(1, 24)),
            Comments = _random.Next(0, 10),
            Locked = false,
            Labels = new List<string> { "mock", "test" },
            RepositoryUrl = $"https://github.com/{owner}/{repository}",
            Assignees = new List<GitHubUser> { new() { Login = "mock-assignee", Name = "Mock Assignee" } },
            User = new GitHubUser { Login = "mock-creator", Name = "Mock Creator" }
        };
    }

    private List<GitHubIssueDetailed> GenerateMockIssues(string owner, string repository, int count)
    {
        var issues = new List<GitHubIssueDetailed>();
        for (int i = 1; i <= count; i++)
        {
            issues.Add(CreateMockIssue(owner, repository, i));
        }
        return issues;
    }
}