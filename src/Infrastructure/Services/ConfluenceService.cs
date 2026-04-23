using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Confluence REST API client. Reuses Jira stored credentials (same Atlassian email + API token).
/// </summary>
public class ConfluenceService : IConfluenceService
{
    private readonly HttpClient _httpClient;
    private readonly IJiraService _jiraService;
    private readonly ILogger<ConfluenceService> _logger;

    /// <summary>When set, HTTP request/response details are written via this callback.</summary>
    public Action<string>? DebugWriter { get; set; }

    public ConfluenceService(HttpClient httpClient, IJiraService jiraService, ILogger<ConfluenceService> logger)
    {
        _httpClient = httpClient;
        _jiraService = jiraService;
        _logger = logger;
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        return await _jiraService.IsAuthenticatedAsync();
    }

    public async Task<List<ConfluenceSpaceInfo>> GetSpacesAsync()
    {
        var credentials = await GetCredentialsOrThrow();
        var spaceApiBase = GetConfluenceWikiBase(credentials) + "/space";

        var spaces = new List<ConfluenceSpaceInfo>();
        var start = 0;
        const int limit = 100;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{spaceApiBase}?limit={limit}&start={start}&expand=homepage");
            ApplyAuth(request, credentials);

            var response = await SendWithDebugAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            var results = doc.RootElement.GetProperty("results");

            foreach (var el in results.EnumerateArray())
            {
                var space = new ConfluenceSpaceInfo
                {
                    Key = el.GetProperty("key").GetString() ?? string.Empty,
                    Name = el.GetProperty("name").GetString() ?? string.Empty,
                    Type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                };

                if (el.TryGetProperty("homepage", out var hp) && hp.ValueKind == JsonValueKind.Object)
                    space.HomepageId = hp.GetProperty("id").GetString();

                spaces.Add(space);
            }

            if (results.GetArrayLength() < limit)
                break;

            start += limit;
        }

