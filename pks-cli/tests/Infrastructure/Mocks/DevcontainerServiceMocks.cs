using Moq;
using Microsoft.Extensions.Logging;
using PKS.CLI.Tests.Infrastructure.Fixtures.Devcontainer;

namespace PKS.CLI.Tests.Infrastructure.Mocks;

/// <summary>
/// Mock factory for devcontainer-related services
/// </summary>
public static class DevcontainerServiceMocks
{
    /// <summary>
    /// Creates a mock IDevcontainerService with default behavior
    /// </summary>
    public static Mock<IDevcontainerService> CreateDevcontainerService()
    {
        var mock = new Mock<IDevcontainerService>();
        
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
                    Name = overlay.Name ?? base_.Name,
                    Image = overlay.Image ?? base_.Image,
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
    public static Mock<IDevcontainerFeatureRegistry> CreateFeatureRegistry()
    {
        var mock = new Mock<IDevcontainerFeatureRegistry>();
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
    public static Mock<IDevcontainerTemplateService> CreateTemplateService()
    {
        var mock = new Mock<IDevcontainerTemplateService>();

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

        return mock;
    }

    /// <summary>
    /// Creates a mock IDevcontainerFileGenerator with default behavior
    /// </summary>
    public static Mock<IDevcontainerFileGenerator> CreateFileGenerator()
    {
        var mock = new Mock<IDevcontainerFileGenerator>();

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
            .ReturnsAsync((string path) => new PathValidationResult
            {
                IsValid = Directory.Exists(Path.GetDirectoryName(path)!),
                CanWrite = true,
                Errors = new List<string>()
            });

        return mock;
    }

    /// <summary>
    /// Creates a mock IVsCodeExtensionService with default behavior
    /// </summary>
    public static Mock<IVsCodeExtensionService> CreateVsCodeExtensionService()
    {
        var mock = new Mock<IVsCodeExtensionService>();
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

// Placeholder interfaces for devcontainer services that will be implemented
public interface IDevcontainerService
{
    Task<DevcontainerResult> CreateConfigurationAsync(DevcontainerOptions options);
    Task<DevcontainerValidationResult> ValidateConfigurationAsync(DevcontainerConfiguration configuration);
    Task<FeatureResolutionResult> ResolveFeatureDependenciesAsync(List<string> features);
    Task<DevcontainerConfiguration> MergeConfigurationsAsync(DevcontainerConfiguration baseConfig, DevcontainerConfiguration overlayConfig);
}

public interface IDevcontainerFeatureRegistry
{
    Task<List<DevcontainerFeature>> GetAvailableFeaturesAsync();
    Task<DevcontainerFeature?> GetFeatureAsync(string id);
    Task<List<DevcontainerFeature>> SearchFeaturesAsync(string query);
    Task<List<DevcontainerFeature>> GetFeaturesByCategory(string category);
    Task<FeatureValidationResult> ValidateFeatureConfiguration(string featureId, object configuration);
}

public interface IDevcontainerTemplateService
{
    Task<List<DevcontainerTemplate>> GetAvailableTemplatesAsync();
    Task<DevcontainerTemplate?> GetTemplateAsync(string id);
    Task<DevcontainerConfiguration> ApplyTemplateAsync(string templateId, DevcontainerOptions options);
}

public interface IDevcontainerFileGenerator
{
    Task<FileGenerationResult> GenerateDevcontainerJsonAsync(DevcontainerConfiguration configuration, string outputPath);
    Task<FileGenerationResult> GenerateDockerfileAsync(DevcontainerConfiguration configuration, string outputPath);
    Task<FileGenerationResult> GenerateDockerComposeAsync(DevcontainerConfiguration configuration, string outputPath);
    Task<PathValidationResult> ValidateOutputPathAsync(string path);
}

public interface IVsCodeExtensionService
{
    Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(string[] categories);
    Task<List<VsCodeExtension>> SearchExtensionsAsync(string query);
    Task<ExtensionValidationResult> ValidateExtensionAsync(string extensionId);
}

// Result classes for testing
public class DevcontainerResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DevcontainerConfiguration? Configuration { get; set; }
    public List<string> GeneratedFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class DevcontainerValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class FeatureResolutionResult
{
    public bool Success { get; set; }
    public List<DevcontainerFeature> ResolvedFeatures { get; set; } = new();
    public List<FeatureConflict> ConflictingFeatures { get; set; } = new();
    public string ErrorMessage { get; set; } = string.Empty;
}

public class FeatureConflict
{
    public string Feature1 { get; set; } = string.Empty;
    public string Feature2 { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class FeatureValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class DevcontainerTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string BaseImage { get; set; } = string.Empty;
    public string[] RequiredFeatures { get; set; } = Array.Empty<string>();
    public string[] OptionalFeatures { get; set; } = Array.Empty<string>();
}

public class FileGenerationResult
{
    public bool Success { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}

public class PathValidationResult
{
    public bool IsValid { get; set; }
    public bool CanWrite { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class ExtensionValidationResult
{
    public bool IsValid { get; set; }
    public bool Exists { get; set; }
    public bool IsCompatible { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
}

public class DevcontainerOptions
{
    public string Name { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string? Template { get; set; }
    public List<string> Features { get; set; } = new();
    public List<string> Extensions { get; set; } = new();
    public bool UseDockerCompose { get; set; }
    public bool Interactive { get; set; }
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}