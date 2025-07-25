using Moq;
using Microsoft.Extensions.Logging;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Linq;

namespace PKS.CLI.Tests.Infrastructure.Mocks;

/// <summary>
/// Mock factory for devcontainer-related services
/// </summary>
public static class DevcontainerServiceMocks
{
    /// <summary>
    /// Creates a mock IDevcontainerService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IDevcontainerService> CreateDevcontainerService()
    {
        var mock = new Mock<PKS.Infrastructure.Services.IDevcontainerService>();

        // Setup successful configuration creation
        mock.Setup(x => x.CreateConfigurationAsync(It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync((DevcontainerOptions options) => new DevcontainerResult
            {
                Success = true,
                Message = "Devcontainer configuration created successfully",
                Configuration = DevcontainerTestData.GetBasicConfiguration(),
                GeneratedFiles = new List<string>
                {
                    ".devcontainer/devcontainer.json",
                    ".devcontainer/Dockerfile"
                }
            });

        // Setup validation
        mock.Setup(x => x.ValidateConfigurationAsync(It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync((DevcontainerConfiguration config) => new DevcontainerValidationResult
            {
                IsValid = !string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(config.Image),
                Errors = new List<string>()
            });

        // Setup feature resolution
        mock.Setup(x => x.ResolveFeatureDependenciesAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> features) => new FeatureResolutionResult
            {
                Success = true,
                ResolvedFeatures = features.Select(f => DevcontainerTestData.GetAvailableFeatures()
                    .FirstOrDefault(af => af.Id == f) ?? new DevcontainerFeature { Id = f }).ToList(),
                ConflictingFeatures = new List<FeatureConflict>()
            });

        // Setup configuration merging
        mock.Setup(x => x.MergeConfigurationsAsync(It.IsAny<DevcontainerConfiguration>(), It.IsAny<DevcontainerConfiguration>()))
            .ReturnsAsync((DevcontainerConfiguration base_, DevcontainerConfiguration overlay) =>
            {
                var merged = new DevcontainerConfiguration
                {
                    Name = !string.IsNullOrEmpty(overlay.Name) ? overlay.Name : base_.Name,
                    Image = !string.IsNullOrEmpty(overlay.Image) ? overlay.Image : base_.Image,
                    Features = base_.Features.Concat(overlay.Features).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                    Customizations = base_.Customizations.Concat(overlay.Customizations).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                };
                return merged;
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerFeatureRegistry with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IDevcontainerFeatureRegistry> CreateFeatureRegistry()
    {
        var mock = new Mock<PKS.Infrastructure.Services.IDevcontainerFeatureRegistry>();
        var features = DevcontainerTestData.GetAvailableFeatures();

        // Setup feature discovery
        mock.Setup(x => x.GetAvailableFeaturesAsync())
            .ReturnsAsync(features);

        mock.Setup(x => x.GetFeatureAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => features.FirstOrDefault(f => f.Id == id));

        mock.Setup(x => x.SearchFeaturesAsync(It.IsAny<string>()))
            .ReturnsAsync((string query) => features.Where(f =>
                f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList());

        mock.Setup(x => x.GetFeaturesByCategory(It.IsAny<string>()))
            .ReturnsAsync((string category) => features.Where(f => f.Category == category).ToList());

        // Setup feature validation
        mock.Setup(x => x.ValidateFeatureConfiguration(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync((string featureId, object config) => new FeatureValidationResult
            {
                IsValid = true,
                Errors = new List<string>()
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerTemplateService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IDevcontainerTemplateService> CreateTemplateService()
    {
        var mock = new Mock<PKS.Infrastructure.Services.IDevcontainerTemplateService>();

        // Setup template retrieval
        mock.Setup(x => x.GetAvailableTemplatesAsync())
            .ReturnsAsync(new List<DevcontainerTemplate>
            {
                new()
                {
                    Id = "dotnet-basic",
                    Name = ".NET Basic",
                    Description = "Basic .NET development environment",
                    Category = "runtime",
                    BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                    RequiredFeatures = new[] { "dotnet" },
                    OptionalFeatures = new[] { "docker-in-docker", "azure-cli" }
                },
                new()
                {
                    Id = "dotnet-web",
                    Name = ".NET Web API",
                    Description = "Complete .NET web development environment",
                    Category = "web",
                    BaseImage = "mcr.microsoft.com/dotnet/aspnet:8.0",
                    RequiredFeatures = new[] { "dotnet", "node" },
                    OptionalFeatures = new[] { "docker-in-docker", "azure-cli", "kubectl-helm-minikube" }
                }
            });

        mock.Setup(x => x.GetTemplateAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new DevcontainerTemplate
            {
                Id = id,
                Name = "Test Template",
                Description = "Test template for unit tests",
                Category = "test",
                BaseImage = "mcr.microsoft.com/dotnet/sdk:8.0",
                RequiredFeatures = new[] { "dotnet" }
            });

        // Setup template application
        mock.Setup(x => x.ApplyTemplateAsync(It.IsAny<string>(), It.IsAny<DevcontainerOptions>()))
            .ReturnsAsync((string templateId, DevcontainerOptions options) => new DevcontainerConfiguration
            {
                Name = options.Name,
                Image = "mcr.microsoft.com/dotnet/sdk:8.0",
                Features = new Dictionary<string, object>
                {
                    ["ghcr.io/devcontainers/features/dotnet:2"] = new { version = "8.0" }
                }
            });

        // Setup template extraction
        mock.Setup(x => x.ExtractTemplateAsync(It.IsAny<string>(), It.IsAny<DevcontainerOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string templateId, DevcontainerOptions options, CancellationToken cancellationToken) =>
            {
                // Create mock extracted files
                var devcontainerDir = Path.Combine(options.OutputPath, ".devcontainer");
                Directory.CreateDirectory(devcontainerDir);

                var devcontainerJsonPath = Path.Combine(devcontainerDir, "devcontainer.json");
                var dockerfilePath = Path.Combine(devcontainerDir, "Dockerfile");

                // Create mock files with placeholder replacement
                var devcontainerContent = $@"{{
    ""name"": ""{options.Name}"",
    ""build"": {{
        ""dockerfile"": ""Dockerfile""
    }},
    ""features"": {{
        ""ghcr.io/devcontainers/features/dotnet:2"": {{
            ""version"": ""8.0""
        }}
    }}
}}";

                File.WriteAllText(devcontainerJsonPath, devcontainerContent);
                File.WriteAllText(dockerfilePath, "FROM mcr.microsoft.com/dotnet/sdk:8.0\nWORKDIR /workspace");

                return new NuGetTemplateExtractionResult
                {
                    Success = true,
                    ExtractedFiles = new List<string> { devcontainerJsonPath, dockerfilePath },
                    Message = "Template extracted successfully"
                };
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerFileGenerator with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IDevcontainerFileGenerator> CreateFileGenerator()
    {
        var mock = new Mock<PKS.Infrastructure.Services.IDevcontainerFileGenerator>();

        // Setup file generation
        mock.Setup(x => x.GenerateDevcontainerJsonAsync(It.IsAny<DevcontainerConfiguration>(), It.IsAny<string>()))
            .ReturnsAsync((DevcontainerConfiguration config, string outputPath) => new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "devcontainer.json"),
                Content = DevcontainerTestData.GetDevcontainerJson()
            });

        mock.Setup(x => x.GenerateDockerfileAsync(It.IsAny<DevcontainerConfiguration>(), It.IsAny<string>()))
            .ReturnsAsync((DevcontainerConfiguration config, string outputPath) => new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "Dockerfile"),
                Content = DevcontainerTestData.GetDockerfile()
            });

        mock.Setup(x => x.GenerateDockerComposeAsync(It.IsAny<DevcontainerConfiguration>(), It.IsAny<string>()))
            .ReturnsAsync((DevcontainerConfiguration config, string outputPath) => new FileGenerationResult
            {
                Success = true,
                FilePath = Path.Combine(outputPath, ".devcontainer", "docker-compose.yml"),
                Content = DevcontainerTestData.GetDockerComposeYml()
            });

        // Setup validation
        mock.Setup(x => x.ValidateOutputPathAsync(It.IsAny<string>()))
            .ReturnsAsync((string path) =>
            {
                var isReadOnlyPath = path.StartsWith("/readonly") || path.Contains("readonly");
                return new PathValidationResult
                {
                    IsValid = !isReadOnlyPath && Directory.Exists(Path.GetDirectoryName(path)!),
                    CanWrite = !isReadOnlyPath,
                    Errors = isReadOnlyPath ? new List<string> { "Path is read-only" } : new List<string>()
                };
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IVsCodeExtensionService with default behavior
    /// </summary>
    public static Mock<PKS.Infrastructure.Services.IVsCodeExtensionService> CreateVsCodeExtensionService()
    {
        var mock = new Mock<PKS.Infrastructure.Services.IVsCodeExtensionService>();
        var extensions = DevcontainerTestData.GetVsCodeExtensions();

        // Setup extension discovery
        mock.Setup(x => x.GetRecommendedExtensionsAsync(It.IsAny<string[]>()))
            .ReturnsAsync((string[] categories) => extensions.Where(e =>
                categories.Contains(e.Category)).ToList());

        mock.Setup(x => x.SearchExtensionsAsync(It.IsAny<string>()))
            .ReturnsAsync((string query) => extensions.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());

        mock.Setup(x => x.ValidateExtensionAsync(It.IsAny<string>()))
            .ReturnsAsync((string extensionId) => new ExtensionValidationResult
            {
                IsValid = extensions.Any(e => e.Id == extensionId),
                Exists = true,
                IsCompatible = true
            });

        return mock;
    }
}