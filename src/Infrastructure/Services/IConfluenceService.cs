namespace PKS.Infrastructure.Services;

using PKS.Infrastructure.Services.Models;

/// <summary>
/// Confluence REST API client. Reuses Jira authentication (same Atlassian credentials).
/// </summary>
public interface IConfluenceService
{
    Task<bool> IsAuthenticatedAsync();

    Task<List<ConfluenceSpaceInfo>> GetSpacesAsync();
    Task<ConfluencePage?> GetPageByIdAsync(string pageId, bool expandBody = true);
    Task<ConfluencePage?> FindPageByTitleAsync(string spaceKey, string title);
    Task<List<ConfluencePage>> GetChildPagesAsync(string parentId);
    Task<List<ConfluencePage>> GetPageTreeAsync(string rootPageId, int maxDepth = 10);
    Task<ConfluencePage> UpdatePageAsync(string pageId, string title, string storageFormatHtml, int currentVersion);
    Task<ConfluencePage> CreatePageAsync(string spaceKey, string title, string storageFormatHtml, string? parentId = null);
    Task<bool> DeletePageAsync(string pageId);
    Task<string> UploadAttachmentAsync(string pageId, string filePath, string? comment = null);

    /// <summary>Fetch all footer + inline comments on a page, including nested replies. Read-only.</summary>
    Task<List<ConfluenceComment>> GetPageCommentsAsync(string pageId);

    Task<ConfluenceWorkspaceConfig?> LoadWorkspaceConfigAsync(string workingDir);
    Task SaveWorkspaceConfigAsync(string workingDir, ConfluenceWorkspaceConfig config);
}
