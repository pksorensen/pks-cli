namespace PKS.Infrastructure.Services;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Interface for Jira integration — authentication, project listing, and issue queries
/// </summary>
public interface IJiraService
{
    Task<bool> IsAuthenticatedAsync();
    Task<JiraStoredCredentials?> GetStoredCredentialsAsync();
    Task StoreCredentialsAsync(JiraStoredCredentials credentials);
    Task ClearCredentialsAsync();
    Task<bool> ValidateCredentialsAsync(JiraStoredCredentials credentials);
    Task<JiraDeploymentType> DetectDeploymentTypeAsync(string baseUrl);
    Task<string?> FetchCloudIdAsync(string baseUrl);
    Task<List<JiraProject>> GetProjectsAsync();
    Task<JiraSearchResult> SearchIssuesAsync(string jql, int startAt = 0, int maxResults = 50);
    Task<List<JiraIssue>> GetProjectIssuesAsync(string projectKey, string? epicKey = null);
    Task<List<JiraIssue>> GetIssuesByParentAsync(string parentKey);
    Task<JiraIssue?> GetIssueAsync(string issueKey);
    Task<List<JiraSavedFilter>> GetSavedFiltersAsync();
    Task SaveFilterAsync(JiraSavedFilter filter);
    Task DeleteFilterAsync(string label);
    Task<List<JiraComment>> GetCommentsAsync(string issueKey);
    Task<List<JiraWorklog>> GetWorklogsAsync(string issueKey);
    Task<List<JiraAttachment>> GetAttachmentsAsync(string issueKey);
    Task<List<JiraChangelogEntry>> GetChangelogAsync(string issueKey);
    Task<byte[]> DownloadAttachmentAsync(string contentUrl);
}
