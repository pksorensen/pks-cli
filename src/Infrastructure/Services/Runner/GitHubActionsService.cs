using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Runner;

/// <summary>
/// Service for interacting with GitHub Actions API endpoints (JIT runners, workflow runs, permissions)
/// </summary>
public class GitHubActionsService : IGitHubActionsService
{
    private readonly IGitHubApiClient _apiClient;
    private readonly IGitHubAuthenticationService _authService;

    public GitHubActionsService(IGitHubApiClient apiClient, IGitHubAuthenticationService authService)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _authService = authService ?? throw new ArgumentNullException(nameof(authService));
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (!_apiClient.IsAuthenticated)
        {
            var storedToken = await _authService.GetStoredTokenAsync();
            if (storedToken != null && !string.IsNullOrEmpty(storedToken.AccessToken))
            {
                _apiClient.SetAuthenticationToken(storedToken.AccessToken);
            }
        }
    }

    /// <inheritdoc />
    public async Task<GitHubJitRunnerConfig> GenerateJitConfigAsync(
        string owner,
        string repo,
        string name,
        string[] labels,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync();
        var endpoint = $"repos/{owner}/{repo}/actions/runners/generate-jitconfig";

        var body = new
        {
            name,
            runner_group_id = 1,
            labels
        };

        var response = await _apiClient.PostAsync<GitHubJitRunnerConfigResponse>(endpoint, body, cancellationToken);

        if (response == null)
        {
            throw new GitHubApiException("Failed to generate JIT runner config: null response", System.Net.HttpStatusCode.InternalServerError);
        }

        return new GitHubJitRunnerConfig
        {
            RunnerId = response.Runner.Id,
            EncodedJitConfig = response.EncodedJitConfig
        };
    }

    /// <inheritdoc />
    public async Task<List<QueuedWorkflowRun>> GetQueuedRunsAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync();
        var endpoint = $"repos/{owner}/{repo}/actions/runs?status=queued&per_page=10";

        var response = await _apiClient.GetAsync<WorkflowRunsResponse>(endpoint, cancellationToken);

        return response?.WorkflowRuns ?? new List<QueuedWorkflowRun>();
    }

    /// <inheritdoc />
    public async Task<bool> CheckAdminPermissionAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticatedAsync();
            var endpoint = $"repos/{owner}/{repo}";
            var response = await _apiClient.GetAsync<GitHubRepositoryResponse>(endpoint, cancellationToken);

            return response?.Permissions?.Admin ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<(bool IsInstalled, bool HasAdmin)> CheckAppInstallationAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticatedAsync();
            var endpoint = $"repos/{owner}/{repo}";
            var response = await _apiClient.GetAsync<GitHubRepositoryResponse>(endpoint, cancellationToken);

            if (response == null)
                return (false, false);

            // If we get a response, the app is installed (or the token has access)
            var hasAdmin = response.Permissions?.Admin ?? false;
            return (true, hasAdmin);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 404 means app is not installed on this repo
            return (false, false);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 403 means app is installed but doesn't have sufficient permissions
            return (true, false);
        }
        catch
        {
            return (false, false);
        }
    }
}
