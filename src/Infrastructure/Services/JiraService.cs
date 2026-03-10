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

    private readonly HttpClient _httpClient;
    private readonly IConfigurationService _configurationService;
    private readonly ILogger<JiraService> _logger;

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
        var token = await _configurationService.GetAsync(KeyApiToken);

        if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(token))
            return false;

        var deploymentTypeStr = await _configurationService.GetAsync(KeyDeploymentType);
        var isServer = Enum.TryParse<JiraDeploymentType>(deploymentTypeStr, out var dt)
            && dt == JiraDeploymentType.Server;

        if (isServer)
        {
            // Server PAT auth requires only baseUrl + token; basic auth needs username
            var username = await _configurationService.GetAsync(KeyUsername);
            var email = await _configurationService.GetAsync(KeyEmail);
            return !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(email);
        }

        // Cloud requires email
        var cloudEmail = await _configurationService.GetAsync(KeyEmail);
        return !string.IsNullOrEmpty(cloudEmail);
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
        await _configurationService.SetAsync(KeyAuthMethod, credentials.AuthMethod.ToString());
        await _configurationService.SetAsync(KeyDeploymentType, credentials.DeploymentType.ToString());
        await _configurationService.SetAsync(KeyBaseUrl, credentials.BaseUrl);
        await _configurationService.SetAsync(KeyEmail, credentials.Email);
        await _configurationService.SetAsync(KeyUsername, credentials.Username);
        await _configurationService.SetAsync(KeyApiToken, credentials.ApiToken, encrypt: true);
        await _configurationService.SetAsync(KeyAccessToken, credentials.AccessToken, encrypt: true);
        await _configurationService.SetAsync(KeyRefreshToken, credentials.RefreshToken, encrypt: true);
        await _configurationService.SetAsync(KeyCloudId, credentials.CloudId);
        await _configurationService.SetAsync(KeyCreatedAt, credentials.CreatedAt.ToString("O"));
        await _configurationService.SetAsync(KeyLastRefreshedAt, credentials.LastRefreshedAt.ToString("O"));
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
    }

    public async Task<bool> ValidateCredentialsAsync(JiraStoredCredentials credentials)
    {
        try
        {
            var baseUrl = credentials.BaseUrl.TrimEnd('/');
            var apiBase = GetApiBase(credentials);
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{apiBase}/myself");
            ApplyAuth(request, credentials);

            var response = await SendWithDebugAsync(request);
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
            var url = baseUrl.TrimEnd('/');
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

    public async Task<List<JiraProject>> GetProjectsAsync()
    {
        var credentials = await GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated with Jira");

        var baseUrl = credentials.BaseUrl.TrimEnd('/');
        var apiBase = GetApiBase(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{apiBase}/project");
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

        var baseUrl = credentials.BaseUrl.TrimEnd('/');
        var requestBody = JsonSerializer.Serialize(new
        {
            jql,
            startAt,
            maxResults,
            fields = new[] { "summary", "status", "issuetype", "priority", "assignee", "parent", "project" }
        });

        var apiBase = GetApiBase(credentials);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{apiBase}/search")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);

        var result = new JiraSearchResult
        {
            Total = doc.RootElement.GetProperty("total").GetInt32(),
            Issues = ParseIssues(doc.RootElement)
        };

        return result;
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

        var baseUrl = credentials.BaseUrl.TrimEnd('/');
        var apiBase = GetApiBase(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}{apiBase}/issue/{Uri.EscapeDataString(issueKey)}?fields=summary,status,issuetype,priority,assignee,parent,project");
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
        return ParseIssue(doc.RootElement);
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

        var response = await SendWithDebugAsync(request);

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
    /// Returns the REST API base path for the given deployment type.
    /// Cloud uses /rest/api/3, Server/Data Center uses /rest/api/2.
    /// </summary>
    private static string GetApiBase(JiraStoredCredentials credentials)
    {
        return credentials.DeploymentType == JiraDeploymentType.Server
            ? "/rest/api/2"
            : "/rest/api/3";
    }

    private static void ApplyAuth(HttpRequestMessage request, JiraStoredCredentials credentials)
    {
        if (credentials.AuthMethod == JiraAuthMethod.OAuth && !string.IsNullOrEmpty(credentials.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        }
        else if (credentials.DeploymentType == JiraDeploymentType.Server)
        {
            // Server/DC: if username is set, use Basic auth (username:password/token)
            // otherwise, treat the ApiToken as a Personal Access Token (Bearer)
            if (!string.IsNullOrEmpty(credentials.Username))
            {
                var encoded = Convert.ToBase64String(
                    Encoding.ASCII.GetBytes($"{credentials.Username}:{credentials.ApiToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }
            else
            {
                // PAT — used as Bearer token on Server/DC
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.ApiToken);
            }
        }
        else
        {
            // Cloud: Basic auth with email:apiToken
            var encoded = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{credentials.Email}:{credentials.ApiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static List<JiraIssue> ParseIssues(JsonElement root)
    {
        var issues = new List<JiraIssue>();
        if (root.TryGetProperty("issues", out var issuesArray))
        {
            foreach (var element in issuesArray.EnumerateArray())
            {
                issues.Add(ParseIssue(element));
            }
        }
        return issues;
    }

    private static JiraIssue ParseIssue(JsonElement element)
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
}
