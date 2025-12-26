using Moq;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services;
using PKS.CLI.Infrastructure.Services.MCP;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.CLI.Infrastructure.Services.Models;
using AgentModels = PKS.CLI.Infrastructure.Services.Models;
using ProjectModels = PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Tests.Infrastructure.Mocks;

/// <summary>
/// Factory for creating mocks of core PKS CLI services
/// </summary>
public static class ServiceMockFactory
{
    /// <summary>
    /// Creates a mock IKubernetesService with default behavior
    /// </summary>
    public static Mock<PKS.CLI.Tests.Infrastructure.Mocks.IKubernetesService> CreateKubernetesService()
    {
        var mock = new Mock<IKubernetesService>();

        // Setup default successful behaviors
        mock.Setup(x => x.ValidateConnectionAsync())
            .ReturnsAsync(true);

        mock.Setup(x => x.DeployAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
            .ReturnsAsync(new DeploymentResult { Success = true, Message = "Deployment successful" });

        return mock;
    }

    /// <summary>
    /// Creates a mock IConfigurationService with default behavior
    /// </summary>
    public static Mock<PKS.CLI.Tests.Infrastructure.Mocks.IConfigurationService> CreateConfigurationService()
    {
        var mock = new Mock<IConfigurationService>();

        mock.Setup(x => x.GetAsync<string>(It.IsAny<string>()))
            .ReturnsAsync((string key) => $"test-{key}");

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<object>()))
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Creates a mock configuration service with first-time warning support for testing
    /// </summary>
    public static Mock<PKS.Infrastructure.IConfigurationService> CreateConfigurationServiceWithWarningSupport()
    {
        var mock = new Mock<PKS.Infrastructure.IConfigurationService>();
        var settings = new Dictionary<string, string>();

        // Setup configuration methods that work with existing interfaces
        mock.Setup(x => x.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string key) => settings.TryGetValue(key, out var value) ? value : null);

        mock.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Callback<string, string, bool, bool>((key, value, global, encrypt) =>
            {
                settings[key] = encrypt ? "***encrypted***" : value;
            })
            .Returns(Task.CompletedTask);

        mock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(() => new Dictionary<string, string>(settings));

        mock.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .Callback<string>(key => settings.Remove(key))
            .Returns(Task.CompletedTask);

