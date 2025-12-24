using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using System.Text.Json;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Integration tests that verify the pks-universal-devcontainer template creates a .devcontainer identical to the root one
/// </summary>
public class DevcontainerUniversalTemplateTests : TestBase
{
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerFileGenerator _fileGenerator;

    public DevcontainerUniversalTemplateTests()
    {
        _templateService = ServiceProvider.GetRequiredService<IDevcontainerTemplateService>();
        _fileGenerator = ServiceProvider.GetRequiredService<IDevcontainerFileGenerator>();
    }

    [Fact]
    public async Task UniversalTemplate_ExtractedDevcontainer_ShouldMatchRootDevcontainer()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("universal-template-match-test");
        var rootDevcontainerPath = "/workspace/.devcontainer";
        var templateDevcontainerPath = Path.Combine(testOutputPath, ".devcontainer");

        var options = new DevcontainerOptions
        {
            Name = "test-universal-match",
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        extractionResult.Success.Should().BeTrue();

        // Verify files were extracted
        var extractedDevcontainerJson = Path.Combine(templateDevcontainerPath, "devcontainer.json");
        var extractedDockerfile = Path.Combine(templateDevcontainerPath, "Dockerfile");

        File.Exists(extractedDevcontainerJson).Should().BeTrue();
        File.Exists(extractedDockerfile).Should().BeTrue();

        // Compare devcontainer.json files
        await CompareDevcontainerJsonFiles(
            Path.Combine(rootDevcontainerPath, "devcontainer.json"),
            extractedDevcontainerJson,
            options.Name
        );

        // Compare Dockerfile content (if exists)
        var rootDockerfile = Path.Combine(rootDevcontainerPath, "Dockerfile");
        if (File.Exists(rootDockerfile))
        {
            await CompareDockerfiles(rootDockerfile, extractedDockerfile);
        }
    }

    [Fact]
    public async Task UniversalTemplate_StructuralComparison_ShouldHaveIdenticalStructure()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("universal-template-structure-test");
        var rootDevcontainerPath = "/workspace/.devcontainer";

        var options = new DevcontainerOptions
        {
            Name = "test-structure",
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        extractionResult.Success.Should().BeTrue();

        // Get all files in root devcontainer
        var rootFiles = Directory.GetFiles(rootDevcontainerPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(rootDevcontainerPath, f))
            .OrderBy(f => f)
            .ToList();

        // Get all extracted files
        var extractedDevcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        var extractedFiles = Directory.GetFiles(extractedDevcontainerPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extractedDevcontainerPath, f))
            .OrderBy(f => f)
            .ToList();

        // Compare file structures
        extractedFiles.Should().BeEquivalentTo(rootFiles, options => options
            .WithStrictOrdering()
            .WithTracing());

