using System.Text.Json;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Implementation of project identity management service
/// </summary>
public class ProjectIdentityService : IProjectIdentityService
{
    private readonly IConfigurationService _configurationService;
    private readonly string _projectsConfigKey = "projects";
    private readonly string _pksConfigFolder = ".pks";

    public ProjectIdentityService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public async Task<ProjectIdentity> CreateProjectIdentityAsync(string projectName, string projectPath, string? description = null)
    {
        var projectId = GenerateProjectId(projectName);
        var identity = new ProjectIdentity
        {
            ProjectId = projectId,
            Name = projectName,
            ProjectPath = Path.GetFullPath(projectPath),
            Description = description ?? $"PKS CLI project: {projectName}",
            CreatedAt = DateTime.UtcNow,
            LastModified = DateTime.UtcNow,
            Version = "1.0.0",
            Status = ProjectStatus.Active
        };

        // Persist the identity
        await SaveProjectIdentityAsync(identity);
        await PersistProjectConfigurationAsync(projectPath, identity);

        return identity;
    }

    public async Task<ProjectIdentity?> GetProjectIdentityAsync(string projectId)
    {
        var projects = await GetProjectsFromConfigurationAsync();
        return projects.FirstOrDefault(p => p.ProjectId == projectId);
    }

    public async Task<ProjectIdentity?> GetProjectIdentityByPathAsync(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        
        // First try to load from local .pks folder
        var localIdentity = await LoadProjectConfigurationAsync(projectPath);
        if (localIdentity != null)
        {
            return localIdentity;
        }

        // Fallback to global configuration
        var projects = await GetProjectsFromConfigurationAsync();
        return projects.FirstOrDefault(p => p.ProjectPath == fullPath);
    }

