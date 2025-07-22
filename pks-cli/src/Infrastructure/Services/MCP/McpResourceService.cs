using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Attribute to mark methods as MCP resource providers
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class McpServerResourceAttribute : Attribute
{
    public string Uri { get; }
    public string Name { get; }
    public string Description { get; }
    public string MimeType { get; }

    public McpServerResourceAttribute(string uri, string name, string description, string mimeType = "text/plain")
    {
        Uri = uri;
        Name = name;
        Description = description;
        MimeType = mimeType;
    }
}

/// <summary>
/// Service for managing MCP resources using attribute-based registration
/// </summary>
public class McpResourceService
{
    private readonly ILogger<McpResourceService> _logger;
    private readonly Dictionary<string, McpResourceDefinition> _resources = new();
    private readonly Dictionary<string, object> _resourceServices = new();

    public McpResourceService(ILogger<McpResourceService> logger)
    {
        _logger = logger;
        RegisterBuiltInResources();
    }

    /// <summary>
    /// Register a service containing MCP resources marked with attributes
    /// </summary>
    /// <typeparam name="T">Service type</typeparam>
    /// <param name="service">Service instance</param>
    public void RegisterService<T>(T service) where T : class
    {
        var serviceType = typeof(T);
        _logger.LogInformation("Registering MCP resource service: {ServiceType}", serviceType.Name);

        var methods = serviceType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() != null);

        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<McpServerResourceAttribute>()!;
            var resourceDefinition = new McpResourceDefinition
            {
                Uri = attribute.Uri,
                Name = attribute.Name,
                Description = attribute.Description,
                MimeType = attribute.MimeType,
                Method = method,
                Service = service
            };
            
            _resources[attribute.Uri] = resourceDefinition;
            _resourceServices[attribute.Uri] = service;
            
