using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Spectre.Console;

namespace PKS.Infrastructure.Initializers.Implementations;

/// <summary>
/// Initializer for creating devcontainer configuration integrated with project setup
/// </summary>
public class DevcontainerInitializer : CodeInitializer
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerFeatureRegistry _featureRegistry;

    public DevcontainerInitializer(
        IDevcontainerService devcontainerService,
        IDevcontainerFeatureRegistry featureRegistry)
    {
        _devcontainerService = devcontainerService;
        _featureRegistry = featureRegistry;
    }

    public override string Id => "devcontainer";
    public override string Name => "Devcontainer Configuration";
    public override string Description => "Creates devcontainer configuration for isolated development environments";
    public override int Order => 20; // Run after basic project structure (DotNetProjectInitializer = 10) but before features

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            InitializerOption.Flag("devcontainer", "Enable devcontainer configuration", "dc"),
            InitializerOption.StringArray("devcontainer-features", "Comma-separated list of devcontainer features to include", "dcf"),
            InitializerOption.String("devcontainer-template", "Devcontainer template to use", "dct"),
            InitializerOption.Flag("devcontainer-compose", "Use Docker Compose for devcontainer", "dcc"),
            InitializerOption.String("devcontainer-image", "Base Docker image for devcontainer", "dci"),
            InitializerOption.StringArray("devcontainer-ports", "Comma-separated list of ports to forward", "dcp"),
            InitializerOption.String("devcontainer-post-create", "Command to run after container creation", "dcpc")
        };
    }

    public override Task<bool> ShouldRunAsync(InitializationContext context)
    {
        // Run if devcontainer is explicitly enabled
        var enabled = context.GetOption("devcontainer", false);
        
        // Auto-enable for certain templates that benefit from devcontainers
        if (!enabled)
        {
            var template = context.Template?.ToLowerInvariant();
            enabled = template switch
            {
                "api" => true,
                "web" => true,
                "agent" => true,
                "agentic" => true,
                _ => false
            };
        }

        return Task.FromResult(enabled);
    }

    protected override async Task ExecuteCodeLogicAsync(InitializationContext context, InitializationResult result)
    {
        try
        {
            // Prepare devcontainer options based on context
            var devcontainerOptions = await PrepareDevcontainerOptionsAsync(context);
            
            // Create devcontainer configuration
            AnsiConsole.MarkupLine("[cyan]Creating devcontainer configuration...[/]");
            var devcontainerResult = await _devcontainerService.CreateConfigurationAsync(devcontainerOptions);
            
            if (!devcontainerResult.Success)
            {
                var errorMsg = $"Failed to create devcontainer configuration: {devcontainerResult.Message}";
                result.Errors.Add(errorMsg);
                
                // Add individual errors from the devcontainer service
                result.Errors.AddRange(devcontainerResult.Errors);
                
                AnsiConsole.MarkupLine($"[red]✗[/] {errorMsg}");
                return;
            }

            // Add generated files to result
            foreach (var file in devcontainerResult.GeneratedFiles)
            {
                result.AffectedFiles.Add(file);
            }

            // Add warnings from devcontainer creation
            result.Warnings.AddRange(devcontainerResult.Warnings);
            
            // Store devcontainer info in result data
            result.Data["devcontainer_enabled"] = "true";
            result.Data["devcontainer_features"] = string.Join(", ", devcontainerOptions.Features);
            result.Data["devcontainer_template"] = devcontainerOptions.Template ?? "custom";
            
            result.Warnings.Add("Remember to install the Dev Containers extension in VS Code");
            result.Warnings.Add("Use 'Dev Containers: Reopen in Container' command to start development");

            AnsiConsole.MarkupLine($"[green]✓[/] Devcontainer configuration created ({devcontainerResult.GeneratedFiles.Count} files)");
        }
        catch (Exception ex)
        {
            var errorMsg = $"Devcontainer initialization failed: {ex.Message}";
            result.Errors.Add(errorMsg);
            AnsiConsole.MarkupLine($"[red]✗[/] {errorMsg}");
        }
    }

    private async Task<DevcontainerOptions> PrepareDevcontainerOptionsAsync(InitializationContext context)
    {
        var options = new DevcontainerOptions
        {
            Name = context.ProjectName,
            OutputPath = Path.Combine(context.TargetDirectory, ".devcontainer"),
            Interactive = context.Interactive
        };

        // Set template based on project template or explicit option
        var explicitTemplate = context.GetOption<string>("devcontainer-template");
        if (!string.IsNullOrEmpty(explicitTemplate))
        {
            options.Template = explicitTemplate;
        }
        else
        {
            // Map project template to devcontainer template
            options.Template = context.Template?.ToLowerInvariant() switch
            {
                "api" => "dotnet-web", // Use dotnet-web for API projects as it has similar setup
                "web" => "dotnet-web",
                "console" => "dotnet-basic",
                "agent" => "dotnet-basic", // Use basic for agent projects
                "agentic" => "dotnet-basic",
                _ => "dotnet-basic"
            };
        }

        // Configure features
        var explicitFeatures = context.GetOption<string[]>("devcontainer-features");
        if (explicitFeatures?.Length > 0)
        {
            // Convert simple feature names to full feature identifiers
            foreach (var feature in explicitFeatures)
            {
                var featureId = ConvertToFeatureId(feature);
                if (!string.IsNullOrEmpty(featureId))
                {
                    options.Features.Add(featureId);
                }
            }
        }
        else
        {
            // Auto-select features based on project template and options
            await AddDefaultFeaturesAsync(options, context);
        }

        // Configure Docker Compose if requested
        options.UseDockerCompose = context.GetOption("devcontainer-compose", false);

        // Set base image if specified
        var baseImage = context.GetOption<string>("devcontainer-image");
        if (!string.IsNullOrEmpty(baseImage))
        {
            options.BaseImage = baseImage;
        }

        // Configure port forwarding
        var ports = context.GetOption<string[]>("devcontainer-ports");
        if (ports?.Length > 0)
        {
            foreach (var port in ports)
            {
                if (int.TryParse(port, out var portNumber))
                {
                    options.ForwardPorts.Add(portNumber);
                }
            }
        }
        else
        {
            // Add default ports based on template
            AddDefaultPorts(options, context.Template);
        }

        // Set post-create command
        var postCreateCommand = context.GetOption<string>("devcontainer-post-create");
        if (!string.IsNullOrEmpty(postCreateCommand))
        {
            options.PostCreateCommand = postCreateCommand;
        }
        else
        {
            // Set default post-create command based on template
            options.PostCreateCommand = GetDefaultPostCreateCommand(context);
        }

        // Add environment variables
        options.EnvironmentVariables["DOTNET_USE_POLLING_FILE_WATCHER"] = "true";
        options.EnvironmentVariables["NUGET_FALLBACK_PACKAGES"] = "/usr/share/dotnet/sdk/NuGetFallbackFolder";

        return options;
    }

    private Task AddDefaultFeaturesAsync(DevcontainerOptions options, InitializationContext context)
    {
        // Always include .NET feature
        options.Features.Add("ghcr.io/devcontainers/features/dotnet:2");

        // Add Git feature
        options.Features.Add("ghcr.io/devcontainers/features/git:1");

        // Add features based on template and other options
        switch (context.Template?.ToLowerInvariant())
        {
            case "api":
                options.Features.Add("ghcr.io/devcontainers/features/docker-in-docker:2");
                break;
                
            case "web":
                options.Features.Add("ghcr.io/devcontainers/features/node:1");
                options.Features.Add("ghcr.io/devcontainers/features/docker-in-docker:2");
                break;
                
            case "agent":
            case "agentic":
                options.Features.Add("ghcr.io/devcontainers/features/docker-in-docker:2");
                options.Features.Add("ghcr.io/devcontainers/features/python:1");
                break;
        }

        // Add MCP features if MCP is enabled
        if (context.GetOption("mcp", false))
        {
            options.Features.Add("ghcr.io/devcontainers/features/python:1");
        }

        // Add GitHub CLI if GitHub integration is used
        if (context.GetOption("github", false))
        {
            options.Features.Add("ghcr.io/devcontainers/features/github-cli:1");
        }
        
        return Task.CompletedTask;
    }

    private void AddDefaultPorts(DevcontainerOptions options, string? template)
    {
        switch (template?.ToLowerInvariant())
        {
            case "api":
                options.ForwardPorts.Add(5000); // HTTP
                options.ForwardPorts.Add(5001); // HTTPS
                break;
                
            case "web":
                options.ForwardPorts.Add(5000); // HTTP
                options.ForwardPorts.Add(5001); // HTTPS
                options.ForwardPorts.Add(3000); // Potential frontend dev server
                break;
                
            case "agent":
            case "agentic":
                options.ForwardPorts.Add(8080); // MCP server port
                options.ForwardPorts.Add(5000); // API port
                break;
        }
    }

    private string GetDefaultPostCreateCommand(InitializationContext context)
    {
        var commands = new List<string>();

        // Always restore .NET packages
        commands.Add("dotnet restore");

        // Build the project
        commands.Add("dotnet build");

        // Install global tools if needed
        if (context.GetOption("agentic", false))
        {
            commands.Add("dotnet tool restore");
        }

        return string.Join(" && ", commands);
    }

    private string ConvertToFeatureId(string feature)
    {
        // Convert simple feature names to full devcontainer feature identifiers
        return feature.ToLowerInvariant() switch
        {
            "dotnet" => "ghcr.io/devcontainers/features/dotnet:2",
            "docker" or "docker-in-docker" => "ghcr.io/devcontainers/features/docker-in-docker:2",
            "node" or "nodejs" => "ghcr.io/devcontainers/features/node:1",
            "python" => "ghcr.io/devcontainers/features/python:1",
            "git" => "ghcr.io/devcontainers/features/git:1",
            "azure-cli" => "ghcr.io/devcontainers/features/azure-cli:1",
            "kubectl" => "ghcr.io/devcontainers/features/kubectl-helm-minikube:1",
            "github-cli" => "ghcr.io/devcontainers/features/github-cli:1",
            // If it's already a full feature ID, return as is
            _ when feature.StartsWith("ghcr.io/") => feature,
            // Unknown feature
            _ => $"ghcr.io/devcontainers/features/{feature}:1"
        };
    }
}