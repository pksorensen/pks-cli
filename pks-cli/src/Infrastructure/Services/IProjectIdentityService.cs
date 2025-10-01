using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for managing project identity, configuration persistence, and cross-component coordination
/// </summary>
public interface IProjectIdentityService
{
    /// <summary>
    /// Creates a new project identity with unique ID and configuration
    /// </summary>
    /// <param name="projectName">Name of the project</param>
    /// <param name="projectPath">Local path to the project</param>
    /// <param name="description">Optional project description</param>
    /// <returns>Created project identity</returns>
    Task<ProjectIdentity> CreateProjectIdentityAsync(string projectName, string projectPath, string? description = null);

    /// <summary>
    /// Retrieves project identity from configuration
    /// </summary>
    /// <param name="projectId">Unique project identifier</param>
    /// <returns>Project identity if found, null otherwise</returns>
    Task<ProjectIdentity?> GetProjectIdentityAsync(string projectId);

    /// <summary>
    /// Retrieves project identity by project path
    /// </summary>
    /// <param name="projectPath">Local path to the project</param>
    /// <returns>Project identity if found, null otherwise</returns>
    Task<ProjectIdentity?> GetProjectIdentityByPathAsync(string projectPath);

    /// <summary>
    /// Updates project identity configuration
    /// </summary>
    /// <param name="projectIdentity">Updated project identity</param>
    /// <returns>Success status</returns>
    Task<bool> UpdateProjectIdentityAsync(ProjectIdentity projectIdentity);

    /// <summary>
    /// Associates a GitHub repository with the project
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <param name="repositoryUrl">GitHub repository URL</param>
    /// <param name="accessToken">Optional access token for the repository</param>
    /// <returns>Updated project identity</returns>
    Task<ProjectIdentity> AssociateGitHubRepositoryAsync(string projectId, string repositoryUrl, string? accessToken = null);

    /// <summary>
    /// Associates an MCP server with the project
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <param name="serverId">MCP server identifier</param>
    /// <param name="configuration">Server configuration</param>
    /// <returns>Updated project identity</returns>
    Task<ProjectIdentity> AssociateMcpServerAsync(string projectId, string serverId, McpServerConfiguration configuration);

    /// <summary>
    /// Registers an agent with the project
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <param name="agentId">Agent identifier</param>
    /// <param name="agentType">Type of agent</param>
    /// <returns>Updated project identity</returns>
    Task<ProjectIdentity> RegisterAgentAsync(string projectId, string agentId, string agentType);

    /// <summary>
    /// Configures hooks for the project
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <param name="hooksConfiguration">Hooks configuration</param>
    /// <returns>Updated project identity</returns>
    Task<ProjectIdentity> ConfigureHooksAsync(string projectId, HooksConfiguration hooksConfiguration);

    /// <summary>
    /// Gets all projects managed by this system
    /// </summary>
    /// <returns>List of all project identities</returns>
    Task<IEnumerable<ProjectIdentity>> GetAllProjectsAsync();

    /// <summary>
    /// Removes a project identity and all associated configuration
    /// </summary>
    /// <param name="projectId">Project identifier to remove</param>
    /// <returns>Success status</returns>
    Task<bool> RemoveProjectAsync(string projectId);

    /// <summary>
    /// Creates or updates the .pks configuration folder in the project
    /// </summary>
    /// <param name="projectPath">Project path</param>
    /// <param name="projectIdentity">Project identity to persist</param>
    /// <returns>Success status</returns>
    Task<bool> PersistProjectConfigurationAsync(string projectPath, ProjectIdentity projectIdentity);

    /// <summary>
    /// Loads project configuration from .pks folder
    /// </summary>
    /// <param name="projectPath">Project path</param>
    /// <returns>Project identity if configuration exists</returns>
    Task<ProjectIdentity?> LoadProjectConfigurationAsync(string projectPath);

    /// <summary>
    /// Generates a unique project identifier
    /// </summary>
    /// <param name="projectName">Project name for context</param>
    /// <returns>Unique project identifier</returns>
    string GenerateProjectId(string projectName);

    /// <summary>
    /// Validates project identity integrity
    /// </summary>
    /// <param name="projectIdentity">Project identity to validate</param>
    /// <returns>Validation result with any issues found</returns>
    Task<ProjectValidationResult> ValidateProjectAsync(ProjectIdentity projectIdentity);

    /// <summary>
    /// Exports project configuration for backup or sharing
    /// </summary>
    /// <param name="projectId">Project identifier</param>
    /// <returns>Exportable project configuration</returns>
    Task<ProjectExport?> ExportProjectAsync(string projectId);

    /// <summary>
    /// Imports project configuration from export
    /// </summary>
    /// <param name="projectExport">Project export data</param>
    /// <param name="targetPath">Target path for import</param>
    /// <returns>Imported project identity</returns>
    Task<ProjectIdentity> ImportProjectAsync(ProjectExport projectExport, string targetPath);
}