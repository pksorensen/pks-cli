using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Jira integration service — authentication, project listing, and issue queries.
/// Uses Jira REST API v3 with Basic auth (email + API token).
/// </summary>
public class JiraService : IJiraService
{
    private const string KeyPrefix = "jira:";
    private const string KeyBaseUrl = $"{KeyPrefix}base_url";
    private const string KeyEmail = $"{KeyPrefix}email";
    private const string KeyUsername = $"{KeyPrefix}username";
    private const string KeyApiToken = $"{KeyPrefix}api_token";
    private const string KeyAuthMethod = $"{KeyPrefix}auth_method";
    private const string KeyDeploymentType = $"{KeyPrefix}deployment_type";
    private const string KeyAccessToken = $"{KeyPrefix}access_token";
    private const string KeyRefreshToken = $"{KeyPrefix}refresh_token";
    private const string KeyCloudId = $"{KeyPrefix}cloud_id";
    private const string KeyCreatedAt = $"{KeyPrefix}created_at";
    private const string KeyLastRefreshedAt = $"{KeyPrefix}last_refreshed_at";
    private const string KeySavedFilters = $"{KeyPrefix}saved_filters";
    private const string KeyAcFieldId = $"{KeyPrefix}ac_field_id";

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<JiraService> _logger;

    /// <summary>Cached acceptance criteria custom field ID (e.g. "customfield_10035")</summary>
    private string? _acceptanceCriteriaFieldId;
    private bool _acFieldDiscovered;
    private Dictionary<string, string>? _fieldNamesCache;

    /// <summary>
    /// When set, HTTP request/response details are written via this callback.
    /// Set from commands when --debug flag is used.
    /// </summary>
    public Action<string>? DebugWriter { get; set; }

    public JiraService(
        HttpClient httpClient,
        IConfigurationService configurationService,
        ILogger<JiraService> logger)
    {
        _httpClient = httpClient;
        _configurationService = configurationService;
        _logger = logger;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var baseUrl = await _configurationService.GetAsync(KeyBaseUrl);
        if (string.IsNullOrEmpty(baseUrl))
            return false;

        var token = await _configurationService.GetAsync(KeyApiToken);
        var authMethod = await _configurationService.GetAsync(KeyAuthMethod);

        // If we have a base URL and an auth method was stored, consider authenticated
        // (tokens may be stored encrypted and read back differently)
        if (!string.IsNullOrEmpty(authMethod))
            return true;

        // Fallback: check for token presence
        return !string.IsNullOrEmpty(token);
    }

