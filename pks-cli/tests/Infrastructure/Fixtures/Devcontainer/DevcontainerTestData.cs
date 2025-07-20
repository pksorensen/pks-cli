using System.Text.Json;

namespace PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;

/// <summary>
/// Test data generator for devcontainer configurations and scenarios
/// </summary>
public static class DevcontainerTestData
{
    /// <summary>
    /// Gets a basic devcontainer configuration for testing
    /// </summary>
    public static DevcontainerConfiguration GetBasicConfiguration()
    {
        return new DevcontainerConfiguration
        {
            Name = "test-devcontainer",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            Features = new Dictionary<string, object>
            {
                ["ghcr.io/devcontainers/features/dotnet:2"] = new { version = "8.0" }
            },
            Customizations = new Dictionary<string, object>
            {
                ["vscode"] = new
                {
                    extensions = new[] { "ms-dotnettools.csharp", "ms-dotnettools.vscode-dotnet-runtime" }
                }
            },
            ForwardPorts = new[] { 5000, 5001 },
            PostCreateCommand = "dotnet restore"
        };
    }

    /// <summary>
    /// Gets a complex devcontainer configuration for testing
    /// </summary>
    public static DevcontainerConfiguration GetComplexConfiguration()
    {
        return new DevcontainerConfiguration
        {
            Name = "complex-devcontainer",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0",
            Features = new Dictionary<string, object>
            {
                ["ghcr.io/devcontainers/features/dotnet:2"] = new { version = "8.0" },
                ["ghcr.io/devcontainers/features/docker-in-docker:2"] = new { version = "latest" },
                ["ghcr.io/devcontainers/features/azure-cli:1"] = new { version = "latest" },
                ["ghcr.io/devcontainers/features/kubectl-helm-minikube:1"] = new { version = "latest" },
                ["ghcr.io/devcontainers/features/node:1"] = new { version = "20" }
            },
            Customizations = new Dictionary<string, object>
            {
                ["vscode"] = new
                {
                    extensions = new[]
                    {
                        "ms-dotnettools.csharp",
                        "ms-dotnettools.vscode-dotnet-runtime",
                        "ms-vscode.vscode-docker",
                        "ms-kubernetes-tools.vscode-kubernetes-tools",
                        "ms-azuretools.vscode-azure-account"
                    },
                    settings = new
                    {
                        dotnetCoreCliTelemetryOptOut = true,
                        filesWatcherExclude = new { binFolder = true, objFolder = true }
                    }
                }
            },
            ForwardPorts = new[] { 5000, 5001, 3000, 8080 },
            PostCreateCommand = "dotnet restore && npm install",
            Mounts = new[]
            {
                "source=/var/run/docker.sock,target=/var/run/docker.sock,type=bind"
            },
            RemoteEnv = new Dictionary<string, string>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }
        };
    }

    /// <summary>
    /// Gets a list of available devcontainer features for testing
    /// </summary>
    public static List<DevcontainerFeature> GetAvailableFeatures()
    {
        return new List<DevcontainerFeature>
        {
            new()
            {
                Id = "dotnet",
                Name = ".NET",
                Description = "Installs .NET SDK and runtime",
                Version = "2",
                Repository = "ghcr.io/devcontainers/features/dotnet",
                Category = "runtime",
                Tags = new[] { "dotnet", "csharp", "runtime" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/dotnet",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "8.0"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = ".NET version to install",
                        Default = "8.0",
                        Enum = new[] { "6.0", "7.0", "8.0", "latest" }
                    }
                }
            },
            new()
            {
                Id = "docker-in-docker",
                Name = "Docker in Docker",
                Description = "Enables Docker inside the container",
                Version = "2",
                Repository = "ghcr.io/devcontainers/features/docker-in-docker",
                Category = "tool",
                Tags = new[] { "docker", "container" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/docker-in-docker",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest",
                    ["moby"] = true
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Docker version",
                        Default = "latest"
                    },
                    ["moby"] = new()
                    {
                        Type = "boolean",
                        Description = "Install Moby CLI instead of Docker CLI",
                        Default = true
                    }
                }
            },
            new()
            {
                Id = "azure-cli",
                Name = "Azure CLI",
                Description = "Installs Azure CLI",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/azure-cli",
                Category = "cloud",
                Tags = new[] { "azure", "cli", "cloud" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/azure-cli",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Azure CLI version",
                        Default = "latest"
                    }
                }
            },
            new()
            {
                Id = "kubectl-helm-minikube",
                Name = "Kubernetes Tools",
                Description = "Installs kubectl, Helm, and Minikube",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/kubectl-helm-minikube",
                Category = "kubernetes",
                Tags = new[] { "kubernetes", "kubectl", "helm", "minikube" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/kubectl-helm-minikube",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "latest",
                    ["helm"] = "latest",
                    ["minikube"] = "latest"
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "kubectl version",
                        Default = "latest"
                    },
                    ["helm"] = new()
                    {
                        Type = "string",
                        Description = "Helm version",
                        Default = "latest"
                    },
                    ["minikube"] = new()
                    {
                        Type = "string",
                        Description = "Minikube version",
                        Default = "latest"
                    }
                }
            },
            new()
            {
                Id = "node",
                Name = "Node.js",
                Description = "Installs Node.js and npm",
                Version = "1",
                Repository = "ghcr.io/devcontainers/features/node",
                Category = "runtime",
                Tags = new[] { "node", "npm", "javascript", "typescript" },
                Documentation = "https://github.com/devcontainers/features/tree/main/src/node",
                DefaultOptions = new Dictionary<string, object>
                {
                    ["version"] = "lts",
                    ["nodeGypDependencies"] = true
                },
                AvailableOptions = new Dictionary<string, DevcontainerFeatureOption>
                {
                    ["version"] = new()
                    {
                        Type = "string",
                        Description = "Node.js version",
                        Default = "lts",
                        Enum = new[] { "lts", "18", "19", "20", "latest" }
                    },
                    ["nodeGypDependencies"] = new()
                    {
                        Type = "boolean",
                        Description = "Install dependencies for node-gyp",
                        Default = true
                    }
                }
            }
        };
    }

    /// <summary>
    /// Gets a list of VS Code extensions for testing
    /// </summary>
    public static List<VsCodeExtension> GetVsCodeExtensions()
    {
        return new List<VsCodeExtension>
        {
            new()
            {
                Id = "ms-dotnettools.csharp",
                Name = "C#",
                Publisher = "ms-dotnettools",
                Description = "C# for Visual Studio Code",
                Category = "language",
                Tags = new[] { "csharp", "dotnet", "language" }
            },
            new()
            {
                Id = "ms-dotnettools.vscode-dotnet-runtime",
                Name = ".NET Install Tool",
                Publisher = "ms-dotnettools",
                Description = "Installs and manages .NET runtimes and SDKs",
                Category = "runtime",
                Tags = new[] { "dotnet", "runtime", "sdk" }
            },
            new()
            {
                Id = "ms-vscode.vscode-docker",
                Name = "Docker",
                Publisher = "ms-vscode",
                Description = "Makes it easy to create, manage, and debug containerized applications",
                Category = "tool",
                Tags = new[] { "docker", "container" }
            },
            new()
            {
                Id = "ms-kubernetes-tools.vscode-kubernetes-tools",
                Name = "Kubernetes",
                Publisher = "ms-kubernetes-tools",
                Description = "Develop, deploy and debug Kubernetes applications",
                Category = "kubernetes",
                Tags = new[] { "kubernetes", "kubectl", "helm" }
            },
            new()
            {
                Id = "ms-azuretools.vscode-azure-account",
                Name = "Azure Account",
                Publisher = "ms-azuretools",
                Description = "A common Sign-In and Subscription management extension for VS Code",
                Category = "cloud",
                Tags = new[] { "azure", "cloud", "account" }
            }
        };
    }

    /// <summary>
    /// Gets sample JSON content for devcontainer.json
    /// </summary>
    public static string GetDevcontainerJson()
    {
        var config = GetBasicConfiguration();
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Gets sample docker-compose.yml content for devcontainer
    /// </summary>
    public static string GetDockerComposeYml()
    {
        return @"version: '3.8'

services:
  devcontainer:
    build:
      context: .
      dockerfile: Dockerfile
    volumes:
      - ../..:/workspaces:cached
    command: sleep infinity
    ports:
      - '5000:5000'
      - '5001:5001'
    environment:
      - DOTNET_CLI_TELEMETRY_OPTOUT=1
      - ASPNETCORE_ENVIRONMENT=Development

  database:
    image: postgres:15
    restart: unless-stopped
    volumes:
      - postgres-data:/var/lib/postgresql/data
    environment:
      POSTGRES_PASSWORD: postgres
      POSTGRES_USER: postgres
      POSTGRES_DB: testdb
    ports:
      - '5432:5432'

volumes:
  postgres-data:
";
    }

    /// <summary>
    /// Gets sample Dockerfile content for devcontainer
    /// </summary>
    public static string GetDockerfile()
    {
        return @"FROM mcr.microsoft.com/dotnet/sdk:8.0

# Install additional tools
RUN apt-get update && apt-get install -y \
    git \
    curl \
    wget \
    unzip \
    && rm -rf /var/lib/apt/lists/*

# Install Node.js
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs

# Install Azure CLI
RUN curl -sL https://aka.ms/InstallAzureCLIDeb | bash

# Set working directory
WORKDIR /workspaces

# Configure git
RUN git config --global --add safe.directory /workspaces

# Install dotnet tools
RUN dotnet tool install -g dotnet-ef
RUN dotnet tool install -g dotnet-aspnet-codegenerator

ENV PATH=""$PATH:/root/.dotnet/tools""

CMD [""sleep"", ""infinity""]
";
    }

    /// <summary>
    /// Gets invalid devcontainer configuration for error testing
    /// </summary>
    public static DevcontainerConfiguration GetInvalidConfiguration()
    {
        return new DevcontainerConfiguration
        {
            Name = "", // Invalid empty name
            Image = "invalid-image-name!@#", // Invalid image name
            Features = new Dictionary<string, object>
            {
                [""] = new { }, // Invalid empty feature name
                ["invalid-feature"] = "invalid-config" // Invalid feature config
            }
        };
    }

    /// <summary>
    /// Gets validation test scenarios
    /// </summary>
    public static IEnumerable<object[]> GetValidationTestCases()
    {
        yield return new object[] { GetBasicConfiguration(), true, "" };
        yield return new object[] 
        { 
            new DevcontainerConfiguration { Name = "", Image = "test" }, 
            false, 
            "Name is required" 
        };
        yield return new object[] 
        { 
            new DevcontainerConfiguration { Name = "test", Image = "" }, 
            false, 
            "Image is required" 
        };
        yield return new object[] 
        { 
            new DevcontainerConfiguration 
            { 
                Name = "test-with-invalid-chars!@#", 
                Image = "test" 
            }, 
            false, 
            "Name contains invalid characters" 
        };
    }
}

// Test model classes (these will match the actual models when implemented)
public class DevcontainerConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public Dictionary<string, object> Features { get; set; } = new();
    public Dictionary<string, object> Customizations { get; set; } = new();
    public int[] ForwardPorts { get; set; } = Array.Empty<int>();
    public string PostCreateCommand { get; set; } = string.Empty;
    public string[] Mounts { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> RemoteEnv { get; set; } = new();
}

public class DevcontainerFeature
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string Documentation { get; set; } = string.Empty;
    public Dictionary<string, object> DefaultOptions { get; set; } = new();
    public Dictionary<string, DevcontainerFeatureOption> AvailableOptions { get; set; } = new();
}

public class DevcontainerFeatureOption
{
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public object? Default { get; set; }
    public string[]? Enum { get; set; }
}

public class VsCodeExtension
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}