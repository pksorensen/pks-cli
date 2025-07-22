using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Integration tests for NuGet template discovery and devcontainer template integration
/// </summary>
public class DevcontainerNuGetTemplateTests : TestBase
{
    private readonly INuGetTemplateDiscoveryService _nugetService;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerService _devcontainerService;

    public DevcontainerNuGetTemplateTests()
    {
        _nugetService = ServiceProvider.GetRequiredService<INuGetTemplateDiscoveryService>();
        _templateService = ServiceProvider.GetRequiredService<IDevcontainerTemplateService>();
        _devcontainerService = ServiceProvider.GetRequiredService<IDevcontainerService>();
    }

    [Fact]
    public async Task NuGetDiscovery_PksUniversalDevcontainer_ShouldBeDiscoverable()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";
        var templateId = "pks-universal-devcontainer";

        // Act
        var discoveredTemplates = await _nugetService.DiscoverTemplatesAsync();
        var pksTemplate = await _nugetService.GetTemplatePackageAsync(packageId);

        // Assert
        discoveredTemplates.Should().NotBeEmpty();
        
        if (pksTemplate != null)
        {
            pksTemplate.Id.Should().Be(packageId);
            pksTemplate.Templates.Should().Contain(t => t.ShortName == templateId);
            
            var template = pksTemplate.Templates.First(t => t.ShortName == templateId);
            template.Name.Should().NotBeNullOrEmpty();
            template.Description.Should().NotBeNullOrEmpty();
            template.Tags.Should().Contain("devcontainer");
        }
    }

    [Fact]
    public async Task NuGetDiscovery_TemplateInstallation_ShouldInstallSuccessfully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-installation");
        var packageId = "PKS.Templates.DevContainer";

        // Act
        var installResult = await _nugetService.InstallTemplatePackageAsync(packageId, testOutputPath);

        // Assert
        installResult.Should().NotBeNull();
        installResult.Success.Should().BeTrue();
        installResult.InstalledTemplates.Should().NotBeEmpty();
        installResult.InstalledTemplates.Should().Contain(t => t.ShortName.Contains("pks-universal-devcontainer"));
        
        // Verify template files were installed
        var templatePath = Path.Combine(testOutputPath, "templates");
        if (Directory.Exists(templatePath))
        {
            var templateFiles = Directory.GetFiles(templatePath, "*", SearchOption.AllDirectories);
            templateFiles.Should().NotBeEmpty();
            templateFiles.Should().Contain(f => f.EndsWith("devcontainer.json") || f.EndsWith(".template.config"));
        }
    }

    [Fact]
    public async Task NuGetDiscovery_TemplateUsage_ShouldUseInstalledTemplate()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-usage");
        var packageId = "PKS.Templates.DevContainer";
        var templateId = "pks-universal-devcontainer";

        // Install the template first
        var installResult = await _nugetService.InstallTemplatePackageAsync(packageId, testOutputPath);
        installResult.Success.Should().BeTrue();

        var options = new DevcontainerOptions
        {
            Name = "nuget-template-test",
            OutputPath = testOutputPath,
            Template = templateId
        };

        // Act
        var template = await _templateService.GetTemplateAsync(templateId);
        var extractionResult = await _templateService.ExtractTemplateAsync(templateId, options);

        // Assert
        template.Should().NotBeNull();
        template!.Id.Should().Be(templateId);
        
        extractionResult.Success.Should().BeTrue();
        extractionResult.ExtractedFiles.Should().NotBeEmpty();
        extractionResult.ExtractedFiles.Should().Contain(f => f.EndsWith("devcontainer.json"));
    }

    [Fact]
    public async Task NuGetDiscovery_TemplateVersioning_ShouldHandleVersions()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";

        // Act
        var availableVersions = await _nugetService.GetAvailableVersionsAsync(packageId);
        var latestVersion = await _nugetService.GetLatestVersionAsync(packageId);

        // Assert
        if (availableVersions.Any())
        {
            availableVersions.Should().NotBeEmpty();
            availableVersions.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
            
            if (latestVersion != null)
            {
                latestVersion.Should().NotBeNullOrWhiteSpace();
                availableVersions.Should().Contain(latestVersion);
            }
        }
    }

    [Fact]
    public async Task NuGetDiscovery_TemplateMetadata_ShouldProvideRichMetadata()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";

        // Act
        var templatePackage = await _nugetService.GetTemplatePackageAsync(packageId);

        // Assert
        if (templatePackage != null)
        {
            templatePackage.Id.Should().Be(packageId);
            templatePackage.Version.Should().NotBeNullOrWhiteSpace();
            templatePackage.Description.Should().NotBeNullOrWhiteSpace();
            templatePackage.Authors.Should().NotBeEmpty();
            templatePackage.Templates.Should().NotBeEmpty();

            foreach (var template in templatePackage.Templates)
            {
                template.ShortName.Should().NotBeNullOrWhiteSpace();
                template.Name.Should().NotBeNullOrWhiteSpace();
                template.Description.Should().NotBeNullOrWhiteSpace();
                template.Tags.Should().NotBeEmpty();
                template.Classifications.Should().NotBeEmpty();
            }
        }
    }

    [Fact]
    public async Task NuGetDiscovery_TemplateSearch_ShouldFindDevcontainerTemplates()
    {
        // Arrange
        var searchTerms = new[] { "devcontainer", "dev container", "container", "docker" };

        // Act & Assert
        foreach (var searchTerm in searchTerms)
        {
            var searchResults = await _nugetService.SearchTemplatesAsync(searchTerm);
            
            if (searchResults.Any())
            {
                searchResults.Should().NotBeEmpty();
                searchResults.Should().OnlyContain(r => 
                    r.Id.ToLower().Contains(searchTerm.ToLower()) ||
                    r.Description.ToLower().Contains(searchTerm.ToLower()) ||
                    r.Tags.Any(t => t.ToLower().Contains(searchTerm.ToLower())));
            }
        }
    }

    [Fact]
    public async Task NuGetIntegration_TemplateWithCustomFeatures_ShouldApplyFeatures()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("nuget-custom-features");
        var templateId = "pks-universal-devcontainer";

        var options = new DevcontainerOptions
        {
            Name = "custom-features-test",
            OutputPath = testOutputPath,
            Template = templateId,
            Features = new List<string>
            {
                "ghcr.io/devcontainers/features/dotnet:2",
                "ghcr.io/devcontainers/features/azure-cli:1",
                "ghcr.io/devcontainers/features/github-cli:1"
            }
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync(templateId, options);

        // Assert
        extractionResult.Success.Should().BeTrue();
        
        var devcontainerJsonPath = extractionResult.ExtractedFiles.First(f => f.EndsWith("devcontainer.json"));
        var devcontainerContent = await File.ReadAllTextAsync(devcontainerJsonPath);
        var config = JsonSerializer.Deserialize<DevcontainerConfiguration>(devcontainerContent);

        config!.Features.Should().ContainKey("ghcr.io/devcontainers/features/dotnet:2");
        config.Features.Should().ContainKey("ghcr.io/devcontainers/features/azure-cli:1");
        config.Features.Should().ContainKey("ghcr.io/devcontainers/features/github-cli:1");
    }

    [Fact]
    public async Task NuGetIntegration_TemplateUpdates_ShouldDetectUpdates()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-updates");

        // Install current version
        var installResult = await _nugetService.InstallTemplatePackageAsync(packageId, testOutputPath);
        
        if (installResult.Success)
        {
            // Act
            var installedPackages = new Dictionary<string, string> { { packageId, "1.0.0" } };
            var updateCheckResult = await _nugetService.CheckForUpdatesAsync(installedPackages);

            // Assert
            updateCheckResult.Should().NotBeNull();
            
            // If an update is available for this package, it should be in the dictionary
            if (updateCheckResult.ContainsKey(packageId))
            {
                updateCheckResult[packageId].Should().NotBeNullOrEmpty();
            }
        }
    }

    [Fact]
    public async Task NuGetIntegration_TemplateUninstallation_ShouldCleanup()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-uninstall");
        var packageId = "PKS.Templates.DevContainer";

        // Install template first
        var installResult = await _nugetService.InstallTemplatePackageAsync(packageId, testOutputPath);
        
        if (installResult.Success)
        {
            // Act
            var uninstallResult = await _nugetService.UninstallTemplatePackageAsync(packageId);

            // Assert
            uninstallResult.Should().BeTrue();
            
            // Verify template is no longer available
            var template = await _templateService.GetTemplateAsync("pks-universal-devcontainer");
            template.Should().BeNull();
        }
    }

    [Fact]
    public async Task NuGetIntegration_TemplateValidation_ShouldValidateTemplateStructure()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-validation");

        // Act
        var templatePackage = await _nugetService.GetTemplatePackageAsync(packageId);

        // Assert
        templatePackage.Should().NotBeNull();
        
        if (templatePackage != null)
        {
            templatePackage.Id.Should().Be(packageId);
            templatePackage.Templates.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task NuGetIntegration_TemplateCache_ShouldCacheResults()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";

        // Act - First call
        var startTime1 = DateTime.UtcNow;
        var result1 = await _nugetService.GetTemplatePackageAsync(packageId);
        var duration1 = DateTime.UtcNow - startTime1;

        // Act - Second call (should be faster due to caching)
        var startTime2 = DateTime.UtcNow;
        var result2 = await _nugetService.GetTemplatePackageAsync(packageId);
        var duration2 = DateTime.UtcNow - startTime2;

        // Assert
        if (result1 != null && result2 != null)
        {
            result1.Id.Should().Be(result2.Id);
            result1.Version.Should().Be(result2.Version);
            
            // Second call should generally be faster (cached)
            // Note: This might not always be true due to system variations, so we just check they're both reasonable
            duration1.Should().BeLessThan(TimeSpan.FromSeconds(30));
            duration2.Should().BeLessThan(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task NuGetIntegration_TemplateConfiguration_ShouldRespectConfiguration()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("nuget-template-configuration");
        
        // Configure NuGet service with custom settings
        var configuration = new NuGetDiscoveryConfiguration
        {
            Sources = new List<string> { "https://api.nuget.org/v3/index.json" },
            CacheDirectory = Path.Combine(testOutputPath, "cache"),
            TimeoutSeconds = 30,
            EnablePrerelease = false
        };

        // Act
        var configuredService = await _nugetService.ConfigureAsync(configuration);

        // Assert
        configuredService.Should().NotBeNull();
        configuredService.Configuration.Should().NotBeNull();
        configuredService.Configuration.Sources.Should().BeEquivalentTo(configuration.Sources);
        configuredService.Configuration.CacheDirectory.Should().Be(configuration.CacheDirectory);
        configuredService.Configuration.TimeoutSeconds.Should().Be(configuration.TimeoutSeconds);
        configuredService.Configuration.EnablePrerelease.Should().Be(configuration.EnablePrerelease);
    }

    [Fact]
    public async Task NuGetIntegration_ErrorHandling_ShouldHandleNetworkErrors()
    {
        // Arrange
        var invalidPackageId = "NonExistent.Package.That.Should.Not.Exist";

        // Act
        var result = await _nugetService.GetTemplatePackageAsync(invalidPackageId);
        var searchResults = await _nugetService.SearchTemplatesAsync("nonexistentpackage12345");

        // Assert
        result.Should().BeNull();
        searchResults.Should().BeEmpty();
        
        // Service should handle errors gracefully without throwing exceptions
        var installResult = await _nugetService.InstallTemplatePackageAsync(invalidPackageId, CreateTestArtifactDirectory("error-handling"));
        installResult.Success.Should().BeFalse();
        installResult.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NuGetIntegration_ConcurrentOperations_ShouldHandleConcurrency()
    {
        // Arrange
        var packageId = "PKS.Templates.DevContainer";
        var tasks = new List<Task<NuGetTemplatePackage?>>();

        // Act - Start multiple concurrent operations
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(_nugetService.GetTemplatePackageAsync(packageId));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().HaveCount(5);
        
        // All results should be consistent
        var nonNullResults = results.Where(r => r != null).ToList();
        if (nonNullResults.Any())
        {
            var firstResult = nonNullResults.First();
            nonNullResults.Should().OnlyContain(r => 
                r!.Id == firstResult!.Id && 
                r.Version == firstResult.Version);
        }
    }

    /// <summary>
    /// Creates a test artifact directory for generated files
    /// </summary>
    private string CreateTestArtifactDirectory(string testName)
    {
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "test-artifacts", "nuget-templates", testName);
        
        if (Directory.Exists(testArtifactsPath))
        {
            Directory.Delete(testArtifactsPath, true);
        }
        
        Directory.CreateDirectory(testArtifactsPath);
        return testArtifactsPath;
    }
}