        return mock;
    }

    /// <summary>
    /// Creates a mock IDeploymentService with default behavior
    /// </summary>
    public static Mock<PKS.CLI.Tests.Infrastructure.Mocks.IDeploymentService> CreateDeploymentService()
    {
        var mock = new Mock<IDeploymentService>();

        mock.Setup(x => x.ExecuteDeploymentAsync(It.IsAny<DeploymentPlan>()))
            .ReturnsAsync(new DeploymentResult { Success = true, Message = "Deployment completed" });

        mock.Setup(x => x.ValidateDeploymentAsync(It.IsAny<DeploymentPlan>()))
            .ReturnsAsync(new ValidationResult { IsValid = true });

        return mock;
    }

    /// <summary>
    /// Creates a mock IInitializationService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Initializers.Service.IInitializationService> CreateInitializationService()
    {
        var mock = new Mock<PKS.Infrastructure.Initializers.Service.IInitializationService>();

        mock.Setup(x => x.InitializeProjectAsync(It.IsAny<PKS.Infrastructure.Initializers.Context.InitializationContext>()))
            .ReturnsAsync(new PKS.Infrastructure.Initializers.Service.InitializationSummary
            {
                ProjectName = "TestProject",
                Template = "console",
                TargetDirectory = "/test",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now.AddSeconds(1),
                Success = true,
                FilesCreated = 2
            });

        mock.Setup(x => x.GetAvailableTemplatesAsync())
            .ReturnsAsync(new List<PKS.Infrastructure.Initializers.Service.TemplateInfo>
            {
                new() { Name = "console", DisplayName = "Console App", Description = "Console application" },
                new() { Name = "api", DisplayName = "Web API", Description = "ASP.NET Core Web API" }
            });

        mock.Setup(x => x.ValidateTargetDirectoryAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(PKS.Infrastructure.Initializers.Service.ValidationResult.Valid());

        mock.Setup(x => x.CreateContext(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<Dictionary<string, object?>>()))
            .Returns((string projectName, string template, string targetDirectory, bool force, Dictionary<string, object?> options) =>
                new PKS.Infrastructure.Initializers.Context.InitializationContext
                {
                    ProjectName = projectName,
                    Template = template,
                    TargetDirectory = targetDirectory,
                    WorkingDirectory = targetDirectory,
                    Force = force,
                    Options = options ?? new Dictionary<string, object?>()
                });

        return mock;
    }

    /// <summary>
    /// Creates a mock IInitializerRegistry with default behavior
    /// Note: This is no longer used as we use real implementations
    /// </summary>
    public static Mock<PKS.Infrastructure.Initializers.Registry.IInitializerRegistry> CreateInitializerRegistry()
    {
        var mock = new Mock<PKS.Infrastructure.Initializers.Registry.IInitializerRegistry>();

        mock.Setup(x => x.GetAllAsync())
            .ReturnsAsync(new List<PKS.Infrastructure.Initializers.IInitializer>());

        mock.Setup(x => x.GetByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => null);

        mock.Setup(x => x.GetApplicableAsync(It.IsAny<PKS.Infrastructure.Initializers.Context.InitializationContext>()))
            .ReturnsAsync(new List<PKS.Infrastructure.Initializers.IInitializer>());

        mock.Setup(x => x.GetAllOptions())
            .Returns(new List<PKS.Infrastructure.Initializers.Context.InitializerOption>());

        mock.Setup(x => x.ExecuteAllAsync(It.IsAny<PKS.Infrastructure.Initializers.Context.InitializationContext>()))
            .ReturnsAsync(new List<PKS.Infrastructure.Initializers.Context.InitializationResult>
            {
                PKS.Infrastructure.Initializers.Context.InitializationResult.CreateSuccess("All initializers completed")
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IHooksService (to be implemented)
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IHooksService> CreateHooksService()
    {
        var mock = new Mock<IHooksService>();

        mock.Setup(x => x.GetAvailableHooksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PKS.Infrastructure.Services.Models.HookDefinition>());

        mock.Setup(x => x.ExecuteHookAsync(It.IsAny<string>(), It.IsAny<PKS.Infrastructure.Services.Models.HookContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PKS.Infrastructure.Services.Models.HookResult { Success = true, Message = "Hook executed successfully" });

        return mock;
    }

    /// <summary>
    /// Creates a mock IMcpHostingService
    /// </summary>
    public static Mock<IMcpHostingService> CreateMcpHostingService()
    {
        var mock = new Mock<IMcpHostingService>();
        var isRunning = false;
        var currentTransport = "stdio";

        mock.Setup(x => x.StartServerAsync(It.IsAny<McpServerConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((McpServerConfig config, CancellationToken ct) =>
            {
                isRunning = true;
                currentTransport = config.Transport;
                return new McpServerResult
                {
                    Success = true,
                    Port = 8080,
                    Transport = config.Transport,
                    Message = "Server started successfully"
                };
            });

        mock.Setup(x => x.StopServerAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                isRunning = false;
                return true;
            });

        mock.Setup(x => x.GetServerStatusAsync())
            .ReturnsAsync(() => new McpServerStatusInfo
            {
                Status = isRunning ? McpServerStatus.Running : McpServerStatus.Stopped,
                Port = isRunning ? 8080 : 0,
                Transport = currentTransport
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock McpToolService with proper logger dependency
    /// </summary>
    public static Mock<McpToolService> CreateMcpToolService()
    {
        var mockLogger = new Mock<ILogger<McpToolService>>();
        var mock = new Mock<McpToolService>(mockLogger.Object);

        mock.Setup(x => x.GetAvailableTools())
            .Returns(new List<McpServerTool>
            {
                new() { Name = "test_tool", Description = "Test tool", Category = "test", Enabled = true }
            });

        mock.Setup(x => x.ExecuteToolAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(McpToolExecutionResult.CreateSuccess("Tool executed successfully", null, 100));

        return mock;
    }

    /// <summary>
    /// Creates a mock McpResourceService with proper logger dependency
    /// </summary>
    public static Mock<McpResourceService> CreateMcpResourceService()
    {
        var mockLogger = new Mock<ILogger<McpResourceService>>();
        var mock = new Mock<McpResourceService>(mockLogger.Object);

        mock.Setup(x => x.GetAvailableResources())
            .Returns(new List<McpServerResource>
            {
                new() {
                    Name = "Projects",
                    Uri = "pks://projects",
                    MimeType = "application/json",
                    Description = "List of all PKS CLI projects and their configurations",
                    Metadata = new Dictionary<string, object> { ["category"] = "pks" }
                },
                new() {
                    Name = "Agents",
                    Uri = "pks://agents",
                    MimeType = "application/json",
                    Description = "List of all AI agents managed by PKS CLI",
                    Metadata = new Dictionary<string, object> { ["category"] = "pks" }
                },
                new() {
                    Name = "Current Tasks",
                    Uri = "pks://tasks",
                    MimeType = "application/json",
                    Description = "Current and historical tasks managed by PKS CLI",
                    Metadata = new Dictionary<string, object> { ["category"] = "pks" }
                }
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IAgentFrameworkService (to be implemented)
    /// </summary>
    public static Mock<IAgentFrameworkService> CreateAgentFrameworkService()
    {
        var mock = new Mock<IAgentFrameworkService>();

        mock.Setup(x => x.CreateAgentAsync(It.IsAny<AgentModels.AgentConfiguration>()))
            .ReturnsAsync(new AgentModels.AgentResult { Success = true, AgentId = "test-agent-123" });

        mock.Setup(x => x.ListAgentsAsync())
            .ReturnsAsync(new List<AgentModels.AgentInfo>());

        mock.Setup(x => x.GetAgentStatusAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentModels.AgentStatus { Id = "test-agent", Status = "Active" });

        mock.Setup(x => x.StartAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentModels.AgentResult { Success = true, Message = "Agent started" });

        mock.Setup(x => x.StopAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentModels.AgentResult { Success = true, Message = "Agent stopped" });

        mock.Setup(x => x.RemoveAgentAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        mock.Setup(x => x.LoadConfigurationAsync(It.IsAny<string>()))
            .ReturnsAsync(new AgentModels.AgentConfiguration { Name = "test-agent", Type = "automation" });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerService with default behavior
    /// </summary>
    public static Mock<IDevcontainerService> CreateDevcontainerService()
    {
        return DevcontainerServiceMocks.CreateDevcontainerService();
    }

    /// <summary>
    /// Creates a mock IDevcontainerFeatureRegistry with default behavior
    /// </summary>
    public static Mock<IDevcontainerFeatureRegistry> CreateDevcontainerFeatureRegistry()
    {
        return DevcontainerServiceMocks.CreateFeatureRegistry();
    }

    /// <summary>
    /// Creates a mock IDevcontainerTemplateService with default behavior
    /// </summary>
    public static Mock<IDevcontainerTemplateService> CreateDevcontainerTemplateService()
    {
        return DevcontainerServiceMocks.CreateTemplateService();
    }

    /// <summary>
    /// Creates a mock IDevcontainerFileGenerator with default behavior
    /// </summary>
    public static Mock<IDevcontainerFileGenerator> CreateDevcontainerFileGenerator()
    {
        return DevcontainerServiceMocks.CreateFileGenerator();
    }

    /// <summary>
    /// Creates a mock IVsCodeExtensionService with default behavior
    /// </summary>
    public static Mock<IVsCodeExtensionService> CreateVsCodeExtensionService()
    {
        return DevcontainerServiceMocks.CreateVsCodeExtensionService();
    }

    /// <summary>
    /// Creates a mock INuGetTemplateDiscoveryService with default behavior
    /// </summary>
    public static Mock<INuGetTemplateDiscoveryService> CreateNuGetTemplateDiscoveryService()
    {
        var mock = new Mock<INuGetTemplateDiscoveryService>();

        // Setup default successful behaviors for template discovery
        mock.Setup(x => x.DiscoverTemplatesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NuGetDevcontainerTemplate>
            {
                new() { Id = "dotnet-web", Title = "ASP.NET Core Web Template", Description = "Web application template", Version = "1.0.0" },
                new() { Id = "dotnet-basic", Title = "Basic .NET Template", Description = "Basic console template", Version = "1.0.0" }
            });

        mock.Setup(x => x.ExtractTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NuGetTemplateExtractionResult
            {
                Success = true,
                ExtractedPath = "/temp/extracted",
                Message = "Template extracted successfully"
            });

        mock.Setup(x => x.GetTemplateDetailsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NuGetTemplateDetails
            {
                Id = "test-template",
                Title = "Test Template",
                Description = "Test template for unit tests",
                Version = "1.0.0"
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock ITemplatePackagingService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.ITemplatePackagingService> CreateTemplatePackagingService()
    {
        var mock = new Mock<PKS.Infrastructure.Services.ITemplatePackagingService>();

        // Setup successful packaging behavior
        mock.Setup(x => x.PackSolutionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string solutionPath, string outputPath, string configuration, CancellationToken ct) =>
            {
                var packages = new List<string>
                {
                    Path.Combine(outputPath, "pks-cli.1.0.0.nupkg"),
                    Path.Combine(outputPath, "PKS.Templates.DevContainer.1.0.0.nupkg"),
                    Path.Combine(outputPath, "PKS.Templates.ClaudeDocs.1.0.0.nupkg"),
                    Path.Combine(outputPath, "PKS.Templates.Hooks.1.0.0.nupkg"),
                    Path.Combine(outputPath, "PKS.Templates.MCP.1.0.0.nupkg"),
                    Path.Combine(outputPath, "PKS.Templates.PRD.1.0.0.nupkg")
                };

                // Create mock package files to simulate real packages
                Directory.CreateDirectory(outputPath);
                foreach (var packagePath in packages)
                {
                    File.WriteAllText(packagePath, "Mock NuGet package content");
                }

                return new PKS.Infrastructure.Services.PackagingResult
                {
                    Success = true,
                    Output = $"Successfully packed {packages.Count} packages",
                    CreatedPackages = packages,
                    Duration = TimeSpan.FromSeconds(2)
                };
            });

        // Setup successful template installation
        mock.Setup(x => x.InstallTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string packagePath, string workingDirectory, CancellationToken ct) =>
            {
                var packageName = Path.GetFileNameWithoutExtension(packagePath);

                return new PKS.Infrastructure.Services.InstallationResult
                {
                    Success = true,
                    Output = $"Successfully installed template package '{packageName}'",
                    PackageName = packageName,
                    InstalledTemplates = new List<string> { "pks-devcontainer", "pks-claude-docs", "pks-hooks" }
                };
            });

        // Setup successful template uninstallation
        mock.Setup(x => x.UninstallTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string packageName, string workingDirectory, CancellationToken ct) =>
            {
                return new PKS.Infrastructure.Services.UninstallationResult
                {
                    Success = true,
                    Output = $"Successfully uninstalled template package '{packageName}'",
                    PackageName = packageName
                };
            });

        // Setup template listing
        mock.Setup(x => x.ListTemplatesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string workingDirectory, CancellationToken ct) =>
            {
                return new PKS.Infrastructure.Services.TemplateListResult
                {
                    Success = true,
                    Output = "Template Name      Short Name      Language    Tags\n" +
                            "PKS DevContainer   pks-devcontainer C#         pks/devcontainer\n" +
                            "PKS Claude Docs    pks-claude-docs  -          pks/documentation\n" +
                            "PKS Hooks          pks-hooks        -          pks/git",
                    Templates = new List<PKS.Infrastructure.Services.PackagingTemplateInfo>
                    {
                        new() { Name = "PKS DevContainer", ShortName = "pks-devcontainer", Language = "C#", Tags = new List<string> { "pks", "devcontainer" } },
                        new() { Name = "PKS Claude Docs", ShortName = "pks-claude-docs", Language = "", Tags = new List<string> { "pks", "documentation" } },
                        new() { Name = "PKS Hooks", ShortName = "pks-hooks", Language = "", Tags = new List<string> { "pks", "git" } }
                    }
                };
            });

        // Setup project creation from template
        mock.Setup(x => x.CreateProjectFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string templateName, string projectName, string workingDirectory, CancellationToken ct) =>
            {
                var projectPath = Path.Combine(workingDirectory, projectName);
                var createdFiles = new List<string>();

                // Create mock project structure based on template
                Directory.CreateDirectory(projectPath);

                if (templateName == "pks-devcontainer")
                {
                    var devcontainerDir = Path.Combine(projectPath, ".devcontainer");
                    Directory.CreateDirectory(devcontainerDir);

                    var devcontainerJson = Path.Combine(devcontainerDir, "devcontainer.json");
                    File.WriteAllText(devcontainerJson, "{ \"name\": \"Test DevContainer\" }");
                    createdFiles.Add(devcontainerJson);
                }

                var readmePath = Path.Combine(projectPath, "README.md");
                File.WriteAllText(readmePath, $"# {projectName}\n\nProject created from template: {templateName}");
                createdFiles.Add(readmePath);

                return new PKS.Infrastructure.Services.ProjectCreationResult
                {
                    Success = true,
                    Output = $"The template '{templateName}' was created successfully.",
                    ProjectPath = projectPath,
                    CreatedFiles = createdFiles
                };
            });

        // Setup package validation
        mock.Setup(x => x.ValidatePackageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string packagePath, CancellationToken ct) =>
            {
                var packageName = Path.GetFileNameWithoutExtension(packagePath);

                return new PKS.Infrastructure.Services.PackageValidationResult
                {
                    Success = true,
                    Metadata = new PKS.Infrastructure.Services.PackageMetadata
                    {
                        Id = packageName,
                        Version = "1.0.0",
                        Title = $"{packageName} Template",
                        Description = $"Template package for {packageName}",
                        Authors = "PKS CLI",
                        IsTemplate = true,
                        Tags = new List<string> { "pks", "template" }
                    }
                };
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerSpawnerService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IDevcontainerSpawnerService> CreateDevcontainerSpawnerService()
    {
        var mock = new Mock<IDevcontainerSpawnerService>();

        // Setup CheckDockerAvailabilityAsync
        mock.Setup(x => x.CheckDockerAvailabilityAsync())
            .ReturnsAsync(new DockerAvailabilityResult
            {
                IsAvailable = true,
                IsRunning = true,
                Version = "24.0.0",
                Message = "Docker is running"
            });

        // Setup IsDevcontainerCliInstalledAsync
        mock.Setup(x => x.IsDevcontainerCliInstalledAsync())
            .ReturnsAsync(true);

        // Setup CheckVsCodeInstallationAsync
        mock.Setup(x => x.CheckVsCodeInstallationAsync())
            .ReturnsAsync(new VsCodeInstallationInfo
            {
                IsInstalled = true,
                ExecutablePath = "code",
                Version = "1.85.0",
                Edition = VsCodeEdition.Stable
            });

        // Setup GenerateVolumeName
        mock.Setup(x => x.GenerateVolumeName(It.IsAny<string>()))
            .Returns((string projectName) => $"devcontainer-{projectName.ToLowerInvariant()}-abc12345");

        // Setup SpawnLocalAsync
        mock.Setup(x => x.SpawnLocalAsync(It.IsAny<DevcontainerSpawnOptions>()))
            .ReturnsAsync((DevcontainerSpawnOptions options) => new DevcontainerSpawnResult
            {
                Success = true,
                Message = "Devcontainer spawned successfully",
                ContainerId = $"container-{Guid.NewGuid():N[..12]}",
                VolumeName = $"devcontainer-{options.ProjectName.ToLowerInvariant()}-{Guid.NewGuid():N[..8]}",
                VsCodeUri = $"vscode-remote://dev-container+abc123/workspaces/{options.ProjectName}",
                CompletedStep = DevcontainerSpawnStep.Completed,
                Duration = TimeSpan.FromSeconds(30)
            });

        // Setup CleanupFailedSpawnAsync
        mock.Setup(x => x.CleanupFailedSpawnAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        // Setup FindExistingContainerAsync
        mock.Setup(x => x.FindExistingContainerAsync(It.IsAny<string>()))
            .ReturnsAsync((string projectPath) => null);

        // Setup ListManagedVolumesAsync
        mock.Setup(x => x.ListManagedVolumesAsync())
            .ReturnsAsync(new List<DevcontainerVolumeInfo>
            {
                new DevcontainerVolumeInfo
                {
                    Name = "devcontainer-test-abc12345",
                    ProjectName = "test",
                    Created = DateTime.UtcNow.AddDays(-1),
                    Labels = new Dictionary<string, string>
                    {
                        { "pks.managed", "true" },
                        { "devcontainer.project", "test" }
                    }
                }
            });

        // Setup SpawnRemoteAsync - throws NotImplementedException
        mock.Setup(x => x.SpawnRemoteAsync(It.IsAny<DevcontainerSpawnOptions>(), It.IsAny<RemoteHostConfig>()))
            .ThrowsAsync(new NotImplementedException("Remote spawning will be implemented in Phase 2"));

        return mock;
    }
}