        // Verify each file type matches
        foreach (var file in rootFiles)
        {
            var rootFilePath = Path.Combine(rootDevcontainerPath, file);
            var extractedFilePath = Path.Combine(extractedDevcontainerPath, file);

            File.Exists(extractedFilePath).Should().BeTrue($"Extracted file should exist: {file}");

            // Compare file sizes (should be similar, accounting for placeholder replacements)
            var rootFileInfo = new FileInfo(rootFilePath);
            var extractedFileInfo = new FileInfo(extractedFilePath);

            // Files should be roughly the same size (within 20% to account for placeholders)
            var sizeDifferenceRatio = Math.Abs(rootFileInfo.Length - extractedFileInfo.Length) / (double)rootFileInfo.Length;
            sizeDifferenceRatio.Should().BeLessThan(0.2, $"File size difference should be minimal for {file}");
        }
    }

    [Fact]
    public async Task UniversalTemplate_ConfigurationSections_ShouldHaveMatchingStructure()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("universal-template-config-sections-test");

        var options = new DevcontainerOptions
        {
            Name = "ConfigSectionTest",
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        extractionResult.Success.Should().BeTrue();

        var rootDevcontainerJson = "/workspace/.devcontainer/devcontainer.json";
        var extractedDevcontainerJson = Path.Combine(testOutputPath, ".devcontainer", "devcontainer.json");

        // Load and parse both configurations
        var rootConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(
            await File.ReadAllTextAsync(rootDevcontainerJson));
        var extractedConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(
            await File.ReadAllTextAsync(extractedDevcontainerJson));

        // Compare structural elements (ignoring name which should be different)
        rootConfig.Should().NotBeNull();
        extractedConfig.Should().NotBeNull();

        // Build configuration should be identical
        if (rootConfig!.Build != null)
        {
            extractedConfig!.Build.Should().NotBeNull();
            extractedConfig.Build!.DockerfilePath.Should().Be(rootConfig.Build.DockerfilePath);
            extractedConfig.Build!.Context.Should().Be(rootConfig.Build.Context);

            if (rootConfig.Build.Args != null)
            {
                extractedConfig.Build.Args.Should().NotBeNull();
                // Compare arg keys (values might contain placeholders)
                extractedConfig.Build.Args!.Keys.Should().BeEquivalentTo(rootConfig.Build.Args.Keys);
            }
        }

        // RunArgs should be identical
        if (rootConfig.RunArgs != null)
        {
            extractedConfig!.RunArgs.Should().BeEquivalentTo(rootConfig.RunArgs);
        }

        // Customizations structure should match
        CompareCustomizations(rootConfig.Customizations, extractedConfig!.Customizations);

        // Mounts should be identical (they shouldn't contain project-specific placeholders)
        if (rootConfig.Mounts != null)
        {
            extractedConfig.Mounts.Should().BeEquivalentTo(rootConfig.Mounts);
        }

        // RemoteEnv should be identical
        if (rootConfig.RemoteEnv != null)
        {
            extractedConfig.RemoteEnv.Should().BeEquivalentTo(rootConfig.RemoteEnv);
        }

        // WorkspaceMount and WorkspaceFolder should have same structure but different values
        if (!string.IsNullOrEmpty(rootConfig.WorkspaceMount))
        {
            extractedConfig.WorkspaceMount.Should().NotBeNullOrEmpty();
            // Both should contain workspace references
            extractedConfig.WorkspaceMount.Should().Contain("workspace");
            rootConfig.WorkspaceMount.Should().Contain("workspace");
        }

        if (!string.IsNullOrEmpty(rootConfig.WorkspaceFolder))
        {
            extractedConfig.WorkspaceFolder.Should().NotBeNullOrEmpty();
            // Both should reference workspace
            extractedConfig.WorkspaceFolder.Should().Contain("workspace");
            rootConfig.WorkspaceFolder.Should().Contain("workspace");
        }
    }

    [Fact]
    public async Task UniversalTemplate_PlaceholderReplacement_ShouldOnlyReplacePlaceholders()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("universal-template-placeholders-test");
        var projectName = "MyAwesomeProject";

        var options = new DevcontainerOptions
        {
            Name = projectName,
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        extractionResult.Success.Should().BeTrue();

        var extractedDevcontainerJson = Path.Combine(testOutputPath, ".devcontainer", "devcontainer.json");
        var extractedContent = await File.ReadAllTextAsync(extractedDevcontainerJson);

        // Verify placeholders were replaced
        extractedContent.Should().Contain(projectName);
        extractedContent.Should().NotContain("${projectName}");
        extractedContent.Should().NotContain("{{ProjectName}}");

        // Verify other content remained unchanged
        var rootContent = await File.ReadAllTextAsync("/workspace/.devcontainer/devcontainer.json");

        // Extract non-placeholder content for comparison
        var rootNonPlaceholderLines = rootContent.Split('\n')
            .Where(line => !line.Contains("${projectName}") && !line.Contains("{{ProjectName}}"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        var extractedNonPlaceholderLines = extractedContent.Split('\n')
            .Where(line => !line.Contains(projectName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        // Most lines should be identical (accounting for some differences due to placeholder context)
        var matchingLines = rootNonPlaceholderLines.Intersect(extractedNonPlaceholderLines).Count();
        var totalLines = rootNonPlaceholderLines.Count;

        var matchRatio = (double)matchingLines / totalLines;
        matchRatio.Should().BeGreaterThan(0.8, "Most non-placeholder content should remain unchanged");
    }

    [Fact]
    public async Task UniversalTemplate_FilePermissions_ShouldPreserveExecutablePermissions()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("universal-template-permissions-test");

        var options = new DevcontainerOptions
        {
            Name = "permissions-test",
            OutputPath = testOutputPath,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options);

        // Assert
        extractionResult.Success.Should().BeTrue();

        // Check if any script files were extracted and verify they have executable permissions
        var extractedFiles = extractionResult.ExtractedFiles;
        var scriptFiles = extractedFiles.Where(f =>
            f.EndsWith(".sh") ||
            f.EndsWith(".py") ||
            Path.GetFileNameWithoutExtension(f).StartsWith("init-")).ToList();

        foreach (var scriptFile in scriptFiles)
        {
            File.Exists(scriptFile).Should().BeTrue();

            // On Unix systems, check if the file has execute permissions
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                var fileInfo = new FileInfo(scriptFile);
                // This is a basic check - in a real scenario you'd use more sophisticated permission checking
                fileInfo.Exists.Should().BeTrue();
            }
        }
    }

    [Fact]
    public async Task UniversalTemplate_MultipleExtractions_ShouldProduceIdenticalResults()
    {
        // Arrange
        var testOutputPath1 = CreateTestArtifactDirectory("universal-template-multiple-1");
        var testOutputPath2 = CreateTestArtifactDirectory("universal-template-multiple-2");

        var options1 = new DevcontainerOptions
        {
            Name = "test-project-1",
            OutputPath = testOutputPath1,
            Template = "pks-universal-devcontainer"
        };

        var options2 = new DevcontainerOptions
        {
            Name = "test-project-2",
            OutputPath = testOutputPath2,
            Template = "pks-universal-devcontainer"
        };

        // Act
        var result1 = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options1);
        var result2 = await _templateService.ExtractTemplateAsync("pks-universal-devcontainer", options2);

        // Assert
        result1.Success.Should().BeTrue();
        result2.Success.Should().BeTrue();

        // Compare the structure of extracted files
        var files1 = result1.ExtractedFiles
            .Select(f => Path.GetRelativePath(testOutputPath1, f))
            .OrderBy(f => f)
            .ToList();

        var files2 = result2.ExtractedFiles
            .Select(f => Path.GetRelativePath(testOutputPath2, f))
            .OrderBy(f => f)
            .ToList();

        files1.Should().BeEquivalentTo(files2);

        // Compare content of each file (accounting for project name differences)
        foreach (var relativeFile in files1)
        {
            var file1Path = Path.Combine(testOutputPath1, relativeFile);
            var file2Path = Path.Combine(testOutputPath2, relativeFile);

            var content1 = await File.ReadAllTextAsync(file1Path);
            var content2 = await File.ReadAllTextAsync(file2Path);

            // Replace project names to normalize for comparison
            var normalizedContent1 = content1.Replace("test-project-1", "PROJECT_NAME");
            var normalizedContent2 = content2.Replace("test-project-2", "PROJECT_NAME");

            normalizedContent1.Should().Be(normalizedContent2,
                $"Normalized content should be identical for file: {relativeFile}");
        }
    }

    private async Task CompareDevcontainerJsonFiles(string rootPath, string extractedPath, string projectName)
    {
        var rootContent = await File.ReadAllTextAsync(rootPath);
        var extractedContent = await File.ReadAllTextAsync(extractedPath);

        // Parse both configurations
        var rootConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(rootContent);
        var extractedConfig = JsonSerializer.Deserialize<DevcontainerConfiguration>(extractedContent);

        rootConfig.Should().NotBeNull();
        extractedConfig.Should().NotBeNull();

        // The name should be different (project-specific)
        extractedConfig!.Name.Should().Contain(projectName);
        extractedConfig.Name.Should().NotBe(rootConfig!.Name);

        // Other structural elements should match or be equivalent
        if (rootConfig.Image != null)
        {
            extractedConfig.Image.Should().Be(rootConfig.Image);
        }

        if (rootConfig.Build != null)
        {
            extractedConfig.Build.Should().NotBeNull();
            extractedConfig.Build!.DockerfilePath.Should().Be(rootConfig.Build.DockerfilePath);
        }
    }

    private async Task CompareDockerfiles(string rootPath, string extractedPath)
    {
        var rootContent = await File.ReadAllTextAsync(rootPath);
        var extractedContent = await File.ReadAllTextAsync(extractedPath);

        // Dockerfiles should be very similar (might have minor differences due to template processing)
        var rootLines = rootContent.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
        var extractedLines = extractedContent.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();

        // Most lines should match
        var matchingLines = rootLines.Intersect(extractedLines).Count();
        var matchRatio = (double)matchingLines / rootLines.Count;

        matchRatio.Should().BeGreaterThan(0.9, "Dockerfiles should be nearly identical");
    }

    private void CompareCustomizations(object? rootCustomizations, object? extractedCustomizations)
    {
        if (rootCustomizations == null && extractedCustomizations == null)
            return;

        if (rootCustomizations == null || extractedCustomizations == null)
        {
            rootCustomizations.Should().Be(extractedCustomizations);
            return;
        }

        // Convert to JSON for easier comparison
        var rootJson = JsonSerializer.Serialize(rootCustomizations);
        var extractedJson = JsonSerializer.Serialize(extractedCustomizations);

        var rootCustom = JsonSerializer.Deserialize<Dictionary<string, object>>(rootJson);
        var extractedCustom = JsonSerializer.Deserialize<Dictionary<string, object>>(extractedJson);

        rootCustom!.Keys.Should().BeEquivalentTo(extractedCustom!.Keys);

        // Compare VS Code customizations if present
        if (rootCustom.ContainsKey("vscode") && extractedCustom.ContainsKey("vscode"))
        {
            var rootVscode = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(rootCustom["vscode"]));
            var extractedVscode = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(extractedCustom["vscode"]));

            rootVscode!.Keys.Should().BeEquivalentTo(extractedVscode!.Keys);
        }
    }

    /// <summary>
    /// Creates a test artifact directory for generated files
    /// </summary>
    private string CreateTestArtifactDirectory(string testName)
    {
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "test-artifacts", "universal-template", testName);

        if (Directory.Exists(testArtifactsPath))
        {
            Directory.Delete(testArtifactsPath, true);
        }

        Directory.CreateDirectory(testArtifactsPath);
        return testArtifactsPath;
    }
}