using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PKS.Infrastructure.Services.Runner;

public interface ICoolifyApiService
{
    Task<CoolifyConnectionResult> TestConnectionAsync(CoolifyInstance instance);
    Task<List<CoolifyProject>> GetProjectsWithResourcesAsync(CoolifyInstance instance);
    Task<string> GetRawProjectsJsonAsync(CoolifyInstance instance, string? projectUuid = null);
}

public class CoolifyConnectionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
}

public class CoolifyProject
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Uuid { get; set; } = "";
    public List<CoolifyResource> Resources { get; set; } = new();
}

public class CoolifyResource
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string? Fqdn { get; set; }
}

public class CoolifyApiService : ICoolifyApiService
{
    private readonly ILogger<CoolifyApiService> _logger;

    public CoolifyApiService(ILogger<CoolifyApiService> logger)
    {
        _logger = logger;
    }

    public async Task<CoolifyConnectionResult> TestConnectionAsync(CoolifyInstance instance)
    {
        try
        {
            using var http = CreateHttpClient(instance);
            var response = await http.GetAsync($"{instance.Url}/api/v1/version");
            response.EnsureSuccessStatusCode();

            var version = await response.Content.ReadAsStringAsync();
            // The version endpoint may return a plain string or JSON string
            version = version.Trim().Trim('"');

            _logger.LogInformation("Connected to Coolify {Url}, version {Version}", instance.Url, version);

            return new CoolifyConnectionResult
            {
                Success = true,
                Version = version
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to Coolify instance {Url}", instance.Url);

            return new CoolifyConnectionResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public async Task<List<CoolifyProject>> GetProjectsWithResourcesAsync(CoolifyInstance instance)
    {
        using var http = CreateHttpClient(instance);

        // Step 1: Get project list
        var response = await http.GetAsync($"{instance.Url}/api/v1/projects");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        _logger.LogDebug("Projects list response: {Json}", json);
        var doc = JsonDocument.Parse(json);

        var projectElements = GetArrayFromResponse(doc);
        var projects = new List<CoolifyProject>();

        foreach (var projectEl in projectElements)
        {
            var uuid = projectEl.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() ?? "" : "";
            var project = new CoolifyProject
            {
                Id = projectEl.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0,
                Name = projectEl.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                Description = projectEl.TryGetProperty("description", out var descProp) ? descProp.GetString() : null,
                Uuid = uuid
            };

            // Log property names on project element for debugging
            if (projectEl.ValueKind == JsonValueKind.Object)
            {
                var props = string.Join(", ", projectEl.EnumerateObject().Select(p => p.Name));
                _logger.LogDebug("Project '{Name}' properties: {Props}", project.Name, props);
            }

            // Try extracting resources from the list response first
            ExtractAllResources(projectEl, project.Resources);

            // If no resources found, fetch project detail then each environment's resources
            if (project.Resources.Count == 0 && !string.IsNullOrEmpty(uuid))
            {
                try
                {
                    // Get project detail to discover environments
                    var detailResponse = await http.GetAsync($"{instance.Url}/api/v1/projects/{uuid}");
                    if (detailResponse.IsSuccessStatusCode)
                    {
                        var detailJson = await detailResponse.Content.ReadAsStringAsync();
                        var detailDoc = JsonDocument.Parse(detailJson);

                        // First try extracting directly from detail
                        ExtractAllResources(detailDoc.RootElement, project.Resources);

                        // If still empty, fetch each environment separately
                        // Coolify v4: GET /api/v1/projects/{uuid}/{env_name}
                        if (project.Resources.Count == 0 &&
                            detailDoc.RootElement.TryGetProperty("environments", out var envs) &&
                            envs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var env in envs.EnumerateArray())
                            {
                                var envName = env.TryGetProperty("name", out var envNameProp)
                                    ? envNameProp.GetString() ?? "" : "";
                                if (string.IsNullOrEmpty(envName)) continue;

                                try
                                {
                                    var envResponse = await http.GetAsync(
                                        $"{instance.Url}/api/v1/projects/{uuid}/{envName}");
                                    if (envResponse.IsSuccessStatusCode)
                                    {
                                        var envJson = await envResponse.Content.ReadAsStringAsync();
                                        var envDoc = JsonDocument.Parse(envJson);
                                        ExtractAllResources(envDoc.RootElement, project.Resources);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(ex, "Failed to fetch environment {Env} for project {Uuid}", envName, uuid);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch detail for project {Uuid}", uuid);
                }
            }

            projects.Add(project);
        }

        _logger.LogInformation(
            "Fetched {ProjectCount} projects with {ResourceCount} total resources from {Url}",
            projects.Count, projects.Sum(p => p.Resources.Count), instance.Url);

        return projects;
    }

    public async Task<string> GetRawProjectsJsonAsync(CoolifyInstance instance, string? path = null)
    {
        using var http = CreateHttpClient(instance);
        var url = string.IsNullOrEmpty(path)
            ? $"{instance.Url}/api/v1/projects"
            : $"{instance.Url}/api/v1/projects/{path}";
        var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void ExtractAllResources(JsonElement element, List<CoolifyResource> resources)
    {
        // Direct resource arrays on the element
        ExtractResources(element, "applications", "application", resources);
        ExtractResources(element, "services", "service", resources);
        ExtractResources(element, "databases", "database", resources);

        // Resources nested under environments
        if (element.TryGetProperty("environments", out var envsProp) && envsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var env in envsProp.EnumerateArray())
            {
                ExtractResources(env, "applications", "application", resources);
                ExtractResources(env, "services", "service", resources);
                ExtractResources(env, "databases", "database", resources);
            }
        }
    }

    private static void ExtractResources(JsonElement parent, string arrayProperty, string resourceType, List<CoolifyResource> resources)
    {
        if (!parent.TryGetProperty(arrayProperty, out var arrayProp) || arrayProp.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in arrayProp.EnumerateArray())
        {
            var resource = new CoolifyResource
            {
                Uuid = item.TryGetProperty("uuid", out var uuidProp) ? uuidProp.GetString() ?? "" : "",
                Name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "",
                Type = resourceType,
                Status = item.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "unknown" : "unknown",
                Fqdn = item.TryGetProperty("fqdn", out var fqdnProp) ? fqdnProp.GetString() : null
            };

            resources.Add(resource);
        }
    }

    private static List<JsonElement> GetArrayFromResponse(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            return doc.RootElement.EnumerateArray().ToList();
        }

        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToList();
        }

        return new List<JsonElement>();
    }

    private static HttpClient CreateHttpClient(CoolifyInstance instance)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", instance.Token);
        return http;
    }
}