    public async Task<JiraStoredCredentials?> GetStoredCredentialsAsync()
    {
        try
        {
            var baseUrl = await _configurationService.GetAsync(KeyBaseUrl);
            if (string.IsNullOrEmpty(baseUrl))
                return null;

            var authMethodStr = await _configurationService.GetAsync(KeyAuthMethod);
            var authMethod = Enum.TryParse<JiraAuthMethod>(authMethodStr, out var parsed)
                ? parsed
                : JiraAuthMethod.ApiToken;

            var deploymentTypeStr = await _configurationService.GetAsync(KeyDeploymentType);
            var deploymentType = Enum.TryParse<JiraDeploymentType>(deploymentTypeStr, out var dtParsed)
                ? dtParsed
                : JiraDeploymentType.Cloud;

            var createdAtStr = await _configurationService.GetAsync(KeyCreatedAt);
            var lastRefreshedStr = await _configurationService.GetAsync(KeyLastRefreshedAt);

            return new JiraStoredCredentials
            {
                AuthMethod = authMethod,
                DeploymentType = deploymentType,
                BaseUrl = baseUrl,
                Email = await _configurationService.GetAsync(KeyEmail) ?? string.Empty,
                Username = await _configurationService.GetAsync(KeyUsername) ?? string.Empty,
                ApiToken = await _configurationService.GetAsync(KeyApiToken) ?? string.Empty,
                AccessToken = await _configurationService.GetAsync(KeyAccessToken) ?? string.Empty,
                RefreshToken = await _configurationService.GetAsync(KeyRefreshToken) ?? string.Empty,
                CloudId = await _configurationService.GetAsync(KeyCloudId) ?? string.Empty,
                CreatedAt = DateTime.TryParse(createdAtStr, out var created) ? created : DateTime.MinValue,
                LastRefreshedAt = DateTime.TryParse(lastRefreshedStr, out var refreshed) ? refreshed : DateTime.MinValue
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read stored Jira credentials");
            return null;
        }
    }

    public async Task StoreCredentialsAsync(JiraStoredCredentials credentials)
    {
        var normalized = NormalizeCredentials(credentials);

        await _configurationService.SetAsync(KeyAuthMethod, normalized.AuthMethod.ToString(), global: true);
        await _configurationService.SetAsync(KeyDeploymentType, normalized.DeploymentType.ToString(), global: true);
        await _configurationService.SetAsync(KeyBaseUrl, normalized.BaseUrl, global: true);
        await _configurationService.SetAsync(KeyEmail, normalized.Email, global: true);
        await _configurationService.SetAsync(KeyUsername, normalized.Username, global: true);
        await _configurationService.SetAsync(KeyApiToken, normalized.ApiToken, global: true, encrypt: false);
        await _configurationService.SetAsync(KeyAccessToken, normalized.AccessToken, global: true, encrypt: false);
        await _configurationService.SetAsync(KeyRefreshToken, normalized.RefreshToken, global: true, encrypt: false);
        await _configurationService.SetAsync(KeyCloudId, normalized.CloudId, global: true);
        await _configurationService.SetAsync(KeyCreatedAt, normalized.CreatedAt.ToString("O"), global: true);
        await _configurationService.SetAsync(KeyLastRefreshedAt, normalized.LastRefreshedAt.ToString("O"), global: true);
    }

    public async Task ClearCredentialsAsync()
    {
        await _configurationService.DeleteAsync(KeyBaseUrl);
        await _configurationService.DeleteAsync(KeyEmail);
        await _configurationService.DeleteAsync(KeyUsername);
        await _configurationService.DeleteAsync(KeyApiToken);
        await _configurationService.DeleteAsync(KeyAuthMethod);
        await _configurationService.DeleteAsync(KeyDeploymentType);
        await _configurationService.DeleteAsync(KeyAccessToken);
        await _configurationService.DeleteAsync(KeyRefreshToken);
        await _configurationService.DeleteAsync(KeyCloudId);
        await _configurationService.DeleteAsync(KeyCreatedAt);
        await _configurationService.DeleteAsync(KeyLastRefreshedAt);
        await _configurationService.DeleteAsync(KeySavedFilters);
        await _configurationService.DeleteAsync(KeyAcFieldId);
    }

    public async Task<bool> ValidateCredentialsAsync(JiraStoredCredentials credentials)
    {
        try
        {
            var normalized = NormalizeCredentials(credentials);
            var apiBaseUrl = GetApiBaseUrl(normalized);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}/myself");
            ApplyAuth(request, normalized);

            var response = await SendWithDebugAsync(request);

            // If direct URL fails with 401 on Cloud and we don't have a cloudId yet,
            // try fetching cloudId and using the gateway URL (needed for scoped tokens)
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                && normalized.DeploymentType == JiraDeploymentType.Cloud
                && string.IsNullOrEmpty(normalized.CloudId))
            {
                DebugWriter?.Invoke("[yellow]Direct URL returned 401 — trying Atlassian gateway with cloudId...[/]");
                var cloudId = await FetchCloudIdAsync(normalized.BaseUrl);
                if (!string.IsNullOrEmpty(cloudId))
                {
                    normalized.CloudId = cloudId;
                    credentials.CloudId = cloudId; // propagate back so it gets stored
                    var gatewayUrl = GetApiBaseUrl(normalized);
                    var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"{gatewayUrl}/myself");
                    ApplyAuth(retryRequest, normalized);
                    response = await SendWithDebugAsync(retryRequest);
                }
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Jira credentials");
            return false;
        }
    }

    public async Task<JiraDeploymentType> DetectDeploymentTypeAsync(string baseUrl)
    {
        try
        {
            var url = baseUrl.Trim().TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/rest/api/2/serverInfo");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await SendWithDebugAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);

                if (doc.RootElement.TryGetProperty("deploymentType", out var dtProp))
                {
                    var dtValue = dtProp.GetString();
                    if (!string.IsNullOrEmpty(dtValue) &&
                        dtValue.Equals("Cloud", StringComparison.OrdinalIgnoreCase))
                    {
                        return JiraDeploymentType.Cloud;
                    }
                    // Server or Data Center
                    return JiraDeploymentType.Server;
                }

                // serverInfo responded but no deploymentType — likely Server/DC
                return JiraDeploymentType.Server;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect deployment type from serverInfo, defaulting to Cloud");
        }

        return JiraDeploymentType.Cloud;
    }

    public async Task<string?> FetchCloudIdAsync(string baseUrl)
    {
        try
        {
            var url = baseUrl.Trim().TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/_edge/tenant_info");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await SendWithDebugAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("cloudId", out var cloudIdProp))
                {
                    return cloudIdProp.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch cloudId from tenant_info");
        }

        return null;
    }

    public async Task<List<JiraProject>> GetProjectsAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}/project");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var projects = new List<JiraProject>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            projects.Add(new JiraProject
            {
                Id = element.GetProperty("id").GetString() ?? string.Empty,
                Key = element.GetProperty("key").GetString() ?? string.Empty,
                Name = element.GetProperty("name").GetString() ?? string.Empty
            });
        }

        return projects;
    }

    public async Task<JiraSearchResult> SearchIssuesAsync(string jql, int startAt = 0, int maxResults = 50)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        // Discover acceptance criteria field (cached after first call)
        var acFieldId = await DiscoverAcceptanceCriteriaFieldAsync(credentials);

        var fieldsList = new List<string> {
            "summary", "status", "issuetype", "priority", "assignee", "parent", "project",
            "description", "labels", "components", "timeoriginalestimate", "timespent",
            "story_points", "customfield_10016", "reporter", "resolution", "created", "updated",
            "issuelinks"
        };
        if (acFieldId != null) fieldsList.Add(acFieldId);
        var fields = fieldsList.ToArray();

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var isCloud = credentials.DeploymentType == JiraDeploymentType.Cloud;
        var searchPath = isCloud ? "search/jql" : "search";

        // Cap page size at 100 (Jira Cloud maximum)
        var pageSize = Math.Min(maxResults, 100);

        var allIssues = new List<JiraIssue>();

        if (isCloud)
        {
            // Cloud uses token-based pagination via nextPageToken
            string? nextPageToken = null;

            do
            {
                var bodyObj = nextPageToken == null
                    ? new Dictionary<string, object> { ["jql"] = jql, ["maxResults"] = pageSize, ["fields"] = fields }
                    : new Dictionary<string, object> { ["jql"] = jql, ["maxResults"] = pageSize, ["fields"] = fields, ["nextPageToken"] = nextPageToken };

                var requestBody = JsonSerializer.Serialize(bodyObj);
                var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}/{searchPath}")
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                ApplyAuth(request, credentials);

                var response = await SendWithDebugAsync(request);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                var pageIssues = ParseIssues(doc.RootElement, acFieldId);
                allIssues.AddRange(pageIssues);

                // Check for next page token
                nextPageToken = doc.RootElement.TryGetProperty("nextPageToken", out var tokenProp)
                    && tokenProp.ValueKind == JsonValueKind.String
                    ? tokenProp.GetString()
                    : null;

                // Stop if no issues returned (safety)
                if (pageIssues.Count == 0)
                    break;

            } while (nextPageToken != null);
        }
        else
        {
            // Server uses offset-based pagination via startAt/total
            var currentStartAt = startAt;
            int total;

            do
            {
                var requestBody = JsonSerializer.Serialize(new
                {
                    jql,
                    startAt = currentStartAt,
                    maxResults = pageSize,
                    fields
                });

                var request = new HttpRequestMessage(HttpMethod.Post, $"{apiBaseUrl}/{searchPath}")
                {
                    Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
                };
                ApplyAuth(request, credentials);

                var response = await SendWithDebugAsync(request);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(content);
                var pageIssues = ParseIssues(doc.RootElement, acFieldId);
                total = doc.RootElement.TryGetProperty("total", out var totalProp) && totalProp.ValueKind == JsonValueKind.Number
                    ? totalProp.GetInt32()
                    : pageIssues.Count;

                allIssues.AddRange(pageIssues);
                currentStartAt += pageIssues.Count;

                if (pageIssues.Count == 0 || pageIssues.Count < pageSize)
                    break;

            } while (currentStartAt < total);
        }

        return new JiraSearchResult
        {
            Total = allIssues.Count,
            Issues = allIssues
        };
    }

    public async Task<List<JiraIssue>> GetProjectIssuesAsync(string projectKey, string? epicKey = null)
    {
        var jql = epicKey != null
            ? $"project = {projectKey} AND parent = {epicKey} ORDER BY created ASC"
            : $"project = {projectKey} ORDER BY created ASC";

        var result = await SearchIssuesAsync(jql, maxResults: 100);
        return result.Issues;
    }

    public async Task<List<JiraIssue>> GetIssuesByParentAsync(string parentKey)
    {
        var jql = $"parent = {parentKey} ORDER BY created ASC";
        var result = await SearchIssuesAsync(jql, maxResults: 100);
        return result.Issues;
    }

    public async Task<JiraIssue?> GetIssueAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var acFieldId = await DiscoverAcceptanceCriteriaFieldAsync(credentials);
        var fields = "summary,status,issuetype,priority,assignee,parent,project,description,labels,components,timeoriginalestimate,timespent,customfield_10016,story_points,reporter,resolution,created,updated,issuelinks";
        if (acFieldId != null) fields += $",{acFieldId}";

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}?fields={fields}");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
        }

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        return ParseIssue(doc.RootElement, acFieldId);
    }

    public async Task<JiraIssue?> GetIssueWithAllFieldsAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var acFieldId = await DiscoverAcceptanceCriteriaFieldAsync(credentials);

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
        }

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        var issue = ParseIssue(doc.RootElement, acFieldId);

        // Capture ALL fields from the raw JSON into the RawFields dictionary
        if (doc.RootElement.TryGetProperty("fields", out var fieldsElement))
        {
            issue.RawFields = new Dictionary<string, JsonElement>();
            foreach (var prop in fieldsElement.EnumerateObject())
            {
                issue.RawFields[prop.Name] = prop.Value.Clone();
            }

            // Include field name mapping so consumers can resolve customfield IDs
            var nameMap = await GetFieldNamesAsync(credentials);
            if (nameMap != null)
                issue.RawFieldNames = nameMap;

            // Fallback: if AC wasn't extracted (discovery missed the field name),
            // try the stored config field ID, then scan RawFields using field metadata
            if (string.IsNullOrEmpty(issue.AcceptanceCriteria))
            {
                // 1. Try stored config field ID
                try
                {
                    var storedAcField = await _configurationService.GetAsync(KeyAcFieldId);
                    if (!string.IsNullOrEmpty(storedAcField)
                        && fieldsElement.TryGetProperty(storedAcField, out var acRaw))
                    {
                        issue.AcceptanceCriteria = ExtractFieldValue(acRaw);
                    }
                }
                catch { /* best-effort */ }

                // 2. If still null, try to resolve field names from metadata and match
                if (string.IsNullOrEmpty(issue.AcceptanceCriteria))
                {
                    var fieldNames = await GetFieldNamesAsync(credentials);
                    if (fieldNames != null)
                    {
                        foreach (var (fieldId, fieldName) in fieldNames)
                        {
                            if (!fieldId.StartsWith("customfield_")) continue;
                            var nameLower = fieldName.ToLowerInvariant();
                            // Match broader patterns including "checklist text"
                            if (nameLower.Contains("acceptance criteria")
                                || nameLower.Contains("acceptance criterion")
                                || nameLower.Contains("definition of done")
                                || nameLower.Contains("checklist text"))
                            {
                                if (fieldsElement.TryGetProperty(fieldId, out var acVal))
                                {
                                    var extracted = ExtractFieldValue(acVal);
                                    if (!string.IsNullOrEmpty(extracted))
                                    {
                                        issue.AcceptanceCriteria = extracted;
                                        // Persist for future use so search also picks it up
                                        try { await _configurationService.SetAsync(KeyAcFieldId, fieldId, global: true); }
                                        catch { /* best-effort */ }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return issue;
    }

    public async Task<List<JiraSavedFilter>> GetSavedFiltersAsync()
    {
        var json = await _configurationService.GetAsync(KeySavedFilters);
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<List<JiraSavedFilter>>(json) ?? new(); }
        catch { return new(); }
    }

    public async Task SaveFilterAsync(JiraSavedFilter filter)
    {
        var filters = await GetSavedFiltersAsync();
        // Replace if same label exists
        filters.RemoveAll(f => f.Label.Equals(filter.Label, StringComparison.OrdinalIgnoreCase));
        filters.Add(filter);
        await _configurationService.SetAsync(KeySavedFilters, JsonSerializer.Serialize(filters), global: true);
    }

    public async Task DeleteFilterAsync(string label)
    {
        var filters = await GetSavedFiltersAsync();
        filters.RemoveAll(f => f.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        await _configurationService.SetAsync(KeySavedFilters, JsonSerializer.Serialize(filters), global: true);
    }

    public async Task<List<JiraComment>> GetCommentsAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}/comment?orderBy=created");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var comments = new List<JiraComment>();
        if (doc.RootElement.TryGetProperty("comments", out var commentsArray))
        {
            foreach (var element in commentsArray.EnumerateArray())
            {
                var body = "";
                if (element.TryGetProperty("body", out var bodyProp))
                {
                    if (bodyProp.ValueKind == JsonValueKind.String)
                        body = bodyProp.GetString() ?? "";
                    else if (bodyProp.ValueKind == JsonValueKind.Object)
                        body = ExtractTextFromAdf(bodyProp) ?? "";
                }

                string? avatarUrl = null;
                if (element.TryGetProperty("author", out var authorObj) && authorObj.ValueKind == JsonValueKind.Object)
                {
                    if (authorObj.TryGetProperty("avatarUrls", out var avatarUrls) && avatarUrls.ValueKind == JsonValueKind.Object)
                    {
                        if (avatarUrls.TryGetProperty("48x48", out var url48))
                            avatarUrl = url48.GetString();
                    }
                }

                comments.Add(new JiraComment
                {
                    Id = element.GetProperty("id").GetString() ?? "",
                    Author = GetNestedDisplayName(element, "author") ?? "Unknown",
                    AuthorAvatarUrl = avatarUrl,
                    Body = body,
                    Created = DateTime.TryParse(element.GetProperty("created").GetString(), out var c) ? c : DateTime.MinValue,
                    Updated = element.TryGetProperty("updated", out var u) && DateTime.TryParse(u.GetString(), out var up) ? up : null
                });
            }
        }
        return comments;
    }

    public async Task<List<JiraWorklog>> GetWorklogsAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}/worklog");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var worklogs = new List<JiraWorklog>();
        if (doc.RootElement.TryGetProperty("worklogs", out var worklogsArray))
        {
            foreach (var element in worklogsArray.EnumerateArray())
            {
                string? comment = null;
                if (element.TryGetProperty("comment", out var commentProp))
                {
                    if (commentProp.ValueKind == JsonValueKind.String)
                        comment = commentProp.GetString();
                    else if (commentProp.ValueKind == JsonValueKind.Object)
                        comment = ExtractTextFromAdf(commentProp);
                }

                worklogs.Add(new JiraWorklog
                {
                    Id = element.GetProperty("id").GetString() ?? "",
                    Author = GetNestedDisplayName(element, "author") ?? "Unknown",
                    TimeSpentSeconds = element.TryGetProperty("timeSpentSeconds", out var tss) && tss.ValueKind == JsonValueKind.Number
                        ? tss.GetInt32() : 0,
                    TimeSpent = element.TryGetProperty("timeSpent", out var ts) && ts.ValueKind == JsonValueKind.String
                        ? ts.GetString() ?? "" : "",
                    Comment = comment,
                    Started = element.TryGetProperty("started", out var st) && DateTime.TryParse(st.GetString(), out var started)
                        ? started : DateTime.MinValue,
                    Created = element.TryGetProperty("created", out var cr) && DateTime.TryParse(cr.GetString(), out var created)
                        ? created : DateTime.MinValue
                });
            }
        }
        return worklogs;
    }

    public async Task<List<JiraAttachment>> GetAttachmentsAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}?fields=attachment");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var attachments = new List<JiraAttachment>();
        if (doc.RootElement.TryGetProperty("fields", out var fields)
            && fields.TryGetProperty("attachment", out var attachmentArray)
            && attachmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in attachmentArray.EnumerateArray())
            {
                attachments.Add(new JiraAttachment
                {
                    Id = element.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Filename = element.TryGetProperty("filename", out var fn) ? fn.GetString() ?? "" : "",
                    Author = GetNestedDisplayName(element, "author") ?? "Unknown",
                    Size = element.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number
                        ? sz.GetInt64() : 0,
                    MimeType = element.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "",
                    ContentUrl = element.TryGetProperty("content", out var cu) ? cu.GetString() ?? "" : "",
                    Created = element.TryGetProperty("created", out var cr) && DateTime.TryParse(cr.GetString(), out var created)
                        ? created : DateTime.MinValue
                });
            }
        }
        return attachments;
    }

    public async Task<List<JiraChangelogEntry>> GetChangelogAsync(string issueKey)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var apiBaseUrl = GetApiBaseUrl(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBaseUrl}/issue/{Uri.EscapeDataString(issueKey)}?expand=changelog&fields=none");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var entries = new List<JiraChangelogEntry>();
        if (doc.RootElement.TryGetProperty("changelog", out var changelog)
            && changelog.TryGetProperty("histories", out var histories)
            && histories.ValueKind == JsonValueKind.Array)
        {
            foreach (var history in histories.EnumerateArray())
            {
                var entry = new JiraChangelogEntry
                {
                    Id = history.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Author = GetNestedDisplayName(history, "author") ?? "Unknown",
                    Created = history.TryGetProperty("created", out var cr) && DateTime.TryParse(cr.GetString(), out var created)
                        ? created : DateTime.MinValue
                };

                if (history.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        entry.Items.Add(new JiraChangelogItem
                        {
                            Field = item.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "",
                            FromString = item.TryGetProperty("fromString", out var fs) ? fs.GetString() : null,
                            ToStringValue = item.TryGetProperty("toString", out var ts) ? ts.GetString() : null
                        });
                    }
                }

                entries.Add(entry);
            }
        }
        return entries;
    }

    public async Task<byte[]> DownloadAttachmentAsync(string contentUrl)
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var request = new HttpRequestMessage(HttpMethod.Get, contentUrl);
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync();
    }

    // ─────────────────────────────────────────────
    //  Private helpers
    // ─────────────────────────────────────────────

    /// <summary>
    /// Sends an HTTP request. When DebugWriter is set, logs request/response details
    /// including method, URL, auth header type, status code, and response body (truncated).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithDebugAsync(HttpRequestMessage request)
    {
        if (DebugWriter != null)
        {
            DebugWriter($"[dim]──── HTTP Request ────[/]");
            DebugWriter($"[cyan]{request.Method}[/] {Spectre.Console.Markup.Escape(request.RequestUri?.ToString() ?? "(null)")}");
            if (request.Headers.Authorization != null)
                DebugWriter($"[dim]Authorization:[/] {request.Headers.Authorization.Scheme} {(request.Headers.Authorization.Scheme == "Basic" ? "<redacted>" : request.Headers.Authorization.Parameter?[..Math.Min(20, request.Headers.Authorization.Parameter?.Length ?? 0)] + "...")}");
            if (request.Content != null)
            {
                var body = await request.Content.ReadAsStringAsync();
                DebugWriter($"[dim]Body:[/] {Spectre.Console.Markup.Escape(body.Length > 500 ? body[..500] + "..." : body)}");
            }
        }

        var response = await _httpClient.SendAsync(request);

        if (DebugWriter != null)
        {
            var statusColor = response.IsSuccessStatusCode ? "green" : "red";
            DebugWriter($"[dim]──── HTTP Response ────[/]");
            DebugWriter($"[{statusColor}]{(int)response.StatusCode} {response.StatusCode}[/]");
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(responseBody))
            {
                // Pretty-print JSON if possible
                try
                {
                    var doc = JsonDocument.Parse(responseBody);
                    var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
                    var display = pretty.Length > 2000 ? pretty[..2000] + "\n..." : pretty;
                    DebugWriter($"[dim]{Spectre.Console.Markup.Escape(display)}[/]");
                }
                catch
                {
                    var display = responseBody.Length > 2000 ? responseBody[..2000] + "..." : responseBody;
                    DebugWriter($"[dim]{Spectre.Console.Markup.Escape(display)}[/]");
                }
            }
            DebugWriter("");

            // Re-create content since we consumed it (response body can only be read once)
            response.Content = new StringContent(responseBody, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return response;
    }

    /// <summary>
    /// Returns the REST API version path: /rest/api/3 for Cloud, /rest/api/2 for Server.
    /// </summary>
    private static string GetApiVersion(JiraStoredCredentials credentials)
    {
        return credentials.DeploymentType == JiraDeploymentType.Server
            ? "/rest/api/2"
            : "/rest/api/3";
    }

    /// <summary>
    /// Returns the full API base URL for the given credentials.
    /// For Cloud with a cloudId (scoped tokens), uses the Atlassian gateway:
    ///   https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3
    /// Otherwise uses the direct site URL:
    ///   https://site.atlassian.net/rest/api/3
    /// </summary>
    private static string GetApiBaseUrl(JiraStoredCredentials credentials)
    {
        var baseUrl = credentials.BaseUrl.Trim().TrimEnd('/');
        var apiVersion = GetApiVersion(credentials);

        if (credentials.DeploymentType == JiraDeploymentType.Cloud
            && !string.IsNullOrEmpty(credentials.CloudId))
        {
            return $"https://api.atlassian.com/ex/jira/{credentials.CloudId}{apiVersion}";
        }

        return $"{baseUrl}{apiVersion}";
    }

    private static void ApplyAuth(HttpRequestMessage request, JiraStoredCredentials credentials)
    {
        var email = credentials.Email?.Trim() ?? string.Empty;
        var username = credentials.Username?.Trim() ?? string.Empty;
        var apiToken = credentials.ApiToken?.Trim() ?? string.Empty;
        var accessToken = credentials.AccessToken?.Trim() ?? string.Empty;

        if (credentials.AuthMethod == JiraAuthMethod.OAuth && !string.IsNullOrEmpty(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        else if (credentials.DeploymentType == JiraDeploymentType.Server)
        {
            // Server/DC: if username is set, use Basic auth (username:password/token)
            // otherwise, treat the ApiToken as a Personal Access Token (Bearer)
            if (!string.IsNullOrEmpty(username))
            {
                var encoded = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }
            else
            {
                // PAT — used as Bearer token on Server/DC
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            }
        }
        else
        {
            // Cloud: Basic auth with email:apiToken
            var encoded = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{email}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static JiraStoredCredentials NormalizeCredentials(JiraStoredCredentials credentials)
    {
        return new JiraStoredCredentials
        {
            AuthMethod = credentials.AuthMethod,
            DeploymentType = credentials.DeploymentType,
            BaseUrl = credentials.BaseUrl?.Trim() ?? string.Empty,
            Email = credentials.Email?.Trim() ?? string.Empty,
            Username = credentials.Username?.Trim() ?? string.Empty,
            ApiToken = credentials.ApiToken?.Trim() ?? string.Empty,
            AccessToken = credentials.AccessToken?.Trim() ?? string.Empty,
            RefreshToken = credentials.RefreshToken?.Trim() ?? string.Empty,
            CloudId = credentials.CloudId?.Trim() ?? string.Empty,
            CreatedAt = credentials.CreatedAt,
            LastRefreshedAt = credentials.LastRefreshedAt
        };
    }

    /// <summary>
    /// Discovers the custom field ID for "Acceptance Criteria" by querying
    /// the Jira /field endpoint. Caches the result for the lifetime of this service.
    /// </summary>
    /// <summary>
    /// Well-known field name patterns for acceptance criteria across Jira instances.
    /// Checked case-insensitively. Order matters — first match wins.
    /// </summary>
    private static readonly string[] AcceptanceCriteriaPatterns =
    {
        "acceptance criteria",
        "acceptance criterion",
        "acceptance_criteria",
        "ac criteria",
        "definition of done",
    };

    private async Task<string?> DiscoverAcceptanceCriteriaFieldAsync(JiraStoredCredentials credentials)
    {
        if (_acFieldDiscovered) return _acceptanceCriteriaFieldId;
        _acFieldDiscovered = true;

        // 1. Check for a manually configured or previously persisted field ID
        try
        {
            var stored = await _configurationService.GetAsync(KeyAcFieldId);
            if (!string.IsNullOrEmpty(stored))
            {
                _acceptanceCriteriaFieldId = stored;
                DebugWriter?.Invoke($"[green]Acceptance criteria field (stored): {stored}[/]");
                return stored;
            }
        }
        catch { /* continue with discovery */ }

        // 2. Auto-discover from field metadata
        try
        {
            var apiBaseUrl = GetApiBaseUrl(credentials);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}/field");
            ApplyAuth(request, credentials);
            var response = await SendWithDebugAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                DebugWriter?.Invoke($"[yellow]Field discovery failed: {(int)response.StatusCode}[/]");
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var fields = JsonDocument.Parse(content).RootElement;

            // Collect candidates: custom fields whose name matches any known pattern
            var candidates = new List<(string id, string name)>();

            foreach (var field in fields.EnumerateArray())
            {
                var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                var id = field.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (name == null || id == null) continue;

                // Only consider custom fields
                if (!id.StartsWith("customfield_")) continue;

                var nameLower = name.ToLowerInvariant();
                foreach (var pattern in AcceptanceCriteriaPatterns)
                {
                    if (nameLower.Contains(pattern))
                    {
                        candidates.Add((id, name));
                        break;
                    }
                }
            }

            if (candidates.Count > 0)
            {
                _acceptanceCriteriaFieldId = candidates[0].id;
                DebugWriter?.Invoke($"[green]Acceptance criteria field: {candidates[0].name} ({candidates[0].id})[/]");
                if (candidates.Count > 1)
                    DebugWriter?.Invoke($"[dim]Other candidates: {string.Join(", ", candidates.Skip(1).Select(c => $"{c.name} ({c.id})"))}[/]");

                // Persist so it survives across sessions
                try { await _configurationService.SetAsync(KeyAcFieldId, _acceptanceCriteriaFieldId, global: true); }
                catch { /* best-effort persist */ }

                return _acceptanceCriteriaFieldId;
            }

            DebugWriter?.Invoke("[yellow]No acceptance criteria custom field found. Use 'pks jira config --ac-field customfield_NNNNN' to set manually.[/]");
        }
        catch (Exception ex)
        {
            DebugWriter?.Invoke($"[yellow]Field discovery error: {Spectre.Console.Markup.Escape(ex.Message)}[/]");
        }

        return null;
    }

    /// <summary>
    /// Returns a cached mapping of field ID → field name from the Jira /field endpoint.
    /// </summary>
    private async Task<Dictionary<string, string>?> GetFieldNamesAsync(JiraStoredCredentials credentials)
    {
        if (_fieldNamesCache != null) return _fieldNamesCache;
        try
        {
            var apiBaseUrl = GetApiBaseUrl(credentials);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBaseUrl}/field");
            ApplyAuth(request, credentials);
            var response = await SendWithDebugAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var content = await response.Content.ReadAsStringAsync();
            var fields = JsonDocument.Parse(content).RootElement;
            _fieldNamesCache = new Dictionary<string, string>();
            foreach (var field in fields.EnumerateArray())
            {
                var id = field.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var name = field.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (id != null && name != null)
                    _fieldNamesCache[id] = name;
            }
            return _fieldNamesCache;
        }
        catch { return null; }
    }

    /// <summary>
    /// Sets the acceptance criteria custom field ID manually. Use when auto-discovery
    /// doesn't find the right field (e.g. field has a non-standard name).
    /// </summary>
    public async Task SetAcceptanceCriteriaFieldAsync(string fieldId)
    {
        await _configurationService.SetAsync(KeyAcFieldId, fieldId, global: true);
        _acceptanceCriteriaFieldId = fieldId;
        _acFieldDiscovered = true;
    }

    /// <summary>
    /// Gets the currently configured acceptance criteria field ID (stored or discovered).
    /// Returns null if not yet configured.
    /// </summary>
    public async Task<string?> GetAcceptanceCriteriaFieldAsync()
    {
        return await _configurationService.GetAsync(KeyAcFieldId);
    }

    private static List<JiraIssue> ParseIssues(JsonElement root, string? acFieldId = null)
    {
        var issues = new List<JiraIssue>();
        if (root.TryGetProperty("issues", out var issuesArray))
        {
            foreach (var element in issuesArray.EnumerateArray())
            {
                issues.Add(ParseIssue(element, acFieldId));
            }
        }
        return issues;
    }

    private static JiraIssue ParseIssue(JsonElement element, string? acFieldId = null)
    {
        var fields = element.GetProperty("fields");

        var issue = new JiraIssue
        {
            Id = element.GetProperty("id").GetString() ?? string.Empty,
            Key = element.GetProperty("key").GetString() ?? string.Empty,
            Summary = GetStringProperty(fields, "summary"),
            Status = GetNestedName(fields, "status"),
            IssueType = GetNestedName(fields, "issuetype"),
            Priority = GetNestedName(fields, "priority"),
            Assignee = GetNestedDisplayName(fields, "assignee"),
            ProjectKey = GetNestedKey(fields, "project")
        };

        if (fields.TryGetProperty("parent", out var parent) && parent.ValueKind != JsonValueKind.Null)
        {
            issue.ParentKey = parent.TryGetProperty("key", out var pk) ? pk.GetString() : null;

            if (parent.TryGetProperty("fields", out var parentFields))
            {
                issue.ParentSummary = GetStringProperty(parentFields, "summary");
            }
        }

        // Description (can be ADF in v3, plain text in v2)
        if (fields.TryGetProperty("description", out var desc))
        {
            if (desc.ValueKind == JsonValueKind.String)
                issue.Description = desc.GetString();
            else if (desc.ValueKind == JsonValueKind.Object)
            {
                // Atlassian Document Format (ADF) — extract text content
                issue.Description = ExtractTextFromAdf(desc);
            }
        }

        // Labels
        if (fields.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array)
        {
            issue.Labels = labels.EnumerateArray()
                .Where(l => l.ValueKind == JsonValueKind.String)
                .Select(l => l.GetString() ?? "")
                .Where(l => !string.IsNullOrEmpty(l))
                .ToList();
        }

        // Components
        if (fields.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            issue.Components = components.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
        }

        // Time tracking
        if (fields.TryGetProperty("timeoriginalestimate", out var origEst) && origEst.ValueKind == JsonValueKind.Number)
            issue.OriginalEstimateSeconds = origEst.GetInt32();
        if (fields.TryGetProperty("timespent", out var spent) && spent.ValueKind == JsonValueKind.Number)
            issue.TimeSpentSeconds = spent.GetInt32();

        // Format time strings
        issue.OriginalEstimate = FormatSeconds(issue.OriginalEstimateSeconds);
        issue.TimeSpent = FormatSeconds(issue.TimeSpentSeconds);

        // Story points (try customfield_10016 first, then story_points)
        if (fields.TryGetProperty("customfield_10016", out var sp10016) && sp10016.ValueKind == JsonValueKind.Number)
            issue.StoryPoints = sp10016.GetDouble();
        else if (fields.TryGetProperty("story_points", out var spField) && spField.ValueKind == JsonValueKind.Number)
            issue.StoryPoints = spField.GetDouble();

        // Reporter
        issue.Reporter = GetNestedDisplayName(fields, "reporter");

        // Resolution
        issue.Resolution = GetNestedName(fields, "resolution");

        // Dates
        if (fields.TryGetProperty("created", out var createdProp) && createdProp.ValueKind == JsonValueKind.String)
            issue.Created = DateTime.TryParse(createdProp.GetString(), out var c) ? c : null;
        if (fields.TryGetProperty("updated", out var updatedProp) && updatedProp.ValueKind == JsonValueKind.String)
            issue.Updated = DateTime.TryParse(updatedProp.GetString(), out var u) ? u : null;

        // Acceptance Criteria (custom field — ID discovered at runtime)
        if (acFieldId != null && fields.TryGetProperty(acFieldId, out var acProp))
        {
            if (acProp.ValueKind == JsonValueKind.String)
                issue.AcceptanceCriteria = acProp.GetString();
            else if (acProp.ValueKind == JsonValueKind.Object)
                issue.AcceptanceCriteria = ExtractTextFromAdf(acProp);
            else if (acProp.ValueKind == JsonValueKind.Array)
            {
                // Some plugins store AC as an array of ADF blocks or strings
                var parts = new List<string>();
                foreach (var item in acProp.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        parts.Add(item.GetString() ?? "");
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        var text = ExtractTextFromAdf(item);
                        if (text != null) parts.Add(text);
                    }
                }
                if (parts.Count > 0)
                    issue.AcceptanceCriteria = string.Join("\n", parts);
            }
        }

        // Issue links
        if (fields.TryGetProperty("issuelinks", out var linksArray) && linksArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in linksArray.EnumerateArray())
            {
                var linkId = link.TryGetProperty("id", out var lid) ? lid.GetString() ?? "" : "";
                var linkTypeName = "";
                var inwardLabel = "";
                var outwardLabel = "";
                if (link.TryGetProperty("type", out var linkType) && linkType.ValueKind == JsonValueKind.Object)
                {
                    linkTypeName = linkType.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                    inwardLabel = linkType.TryGetProperty("inward", out var iw) ? iw.GetString() ?? "" : "";
                    outwardLabel = linkType.TryGetProperty("outward", out var ow) ? ow.GetString() ?? "" : "";
                }

                if (link.TryGetProperty("outwardIssue", out var outward) && outward.ValueKind == JsonValueKind.Object)
                {
                    var issueLink = new JiraIssueLink
                    {
                        Id = linkId,
                        LinkType = linkTypeName,
                        Direction = "outward",
                        DirectionLabel = outwardLabel,
                        LinkedIssueKey = outward.TryGetProperty("key", out var k) ? k.GetString() ?? "" : ""
                    };
                    if (outward.TryGetProperty("fields", out var lf) && lf.ValueKind == JsonValueKind.Object)
                    {
                        issueLink.LinkedIssueSummary = GetStringProperty(lf, "summary");
                        issueLink.LinkedIssueStatus = GetNestedName(lf, "status");
                        issueLink.LinkedIssueType = GetNestedName(lf, "issuetype");
                    }
                    issue.IssueLinks.Add(issueLink);
                }

                if (link.TryGetProperty("inwardIssue", out var inward) && inward.ValueKind == JsonValueKind.Object)
                {
                    var issueLink = new JiraIssueLink
                    {
                        Id = linkId,
                        LinkType = linkTypeName,
                        Direction = "inward",
                        DirectionLabel = inwardLabel,
                        LinkedIssueKey = inward.TryGetProperty("key", out var k) ? k.GetString() ?? "" : ""
                    };
                    if (inward.TryGetProperty("fields", out var lf) && lf.ValueKind == JsonValueKind.Object)
                    {
                        issueLink.LinkedIssueSummary = GetStringProperty(lf, "summary");
                        issueLink.LinkedIssueStatus = GetNestedName(lf, "status");
                        issueLink.LinkedIssueType = GetNestedName(lf, "issuetype");
                    }
                    issue.IssueLinks.Add(issueLink);
                }
            }
        }

        return issue;
    }

    private static string GetStringProperty(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string GetNestedName(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string? GetNestedDisplayName(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
        }
        return null;
    }

    private static string GetNestedKey(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String)
                return key.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string? FormatSeconds(int? seconds)
    {
        if (seconds == null || seconds == 0) return null;
        var s = seconds.Value;
        var days = s / 28800; // 8h work day
        var hours = (s % 28800) / 3600;
        var minutes = (s % 3600) / 60;

        var parts = new List<string>();
        if (days > 0) parts.Add($"{days}d");
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0) parts.Add($"{minutes}m");
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    /// <summary>
    /// Extracts plain text from Atlassian Document Format (ADF) JSON.
    /// ADF is used in Jira Cloud API v3 for rich text fields.
    /// </summary>
    /// <summary>
    /// Extracts a text value from a Jira field that may be a string, ADF object, or array.
    /// </summary>
    private static string? ExtractFieldValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();
        if (element.ValueKind == JsonValueKind.Object)
            return ExtractTextFromAdf(element);
        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    parts.Add(item.GetString() ?? "");
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var text = ExtractTextFromAdf(item);
                    if (text != null) parts.Add(text);
                }
            }
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
        return element.ToString();
    }

    private static string? ExtractTextFromAdf(JsonElement adf)
    {
        try
        {
            var sb = new StringBuilder();
            ExtractAdfText(adf, sb);
            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        catch { return null; }
    }

    private static void ExtractAdfText(JsonElement element, StringBuilder sb)
    {
        if (element.TryGetProperty("type", out var type) && type.GetString() == "text"
            && element.TryGetProperty("text", out var text))
        {
            sb.Append(text.GetString());
        }

        if (element.TryGetProperty("type", out var nodeType))
        {
            var t = nodeType.GetString();
            if (t == "paragraph" || t == "heading" || t == "bulletList" || t == "orderedList")
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
            }
            if (t == "listItem")
            {
                sb.Append("- ");
            }
        }

        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in content.EnumerateArray())
            {
                ExtractAdfText(child, sb);
            }
        }
    }
}