    public async Task<bool> UpdateProjectIdentityAsync(ProjectIdentity projectIdentity)
    {
        try
        {
            projectIdentity.LastModified = DateTime.UtcNow;
            await SaveProjectIdentityAsync(projectIdentity);
            
            // Update local configuration if project path exists
            if (Directory.Exists(projectIdentity.ProjectPath))
            {
                await PersistProjectConfigurationAsync(projectIdentity.ProjectPath, projectIdentity);
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ProjectIdentity> AssociateGitHubRepositoryAsync(string projectId, string repositoryUrl, string? accessToken = null)
    {
        var identity = await GetProjectIdentityAsync(projectId);
        if (identity == null)
        {
            throw new InvalidOperationException($"Project with ID {projectId} not found");
        }

        identity.GitHubRepository = repositoryUrl;
        identity.LastModified = DateTime.UtcNow;

        // Store access token securely if provided
        if (!string.IsNullOrEmpty(accessToken))
        {
            await _configurationService.SetAsync($"github.{projectId}.token", accessToken, false, true);
        }

        await UpdateProjectIdentityAsync(identity);
        return identity;
    }

    public async Task<ProjectIdentity> AssociateMcpServerAsync(string projectId, string serverId, McpServerConfiguration configuration)
    {
        var identity = await GetProjectIdentityAsync(projectId);
        if (identity == null)
        {
            throw new InvalidOperationException($"Project with ID {projectId} not found");
        }

        identity.McpServerId = serverId;
        identity.McpConfiguration = configuration;
        identity.LastModified = DateTime.UtcNow;

        await UpdateProjectIdentityAsync(identity);
        return identity;
    }

    public async Task<ProjectIdentity> RegisterAgentAsync(string projectId, string agentId, string agentType)
    {
        var identity = await GetProjectIdentityAsync(projectId);
        if (identity == null)
        {
            throw new InvalidOperationException($"Project with ID {projectId} not found");
        }

        var agentInfo = new AgentInfo
        {
            AgentId = agentId,
            AgentType = agentType,
            RegisteredAt = DateTime.UtcNow,
            Status = "active"
        };

        identity.Agents.Add(agentInfo);
        identity.LastModified = DateTime.UtcNow;

        await UpdateProjectIdentityAsync(identity);
        return identity;
    }

    public async Task<ProjectIdentity> ConfigureHooksAsync(string projectId, HooksConfiguration hooksConfiguration)
    {
        var identity = await GetProjectIdentityAsync(projectId);
        if (identity == null)
        {
            throw new InvalidOperationException($"Project with ID {projectId} not found");
        }

        identity.HooksConfiguration = hooksConfiguration;
        identity.LastModified = DateTime.UtcNow;

        await UpdateProjectIdentityAsync(identity);
        return identity;
    }

    public async Task<IEnumerable<ProjectIdentity>> GetAllProjectsAsync()
    {
        return await GetProjectsFromConfigurationAsync();
    }

    public async Task<bool> RemoveProjectAsync(string projectId)
    {
        try
        {
            var projects = await GetProjectsFromConfigurationAsync();
            var project = projects.FirstOrDefault(p => p.ProjectId == projectId);
            
            if (project != null)
            {
                var updatedProjects = projects.Where(p => p.ProjectId != projectId).ToList();
                await SaveProjectsToConfigurationAsync(updatedProjects);
                
                // Clean up project-specific configuration
                await CleanupProjectConfigurationAsync(projectId);
                
                // Remove local .pks folder if it exists
                if (Directory.Exists(project.ProjectPath))
                {
                    var pksPath = Path.Combine(project.ProjectPath, _pksConfigFolder);
                    if (Directory.Exists(pksPath))
                    {
                        Directory.Delete(pksPath, true);
                    }
                }
            }
            
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PersistProjectConfigurationAsync(string projectPath, ProjectIdentity projectIdentity)
    {
        try
        {
            var pksPath = Path.Combine(projectPath, _pksConfigFolder);
            Directory.CreateDirectory(pksPath);

            var configPath = Path.Combine(pksPath, "project.json");
            var json = JsonSerializer.Serialize(projectIdentity, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await File.WriteAllTextAsync(configPath, json);

            // Create .gitignore if it doesn't exist
            var gitignorePath = Path.Combine(pksPath, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                await File.WriteAllTextAsync(gitignorePath, "# PKS CLI local configuration\n*.local\nsecrets.json\n");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ProjectIdentity?> LoadProjectConfigurationAsync(string projectPath)
    {
        try
        {
            var configPath = Path.Combine(projectPath, _pksConfigFolder, "project.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(configPath);
            var identity = JsonSerializer.Deserialize<ProjectIdentity>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return identity;
        }
        catch
        {
            return null;
        }
    }

    public string GenerateProjectId(string projectName)
    {
        // Create a deterministic but unique ID based on project name and timestamp
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sanitizedName = string.Concat(projectName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            .ToLowerInvariant();
        
        if (sanitizedName.Length > 20)
        {
            sanitizedName = sanitizedName[..20];
        }

        return $"pks-{sanitizedName}-{timestamp:x}";
    }

    public async Task<ProjectValidationResult> ValidateProjectAsync(ProjectIdentity projectIdentity)
    {
        var result = new ProjectValidationResult
        {
            ProjectId = projectIdentity.ProjectId,
            IsValid = true,
            ValidatedAt = DateTime.UtcNow
        };

        var issues = new List<string>();

        // Validate basic properties
        if (string.IsNullOrWhiteSpace(projectIdentity.Name))
        {
            issues.Add("Project name is required");
        }

        if (string.IsNullOrWhiteSpace(projectIdentity.ProjectPath))
        {
            issues.Add("Project path is required");
        }
        else if (!Directory.Exists(projectIdentity.ProjectPath))
        {
            issues.Add($"Project path does not exist: {projectIdentity.ProjectPath}");
        }

        // Validate GitHub configuration if present
        if (!string.IsNullOrEmpty(projectIdentity.GitHubRepository))
        {
            if (!Uri.TryCreate(projectIdentity.GitHubRepository, UriKind.Absolute, out var uri) ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add("Invalid GitHub repository URL");
            }
        }

        // Validate MCP configuration if present
        if (projectIdentity.McpConfiguration != null)
        {
            if (string.IsNullOrWhiteSpace(projectIdentity.McpConfiguration.Name))
            {
                issues.Add("MCP server name is required");
            }
        }

        result.Issues = issues.ToArray();
        result.IsValid = issues.Count == 0;

        return result;
    }

    public async Task<ProjectExport?> ExportProjectAsync(string projectId)
    {
        var identity = await GetProjectIdentityAsync(projectId);
        if (identity == null)
        {
            return null;
        }

        return new ProjectExport
        {
            ProjectIdentity = identity,
            ExportedAt = DateTime.UtcNow,
            ExportVersion = "1.0.0",
            ExportedBy = Environment.UserName,
            Metadata = new Dictionary<string, string>
            {
                { "cli-version", "1.0.0" },
                { "export-source", "PKS-CLI" }
            }
        };
    }

    public async Task<ProjectIdentity> ImportProjectAsync(ProjectExport projectExport, string targetPath)
    {
        var identity = projectExport.ProjectIdentity;
        
        // Update paths and regenerate ID if needed
        identity.ProjectPath = Path.GetFullPath(targetPath);
        identity.LastModified = DateTime.UtcNow;
        
        // Ensure unique project ID
        var existingProject = await GetProjectIdentityAsync(identity.ProjectId);
        if (existingProject != null)
        {
            identity.ProjectId = GenerateProjectId(identity.Name);
        }

        await SaveProjectIdentityAsync(identity);
        await PersistProjectConfigurationAsync(targetPath, identity);

        return identity;
    }

    #region Private Helper Methods

    private async Task<List<ProjectIdentity>> GetProjectsFromConfigurationAsync()
    {
        try
        {
            var projectsJson = await _configurationService.GetAsync(_projectsConfigKey);
            if (string.IsNullOrEmpty(projectsJson))
            {
                return new List<ProjectIdentity>();
            }

            var projects = JsonSerializer.Deserialize<List<ProjectIdentity>>(projectsJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return projects ?? new List<ProjectIdentity>();
        }
        catch
        {
            return new List<ProjectIdentity>();
        }
    }

    private async Task SaveProjectsToConfigurationAsync(List<ProjectIdentity> projects)
    {
        var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await _configurationService.SetAsync(_projectsConfigKey, json);
    }

    private async Task SaveProjectIdentityAsync(ProjectIdentity identity)
    {
        var projects = await GetProjectsFromConfigurationAsync();
        var existingIndex = projects.FindIndex(p => p.ProjectId == identity.ProjectId);
        
        if (existingIndex >= 0)
        {
            projects[existingIndex] = identity;
        }
        else
        {
            projects.Add(identity);
        }

        await SaveProjectsToConfigurationAsync(projects);
    }

    private async Task CleanupProjectConfigurationAsync(string projectId)
    {
        // Clean up project-specific configuration keys
        var configKeys = new[]
        {
            $"github.{projectId}.token",
            $"github.{projectId}.repository",
            $"github.{projectId}.configured_at",
            $"github.{projectId}.scopes",
            $"github.{projectId}.config_name",
            $"mcp.{projectId}.server_id",
            $"mcp.{projectId}.configuration",
            $"agents.{projectId}.list",
            $"hooks.{projectId}.configuration"
        };

        foreach (var key in configKeys)
        {
            try
            {
                await _configurationService.DeleteAsync(key);
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }
    }

    #endregion
}