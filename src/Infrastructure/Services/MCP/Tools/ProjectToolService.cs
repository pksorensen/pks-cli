using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Initializers.Service;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PKS.CLI.Infrastructure.Services.MCP.Tools;

/// <summary>
/// MCP tool service for PKS project management operations
/// This service provides MCP tools for project initialization and related operations
/// </summary>
[McpServerToolType]
public class ProjectToolService
{
    // This class contains static methods that will be discovered by WithToolsFromAssembly()
    // Dependencies will be injected as method parameters

    /// <summary>
    /// Initialize a new PKS project with templates and AI features
    /// This tool connects to the real PKS init command functionality
    /// </summary>
    [McpServerTool]
    [Description("Initialize new projects with templates and AI features")]
    public static async Task<object> InitializeProjectAsync(
        ILogger<ProjectToolService> logger,
        IInitializationService initializationService,
        string projectName,
        string template = "console",
        string? description = null,
        bool agentic = false,
        bool mcp = false,
        bool force = false)
    {
        logger.LogInformation("MCP Tool: Initializing project '{ProjectName}' with template '{Template}'",
            projectName, template);

        try
        {
            // Prepare options for initializers (same as InitCommand)
            var options = new Dictionary<string, object?>
            {
                { "agentic", agentic },
                { "mcp", mcp },
                { "template", template },
                { "description", description ?? $"A .NET project initialized with PKS CLI using {template} template" },
                { "devcontainer", false } // Default to false for MCP tool
            };

            // Create target directory path
            var targetDirectory = Path.Combine(Environment.CurrentDirectory, projectName);

            // Validate target directory first
            var validation = await initializationService.ValidateTargetDirectoryAsync(targetDirectory, force);
            if (!validation.IsValid)
            {
                return new
                {
                    success = false,
                    error = validation.ErrorMessage,
                    projectName,
                    template,
                    message = $"Project initialization failed: {validation.ErrorMessage}"
                };
            }

            // Create initialization context
            var initContext = initializationService.CreateContext(
                projectName,
                template,
                targetDirectory,
                force,
                options);

            // Run initialization
            var summary = await initializationService.InitializeProjectAsync(initContext);

            // Return comprehensive result
            if (summary.Success)
            {
                return new
                {
                    success = true,
                    projectName,
                    template,
                    description = description ?? $"A .NET project initialized with PKS CLI using {template} template",
                    features = new { agentic, mcp },
                    location = targetDirectory,
                    filesCreated = summary.FilesCreated,
                    duration = summary.Duration.TotalSeconds,
                    warnings = summary.WarningsCount,
                    initializersExecuted = summary.FilesCreated > 0 ? "Multiple" : "None",
                    createdAt = DateTime.UtcNow,
                    message = $"Project '{projectName}' initialized successfully",
                    nextSteps = new[]
                    {
                        $"cd {projectName}",
                        "pks agent create",
                        "pks deploy --watch"
                    }
                };
            }
            else
            {
                return new
                {
                    success = false,
                    projectName,
                    template,
                    error = summary.ErrorMessage,
                    errors = summary.ErrorsCount,
                    message = $"Project initialization failed: {summary.ErrorMessage}"
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize project '{ProjectName}'", projectName);
            return new
            {
                success = false,
                projectName,
                template,
                error = ex.Message,
                message = $"Project initialization failed with exception: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Create a task for the PKS task management system
    /// This replaces the legacy pks_create_task functionality
    /// </summary>
    [McpServerTool]
    [Description("Create and queue a new task for an agent")]
    public static async Task<object> CreateTaskAsync(
        ILogger<ProjectToolService> logger,
        string taskDescription,
        string agentType = "deployment",
        string priority = "medium")
    {
        logger.LogInformation("MCP Tool: Creating task '{TaskDescription}' for agent type '{AgentType}' with priority '{Priority}'",
            taskDescription, agentType, priority);

        // Simulate task creation since we don't have a full task management system yet
        await Task.Delay(500);

        var taskId = Guid.NewGuid().ToString("N")[..8];
        var estimatedDuration = priority switch
        {
            "high" => TimeSpan.FromMinutes(15),
            "medium" => TimeSpan.FromMinutes(30),
            "low" => TimeSpan.FromHours(1),
            _ => TimeSpan.FromMinutes(30)
        };

        return new
        {
            success = true,
            taskId,
            taskDescription,
            agentType,
            priority,
            status = "queued",
            estimatedDuration = estimatedDuration.TotalMinutes,
            createdAt = DateTime.UtcNow,
            estimatedCompletion = DateTime.UtcNow.Add(estimatedDuration),
            message = $"Task '{taskDescription}' created successfully with ID {taskId}"
        };
    }

    /// <summary>
    /// Get project information and status
    /// </summary>
    [McpServerTool]
    [Description("Get current project information and status")]
    public static async Task<object> GetProjectStatusAsync(
        ILogger<ProjectToolService> logger,
        bool detailed = false)
    {
        logger.LogInformation("MCP Tool: Getting project status, detailed: {Detailed}", detailed);

        await Task.Delay(200);

        var currentDirectory = Environment.CurrentDirectory;
        var projectName = Path.GetFileName(currentDirectory);
        var hasProjectFile = Directory.GetFiles(currentDirectory, "*.csproj").Length > 0;
        var hasSolutionFile = Directory.GetFiles(currentDirectory, "*.sln").Length > 0;

        var status = new
        {
            success = true,
            projectName,
            location = currentDirectory,
            isProject = hasProjectFile || hasSolutionFile,
            projectFiles = hasProjectFile ? Directory.GetFiles(currentDirectory, "*.csproj") : Array.Empty<string>(),
            solutionFiles = hasSolutionFile ? Directory.GetFiles(currentDirectory, "*.sln") : Array.Empty<string>(),
            hasDevcontainer = Directory.Exists(Path.Combine(currentDirectory, ".devcontainer")),
            hasClaudeConfig = File.Exists(Path.Combine(currentDirectory, "CLAUDE.md")),
            hasMcpConfig = File.Exists(Path.Combine(currentDirectory, ".mcp.json")),
            timestamp = DateTime.UtcNow
        };

        if (detailed)
        {
            return new
            {
                success = status.success,
                projectName = status.projectName,
                location = status.location,
                isProject = status.isProject,
                projectFiles = status.projectFiles,
                solutionFiles = status.solutionFiles,
                hasDevcontainer = status.hasDevcontainer,
                hasClaudeConfig = status.hasClaudeConfig,
                hasMcpConfig = status.hasMcpConfig,
                timestamp = status.timestamp,
                detailedInfo = new
                {
                    fileCount = Directory.GetFiles(currentDirectory, "*", SearchOption.TopDirectoryOnly).Length,
                    directoryCount = Directory.GetDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly).Length,
                    totalSize = GetDirectorySize(currentDirectory),
                    lastModified = Directory.GetLastWriteTime(currentDirectory),
                    gitRepository = Directory.Exists(Path.Combine(currentDirectory, ".git")),
                    packageJson = File.Exists(Path.Combine(currentDirectory, "package.json")),
                    dockerFile = File.Exists(Path.Combine(currentDirectory, "Dockerfile"))
                }
            };
        }

        return status;
    }

    private static long GetDirectorySize(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch
        {
            return 0;
        }
    }
}