            _logger.LogDebug("Registered MCP resource: {Uri} -> {ServiceType}.{MethodName}", 
                attribute.Uri, serviceType.Name, method.Name);
        }
    }

    /// <summary>
    /// Get all available MCP resources
    /// </summary>
    /// <returns>Collection of available resources</returns>
    public IEnumerable<McpResource> GetAvailableResources()
    {
        return _resources.Values.Select(rd => new McpResource
        {
            Uri = rd.Uri,
            Name = rd.Name,
            Description = rd.Description,
            MimeType = rd.MimeType,
            Metadata = new Dictionary<string, object>
            {
                ["lastModified"] = DateTime.UtcNow,
                ["size"] = 0, // Will be calculated when content is generated
                ["provider"] = rd.Service.GetType().Name
            }
        });
    }

    /// <summary>
    /// Get resource content by URI
    /// </summary>
    /// <param name="uri">Resource URI</param>
    /// <returns>Resource content as string</returns>
    public async Task<string> GetResourceContentAsync(string uri)
    {
        try
        {
            if (!_resources.TryGetValue(uri, out var resourceDefinition))
            {
                throw new ArgumentException($"Resource '{uri}' not found", nameof(uri));
            }

            var serviceInstance = _resourceServices[uri];
            var result = resourceDefinition.Method.Invoke(serviceInstance, null);
            
            // Handle async results
            if (result is Task<string> stringTask)
            {
                return await stringTask;
            }
            
            if (result is Task task)
            {
                await task;
                if (task.GetType().IsGenericType)
                {
                    var taskResult = task.GetType().GetProperty("Result")?.GetValue(task);
                    return taskResult?.ToString() ?? string.Empty;
                }
            }

            return result?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get resource content for URI: {Uri}", uri);
            throw new InvalidOperationException($"Failed to get resource content for '{uri}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get resource metadata by URI
    /// </summary>
    /// <param name="uri">Resource URI</param>
    /// <returns>Resource metadata</returns>
    public async Task<Dictionary<string, object>> GetResourceMetadataAsync(string uri)
    {
        try
        {
            if (!_resources.TryGetValue(uri, out var resourceDefinition))
            {
                throw new ArgumentException($"Resource '{uri}' not found", nameof(uri));
            }

            var content = await GetResourceContentAsync(uri);
            var contentSize = Encoding.UTF8.GetByteCount(content);

            return new Dictionary<string, object>
            {
                ["uri"] = uri,
                ["name"] = resourceDefinition.Name,
                ["description"] = resourceDefinition.Description,
                ["mimeType"] = resourceDefinition.MimeType,
                ["size"] = contentSize,
                ["lastModified"] = DateTime.UtcNow,
                ["provider"] = resourceDefinition.Service.GetType().Name,
                ["encoding"] = "utf-8"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get resource metadata for URI: {Uri}", uri);
            throw;
        }
    }

    /// <summary>
    /// Check if a resource exists
    /// </summary>
    /// <param name="uri">Resource URI</param>
    /// <returns>True if resource exists</returns>
    public bool ResourceExists(string uri)
    {
        return _resources.ContainsKey(uri);
    }

    /// <summary>
    /// Get resource definition by URI
    /// </summary>
    /// <param name="uri">Resource URI</param>
    /// <returns>Resource definition or null if not found</returns>
    public McpResourceDefinition? GetResourceDefinition(string uri)
    {
        return _resources.TryGetValue(uri, out var definition) ? definition : null;
    }

    private void RegisterBuiltInResources()
    {
        _logger.LogInformation("Registering built-in PKS resources");

        // Register the 3 core PKS resources using hardcoded definitions for now
        // TODO: Convert these to attribute-based once we have proper service implementations
        
        RegisterHardcodedResource(
            "pks://projects",
            "PKS Projects",
            "List of all PKS CLI projects and their configurations",
            "application/json",
            GenerateProjectsResourceContent
        );

        RegisterHardcodedResource(
            "pks://agents", 
            "PKS Agents",
            "List of all AI agents managed by PKS CLI",
            "application/json",
            GenerateAgentsResourceContent
        );

        RegisterHardcodedResource(
            "pks://tasks",
            "PKS Tasks", 
            "Current and historical tasks managed by PKS CLI",
            "application/json",
            GenerateTasksResourceContent
        );
    }

    private void RegisterHardcodedResource(string uri, string name, string description, string mimeType, Func<Task<string>> contentProvider)
    {
        var hardcodedService = new HardcodedResourceService(contentProvider);
        var resourceDefinition = new McpResourceDefinition
        {
            Uri = uri,
            Name = name,
            Description = description,
            MimeType = mimeType,
            Method = typeof(HardcodedResourceService).GetMethod(nameof(HardcodedResourceService.GetContentAsync))!,
            Service = hardcodedService
        };

        _resources[uri] = resourceDefinition;
        _resourceServices[uri] = hardcodedService;
        
        _logger.LogDebug("Registered hardcoded PKS resource: {Uri}", uri);
    }

    private async Task<string> GenerateProjectsResourceContent()
    {
        try
        {
            // This would normally fetch real project data
            var projects = new object[]
            {
                new
                {
                    name = "example-project",
                    path = "/workspace/example-project",
                    type = "console",
                    created = DateTime.UtcNow.AddDays(-7),
                    lastModified = DateTime.UtcNow.AddHours(-2),
                    features = new string[] { "agentic", "mcp" },
                    status = "active"
                },
                new
                {
                    name = "another-project",
                    path = "/workspace/another-project", 
                    type = "api",
                    created = DateTime.UtcNow.AddDays(-3),
                    lastModified = DateTime.UtcNow.AddMinutes(-30),
                    features = new string[] { "devcontainer" },
                    status = "active"
                }
            };

            var result = new
            {
                version = "1.0.0",
                timestamp = DateTime.UtcNow,
                projects = projects,
                total = projects.Length
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate projects resource content");
            return JsonSerializer.Serialize(new { error = "Failed to load projects", message = ex.Message });
        }
    }

    private async Task<string> GenerateAgentsResourceContent()
    {
        try
        {
            // This would normally fetch real agent data
            var agents = new object[]
            {
                new
                {
                    id = "pks-assistant",
                    name = "PKS Assistant",
                    type = "cli-agent",
                    status = "active",
                    capabilities = new string[] { "project-management", "deployment", "documentation" },
                    created = DateTime.UtcNow.AddDays(-5),
                    lastActivity = DateTime.UtcNow.AddMinutes(-10)
                },
                new
                {
                    id = "dev-helper",
                    name = "Development Helper",
                    type = "development-agent",
                    status = "idle",
                    capabilities = new string[] { "code-generation", "testing", "debugging" },
                    created = DateTime.UtcNow.AddDays(-2),
                    lastActivity = DateTime.UtcNow.AddHours(-4)
                }
            };

            var result = new
            {
                version = "1.0.0",
                timestamp = DateTime.UtcNow,
                agents = agents,
                total = agents.Length
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate agents resource content");
            return JsonSerializer.Serialize(new { error = "Failed to load agents", message = ex.Message });
        }
    }

    private async Task<string> GenerateTasksResourceContent()
    {
        try
        {
            // This would normally fetch real task data
            var tasks = new object[]
            {
                new
                {
                    id = "task-001",
                    name = "Initialize Project",
                    type = "project-initialization",
                    status = "completed",
                    progress = 100,
                    created = DateTime.UtcNow.AddHours(-6),
                    completed = DateTime.UtcNow.AddHours(-5),
                    duration = TimeSpan.FromHours(1).ToString()
                },
                new
                {
                    id = "task-002",
                    name = "Deploy to Kubernetes",
                    type = "deployment",
                    status = "in-progress", 
                    progress = 75,
                    created = DateTime.UtcNow.AddMinutes(-30),
                    completed = (DateTime?)null,
                    duration = TimeSpan.FromMinutes(30).ToString()
                }
            };

            var result = new
            {
                version = "1.0.0",
                timestamp = DateTime.UtcNow,
                tasks = tasks,
                total = tasks.Length,
                statistics = new
                {
                    completed = 1, // Simulated count - in real implementation would query actual data
                    inProgress = 1,
                    failed = 0
                }
            };

            return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate tasks resource content");
            return JsonSerializer.Serialize(new { error = "Failed to load tasks", message = ex.Message });
        }
    }
}

/// <summary>
/// Definition of an MCP resource with method reflection info
/// </summary>
public class McpResourceDefinition
{
    public string Uri { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string MimeType { get; set; } = "text/plain";
    public System.Reflection.MethodInfo Method { get; set; } = null!;
    public object Service { get; set; } = null!;
}

/// <summary>
/// Internal service for hardcoded resources during migration period
/// </summary>
internal class HardcodedResourceService
{
    private readonly Func<Task<string>> _contentProvider;

    public HardcodedResourceService(Func<Task<string>> contentProvider)
    {
        _contentProvider = contentProvider;
    }

    public async Task<string> GetContentAsync()
    {
        return await _contentProvider();
    }
}