        return spaces;
    }

    public async Task<ConfluencePage?> GetPageByIdAsync(string pageId, bool expandBody = true)
    {
        var credentials = await GetCredentialsOrThrow();
        var expand = expandBody
            ? "version,body.storage,ancestors,space"
            : "version,ancestors,space";

        var apiBase = GetConfluenceApiBase(credentials);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBase}/{pageId}?expand={expand}");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;
            response.EnsureSuccessStatusCode();
        }

        var content = await response.Content.ReadAsStringAsync();
        return ParsePage(JsonDocument.Parse(content).RootElement);
    }

    public async Task<ConfluencePage?> FindPageByTitleAsync(string spaceKey, string title)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);

        var encodedTitle = Uri.EscapeDataString(title);
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBase}?spaceKey={spaceKey}&title={encodedTitle}&expand=version,body.storage,ancestors,space");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(content);
        var results = doc.RootElement.GetProperty("results");

        if (results.GetArrayLength() == 0)
            return null;

        return ParsePage(results[0]);
    }

    public async Task<List<ConfluencePage>> GetChildPagesAsync(string parentId)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);
        var pages = new List<ConfluencePage>();
        var start = 0;
        const int limit = 200;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{apiBase}/{parentId}/child/page?expand=version,space&start={start}&limit={limit}");
            ApplyAuth(request, credentials);

            var response = await SendWithDebugAsync(request);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            var results = doc.RootElement.GetProperty("results");

            foreach (var el in results.EnumerateArray())
                pages.Add(ParsePage(el));

            if (results.GetArrayLength() < limit)
                break;

            start += limit;
        }

        return pages;
    }

    public async Task<List<ConfluencePage>> GetPageTreeAsync(string rootPageId, int maxDepth = 10)
    {
        var allPages = new List<ConfluencePage>();
        var queue = new Queue<(string parentId, int depth)>();
        queue.Enqueue((rootPageId, 0));

        while (queue.Count > 0)
        {
            var (parentId, depth) = queue.Dequeue();
            if (depth >= maxDepth)
                continue;

            var children = await GetChildPagesAsync(parentId);
            allPages.AddRange(children);

            foreach (var child in children)
                queue.Enqueue((child.Id, depth + 1));
        }

        return allPages;
    }

    public async Task<ConfluencePage> UpdatePageAsync(string pageId, string title, string storageFormatHtml, int currentVersion)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);

        var payload = new
        {
            type = "page",
            title,
            version = new { number = currentVersion + 1, message = $"Updated via pks-cli" },
            body = new
            {
                storage = new
                {
                    value = storageFormatHtml,
                    representation = "storage"
                }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Put, $"{apiBase}/{pageId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return ParsePage(JsonDocument.Parse(content).RootElement);
    }

    public async Task<ConfluencePage> CreatePageAsync(string spaceKey, string title, string storageFormatHtml, string? parentId = null)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);

        var payloadObj = new Dictionary<string, object>
        {
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new { key = spaceKey },
            ["body"] = new
            {
                storage = new
                {
                    value = storageFormatHtml,
                    representation = "storage"
                }
            }
        };

        if (!string.IsNullOrEmpty(parentId))
            payloadObj["ancestors"] = new[] { new { id = parentId } };

        var json = JsonSerializer.Serialize(payloadObj);
        var request = new HttpRequestMessage(HttpMethod.Post, apiBase)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        return ParsePage(JsonDocument.Parse(content).RootElement);
    }

    public async Task<string> UploadAttachmentAsync(string pageId, string filePath, string? comment = null)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);

        var fileName = Path.GetFileName(filePath);
        var fileBytes = await File.ReadAllBytesAsync(filePath);

        // Upsert semantics: POST /child/attachment fails 400 when a file with the
        // same name already exists on the page (including empty placeholders left
        // by a previous page update that referenced a <ac:image> before the data
        // was uploaded). If one exists, target its /data endpoint to upload a new
        // version instead.
        var existingAttachmentId = await FindAttachmentIdByFilenameAsync(apiBase, credentials, pageId, fileName);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            fileName.EndsWith(".png") ? "image/png" :
            fileName.EndsWith(".svg") ? "image/svg+xml" : "application/octet-stream");
        content.Add(fileContent, "file", fileName);

        if (!string.IsNullOrEmpty(comment))
            content.Add(new StringContent(comment), "comment");

        var url = existingAttachmentId == null
            ? $"{apiBase}/{pageId}/child/attachment"
            : $"{apiBase}/{pageId}/child/attachment/{existingAttachmentId}/data";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content
        };
        ApplyAuth(request, credentials);
        request.Headers.Add("X-Atlassian-Token", "nocheck");

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.StatusCode}: {body}",
                null, response.StatusCode);
        }

        var responseBody = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(responseBody);

        // Create returns { results: [...] }, update-data returns the attachment object directly.
        if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            return results[0].GetProperty("title").GetString() ?? fileName;
        if (doc.RootElement.TryGetProperty("title", out var titleProp))
            return titleProp.GetString() ?? fileName;

        return fileName;
    }

    private async Task<string?> FindAttachmentIdByFilenameAsync(string apiBase, JiraStoredCredentials credentials, string pageId, string fileName)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{apiBase}/{pageId}/child/attachment?filename={Uri.EscapeDataString(fileName)}&limit=1");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        return results[0].GetProperty("id").GetString();
    }

    public async Task<List<ConfluenceComment>> GetPageCommentsAsync(string pageId)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);
        const string expand = "body.storage,history.createdBy,extensions.location,extensions.resolution,extensions.inlineProperties,children.comment.body.storage,children.comment.history.createdBy,children.comment.extensions.location,children.comment.extensions.resolution,children.comment.extensions.inlineProperties";

        var comments = new List<ConfluenceComment>();
        var start = 0;
        const int limit = 100;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{apiBase}/{pageId}/child/comment?expand={expand}&start={start}&limit={limit}&depth=all");
            ApplyAuth(request, credentials);

            var response = await SendWithDebugAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return comments;
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(content);
            var results = doc.RootElement.GetProperty("results");

            foreach (var el in results.EnumerateArray())
            {
                var parsed = ParseComment(el);
                if (parsed != null)
                    comments.Add(parsed);
            }

            if (results.GetArrayLength() < limit)
                break;
            start += limit;
        }

        return comments;
    }

    public async Task<bool> DeletePageAsync(string pageId)
    {
        var credentials = await GetCredentialsOrThrow();
        var apiBase = GetConfluenceApiBase(credentials);

        var request = new HttpRequestMessage(HttpMethod.Delete, $"{apiBase}/{pageId}");
        ApplyAuth(request, credentials);

        var response = await SendWithDebugAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.StatusCode}: {body}",
                null, response.StatusCode);
        }
        return true;
    }

    public Task<ConfluenceWorkspaceConfig?> LoadWorkspaceConfigAsync(string startDir)
    {
        // Walk up from startDir to find .confluence/config.json
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var configPath = Path.Combine(dir.FullName, ".confluence", "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<ConfluenceWorkspaceConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (config != null)
                {
                    // Resolve WorkDir relative to where config was found
                    config.WorkDir = Path.GetFullPath(
                        Path.Combine(dir.FullName, config.WorkDir));
                    config.ConfigRoot = dir.FullName;
                }
                return Task.FromResult(config);
            }
            dir = dir.Parent;
        }
        return Task.FromResult<ConfluenceWorkspaceConfig?>(null);
    }

    public Task SaveWorkspaceConfigAsync(string configRoot, ConfluenceWorkspaceConfig config)
    {
        var dir = Path.Combine(configRoot, ".confluence");
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "config.json"), json);
        return Task.CompletedTask;
    }

    // ── Helpers ──

    private async Task<JiraStoredCredentials> GetCredentialsOrThrow()
    {
        var credentials = await _jiraService.GetStoredCredentialsAsync();
        if (credentials == null)
            throw new InvalidOperationException("Not authenticated. Run 'pks jira init' first.");
        return credentials;
    }

    /// <summary>
    /// Returns the Confluence wiki REST API base: {baseUrl}/wiki/rest/api
    /// For Cloud with cloudId, uses the Atlassian Confluence gateway.
    /// </summary>
    private static string GetConfluenceWikiBase(JiraStoredCredentials credentials)
    {
        var baseUrl = credentials.BaseUrl.Trim().TrimEnd('/');

        if (credentials.DeploymentType == JiraDeploymentType.Cloud
            && !string.IsNullOrEmpty(credentials.CloudId))
        {
            return $"https://api.atlassian.com/ex/confluence/{credentials.CloudId}/rest/api";
        }

        return $"{baseUrl}/wiki/rest/api";
    }

    /// <summary>
    /// Returns the Confluence REST API content endpoint.
    /// </summary>
    private static string GetConfluenceApiBase(JiraStoredCredentials credentials)
    {
        return GetConfluenceWikiBase(credentials) + "/content";
    }

    /// <summary>Applies auth headers, mirroring JiraService.ApplyAuth pattern.</summary>
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
            if (!string.IsNullOrEmpty(username))
            {
                var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{apiToken}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            }
        }
        else
        {
            var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{email}:{apiToken}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<HttpResponseMessage> SendWithDebugAsync(HttpRequestMessage request)
    {
        if (DebugWriter != null)
        {
            DebugWriter($"[dim]──── HTTP Request ────[/]");
            DebugWriter($"[cyan]{request.Method}[/] {Spectre.Console.Markup.Escape(request.RequestUri?.ToString() ?? "(null)")}");
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
            DebugWriter($"[{statusColor}]{(int)response.StatusCode} {response.StatusCode}[/]");
            var responseBody = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(responseBody))
            {
                // With --debug we dump the full body so inline-comment-markers and other
                // nested storage details are visible during round-trip diagnostics.
                DebugWriter($"[dim]{Spectre.Console.Markup.Escape(responseBody)}[/]");
            }
            // Re-create content since we consumed it
            response.Content = new StringContent(responseBody, Encoding.UTF8,
                response.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return response;
    }

    private static ConfluencePage ParsePage(JsonElement el)
    {
        var page = new ConfluencePage
        {
            Id = el.GetProperty("id").GetString() ?? string.Empty,
            Title = el.GetProperty("title").GetString() ?? string.Empty,
            Type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "page" : "page",
            Status = el.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty,
        };

        if (el.TryGetProperty("version", out var ver))
            page.Version = new ConfluenceVersion { Number = ver.GetProperty("number").GetInt32() };

        if (el.TryGetProperty("space", out var sp))
            page.Space = new ConfluenceSpace
            {
                Key = sp.GetProperty("key").GetString() ?? string.Empty,
                Name = sp.TryGetProperty("name", out var n) ? n.GetString() : null
            };

        if (el.TryGetProperty("body", out var body) && body.TryGetProperty("storage", out var storage))
            page.Body = new ConfluenceBody
            {
                Storage = new ConfluenceStorage
                {
                    Value = storage.GetProperty("value").GetString() ?? string.Empty,
                    Representation = storage.TryGetProperty("representation", out var r) ? r.GetString() ?? "storage" : "storage"
                }
            };

        if (el.TryGetProperty("ancestors", out var ancestors))
        {
            foreach (var a in ancestors.EnumerateArray())
            {
                page.Ancestors.Add(new ConfluenceAncestor
                {
                    Id = a.GetProperty("id").GetString() ?? string.Empty,
                    Title = a.TryGetProperty("title", out var at) ? at.GetString() ?? string.Empty : string.Empty
                });
            }
        }

        return page;
    }

    private static ConfluenceComment? ParseComment(JsonElement el)
    {
        if (!el.TryGetProperty("id", out var idEl))
            return null;

        var comment = new ConfluenceComment
        {
            Id = idEl.GetString() ?? string.Empty
        };

        if (el.TryGetProperty("body", out var body) && body.TryGetProperty("storage", out var storage) &&
            storage.TryGetProperty("value", out var storageVal))
        {
            comment.BodyStorageHtml = storageVal.GetString() ?? string.Empty;
        }

        if (el.TryGetProperty("history", out var history))
        {
            if (history.TryGetProperty("createdDate", out var createdEl) &&
                DateTime.TryParse(createdEl.GetString(), out var createdDt))
            {
                comment.Created = createdDt;
            }

            if (history.TryGetProperty("createdBy", out var createdBy))
            {
                if (createdBy.TryGetProperty("displayName", out var displayName))
                    comment.AuthorName = displayName.GetString() ?? string.Empty;
                if (createdBy.TryGetProperty("email", out var emailEl))
                    comment.AuthorEmail = emailEl.GetString();
            }

            if (history.TryGetProperty("lastUpdated", out var lastUpdated) &&
                lastUpdated.TryGetProperty("when", out var whenEl) &&
                DateTime.TryParse(whenEl.GetString(), out var updatedDt))
            {
                comment.Updated = updatedDt;
            }
        }

        if (el.TryGetProperty("extensions", out var ext))
        {
            if (ext.TryGetProperty("location", out var locEl))
                comment.Location = locEl.GetString() ?? "footer";

            if (ext.TryGetProperty("resolution", out var resEl) &&
                resEl.TryGetProperty("status", out var statusEl))
            {
                comment.ResolutionStatus = statusEl.GetString();
            }

            if (ext.TryGetProperty("inlineProperties", out var inlineProps) &&
                inlineProps.TryGetProperty("originalSelection", out var selEl))
            {
                comment.InlineSelection = selEl.GetString();
            }
        }

        if (el.TryGetProperty("children", out var children) &&
            children.TryGetProperty("comment", out var childComments) &&
            childComments.TryGetProperty("results", out var childResults))
        {
            foreach (var childEl in childResults.EnumerateArray())
            {
                var reply = ParseComment(childEl);
                if (reply != null)
                    comment.Replies.Add(reply);
            }
        }

        return comment;
    }
}
