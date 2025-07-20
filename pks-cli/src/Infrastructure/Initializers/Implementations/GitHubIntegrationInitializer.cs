using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Services;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Initializer for setting up GitHub integration and project identity
/// </summary>
public class GitHubIntegrationInitializer : CodeInitializer
{
    private readonly IGitHubService _gitHubService;
    private readonly IProjectIdentityService _projectIdentityService;

    public GitHubIntegrationInitializer(IGitHubService gitHubService, IProjectIdentityService projectIdentityService)
    {
        _gitHubService = gitHubService;
        _projectIdentityService = projectIdentityService;
    }

    public override string Id => "github-integration";
    public override string Name => "GitHub Integration";
    public override string Description => "Sets up GitHub repository integration and project identity system";
    public override int Order => 15; // Run early, after .NET project creation

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            InitializerOption.Flag("github", "Enable GitHub integration", "g"),
            InitializerOption.Flag("create-repo", "Create a new GitHub repository", "cr"),
            InitializerOption.String("github-token", "GitHub personal access token", "gt", ""),
            InitializerOption.String("repo-owner", "GitHub repository owner", "ro", ""),
            InitializerOption.String("repo-name", "GitHub repository name", "rn", ""),
            InitializerOption.Flag("private-repo", "Create private repository", "pr"),
            InitializerOption.Flag("init-git", "Initialize local git repository", "ig"),
            InitializerOption.String("remote-url", "Existing GitHub repository URL", "ru", "")
        };
    }

    public override Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Run if GitHub integration is explicitly requested or if any GitHub-related options are provided
        return Task.FromResult(context.GetOption("github", false) ||
               context.GetOption("create-repo", false) ||
               !string.IsNullOrEmpty(context.GetOption("github-token", "")) ||
               !string.IsNullOrEmpty(context.GetOption("remote-url", "")));
    }

    protected override async Task ExecuteCodeLogicAsync(InitializationContext context, InitializationResult result)
    {
        // Create project identity first
        await CreateProjectIdentityAsync(context, result);

        // Handle GitHub integration
        if (context.GetOption("github", false))
        {
            await SetupGitHubIntegrationAsync(context, result);
        }

        // Initialize git repository if requested
        if (context.GetOption("init-git", false))
        {
            await InitializeGitRepositoryAsync(context, result);
        }

        result.Message = "GitHub integration and project identity configured successfully";
    }

    private async Task CreateProjectIdentityAsync(InitializationContext context, InitializationResult result)
    {
        try
        {
            var projectIdentity = await _projectIdentityService.CreateProjectIdentityAsync(
                context.ProjectName,
                context.TargetDirectory,
                context.Description
            );

            // Store project ID in metadata for other initializers
            context.SetMetadata("ProjectId", projectIdentity.ProjectId);

            // Create .pks/project-info.md for documentation
            var projectInfoContent = GenerateProjectInfoMarkdown(projectIdentity, context);
            var projectInfoPath = Path.Combine(context.TargetDirectory, ".pks", "project-info.md");
            
            Directory.CreateDirectory(Path.GetDirectoryName(projectInfoPath)!);
            await CreateFileAsync("/.pks/project-info.md", projectInfoContent, context, result);

            result.AffectedFiles.Add(projectInfoPath);
            result.Message += $" Project ID: {projectIdentity.ProjectId}";
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to create project identity: {ex.Message}");
        }
    }

    private async Task SetupGitHubIntegrationAsync(InitializationContext context, InitializationResult result)
    {
        var projectId = context.GetMetadata<string>("ProjectId");
        if (string.IsNullOrEmpty(projectId))
        {
            result.Warnings.Add("Project ID not available for GitHub integration");
            return;
        }

        try
        {
            var token = context.GetOption("github-token", "");
            var repositoryUrl = "";

            // Create new repository if requested
            if (context.GetOption("create-repo", false))
            {
                repositoryUrl = await CreateGitHubRepositoryAsync(context, result);
            }
            else
            {
                repositoryUrl = context.GetOption("remote-url", "");
            }

            if (!string.IsNullOrEmpty(repositoryUrl))
            {
                // Configure GitHub integration
                var config = await _gitHubService.ConfigureProjectIntegrationAsync(projectId, repositoryUrl, token);
                
                if (config.IsValid)
                {
                    // Associate repository with project
                    await _projectIdentityService.AssociateGitHubRepositoryAsync(projectId, repositoryUrl, token);
                    
                    // Create GitHub configuration file
                    var githubConfigContent = GenerateGitHubConfigurationFile(config, context);
                    await CreateFileAsync("/.pks/github-config.json", githubConfigContent, context, result);
                    
                    result.Message += $" GitHub repository: {repositoryUrl}";
                }
                else
                {
                    result.Warnings.Add($"GitHub configuration may be incomplete: {config.ErrorMessage}");
                }
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to setup GitHub integration: {ex.Message}");
        }
    }

    private async Task<string> CreateGitHubRepositoryAsync(InitializationContext context, InitializationResult result)
    {
        var repoName = context.GetOption("repo-name", context.ProjectName) ?? context.ProjectName;
        var isPrivate = context.GetOption("private-repo", false);
        var description = context.Description ?? $"PKS CLI project: {context.ProjectName}";

        try
        {
            var repository = await _gitHubService.CreateRepositoryAsync(repoName, description, isPrivate);
            result.Message += $" Created GitHub repository: {repository.FullName}";
            return repository.CloneUrl;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to create GitHub repository: {ex.Message}");
            return "";
        }
    }

    private async Task InitializeGitRepositoryAsync(InitializationContext context, InitializationResult result)
    {
        var repositoryUrl = context.GetOption("remote-url", "");
        if (string.IsNullOrEmpty(repositoryUrl))
        {
            result.Warnings.Add("No repository URL provided for git initialization");
            return;
        }

        try
        {
            var initResult = await _gitHubService.InitializeGitRepositoryAsync(
                context.TargetDirectory,
                repositoryUrl,
                "Initial commit - PKS CLI project setup"
            );

            if (initResult.IsSuccess)
            {
                foreach (var step in initResult.Steps)
                {
                    result.Message += $" {step}";
                }
            }
            else
            {
                result.Errors.Add($"Git initialization failed: {initResult.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to initialize git repository: {ex.Message}");
        }
    }

    private string GenerateProjectInfoMarkdown(Services.Models.ProjectIdentity projectIdentity, InitializationContext context)
    {
        return $@"# Project Information

## Identity
- **Project ID**: `{projectIdentity.ProjectId}`
- **Name**: {projectIdentity.Name}
- **Description**: {projectIdentity.Description}
- **Created**: {projectIdentity.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC
- **Version**: {projectIdentity.Version}
- **Status**: {projectIdentity.Status}

## Configuration
- **Project Path**: `{projectIdentity.ProjectPath}`
- **Template**: {context.Template}

## Integration Status
- **GitHub**: {(string.IsNullOrEmpty(projectIdentity.GitHubRepository) ? "Not configured" : $"Connected to {projectIdentity.GitHubRepository}")}
- **MCP Server**: {(string.IsNullOrEmpty(projectIdentity.McpServerId) ? "Not configured" : $"Server ID: {projectIdentity.McpServerId}")}
- **Agents**: {projectIdentity.Agents.Count} registered
- **Hooks**: {(projectIdentity.HooksConfiguration?.Enabled == true ? "Enabled" : "Not configured")}

## PKS CLI Information
This project was created using PKS CLI version 1.0.0.

- **CLI Documentation**: https://github.com/pksorensen/pks-cli
- **Project Identity**: Managed by PKS CLI Project Identity Service
- **Configuration**: Stored in `.pks/` directory (excluded from git by default)

---
*Generated on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC*
";
    }

    private string GenerateGitHubConfigurationFile(Services.Models.GitHubConfiguration config, InitializationContext context)
    {
        var configObj = new
        {
            projectId = config.ProjectId,
            repositoryUrl = config.RepositoryUrl,
            configuredAt = config.ConfiguredAt.ToString("O"),
            isValid = config.IsValid,
            tokenScopes = config.TokenScopes,
            instructions = new[]
            {
                "This file contains GitHub integration configuration for PKS CLI",
                "Personal access tokens are stored securely in the system configuration",
                "To update GitHub token: pks config github.token <your-token>",
                "To check GitHub access: pks github --check-access"
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(configObj, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
    }
}