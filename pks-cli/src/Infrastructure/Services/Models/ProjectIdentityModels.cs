namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Core project identity containing all project-related information
/// </summary>
public class ProjectIdentity
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastModified { get; set; }
    public string Version { get; set; } = "1.0.0";
    public ProjectStatus Status { get; set; } = ProjectStatus.Active;
    
    // GitHub Integration
    public string? GitHubRepository { get; set; }
    
    // MCP Integration
    public string? McpServerId { get; set; }
    public McpServerConfiguration? McpConfiguration { get; set; }
    
    // Agent Framework
    public List<AgentInfo> Agents { get; set; } = new();
    
    // Hooks System
    public HooksConfiguration? HooksConfiguration { get; set; }
    
    // PRD Tools
    public PrdConfiguration? PrdConfiguration { get; set; }
    
    // Additional metadata
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string[] Tags { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Project status enumeration
/// </summary>
public enum ProjectStatus
{
    Active,
    Inactive,
    Archived,
    Deleted
}

/// <summary>
/// Information about an agent associated with a project
/// </summary>
public class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public DateTime RegisteredAt { get; set; }
    public string Status { get; set; } = "active";
    public Dictionary<string, string> Configuration { get; set; } = new();
}

/// <summary>
/// Hooks configuration for a project
/// </summary>
public class HooksConfiguration
{
    public bool Enabled { get; set; }
    public string[] EnabledHooks { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> HookSettings { get; set; } = new();
    public DateTime ConfiguredAt { get; set; }
}

/// <summary>
/// PRD (Product Requirements Document) configuration
/// </summary>
public class PrdConfiguration
{
    public bool Enabled { get; set; }
    public string PrdPath { get; set; } = string.Empty;
    public string RequirementsPath { get; set; } = string.Empty;
    public string UserStoriesPath { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public string[] Templates { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Result of project validation
/// </summary>
public class ProjectValidationResult
{
    public string ProjectId { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public string[] Issues { get; set; } = Array.Empty<string>();
    public DateTime ValidatedAt { get; set; }
    public string[] Warnings { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Export format for project backup/sharing
/// </summary>
public class ProjectExport
{
    public ProjectIdentity ProjectIdentity { get; set; } = new();
    public DateTime ExportedAt { get; set; }
    public string ExportVersion { get; set; } = "1.0.0";
    public string ExportedBy { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public byte[]? ConfigurationFiles { get; set; }
}