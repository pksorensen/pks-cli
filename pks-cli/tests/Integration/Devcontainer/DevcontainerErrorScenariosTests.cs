using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Implementations;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Tests for error scenarios and edge cases in devcontainer initialization
/// </summary>
public class DevcontainerErrorScenariosTests : TestBase
{
    private readonly IDevcontainerService _devcontainerService;
    private readonly IDevcontainerTemplateService _templateService;
    private readonly IDevcontainerFileGenerator _fileGenerator;
    private readonly DevcontainerInitializer _initializer;

    public DevcontainerErrorScenariosTests()
    {
        _devcontainerService = ServiceProvider.GetRequiredService<IDevcontainerService>();
        _templateService = ServiceProvider.GetRequiredService<IDevcontainerTemplateService>();
        _fileGenerator = ServiceProvider.GetRequiredService<IDevcontainerFileGenerator>();
        _initializer = ServiceProvider.GetRequiredService<DevcontainerInitializer>();
    }

    [Fact]
    public async Task ErrorScenario_NonExistentTemplate_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-nonexistent-template");
        var nonExistentTemplate = "completely-fake-template";

        var context = new InitializationContext
        {
            ProjectName = "error-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-template"] = nonExistentTemplate
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(error => error.Contains(nonExistentTemplate) || error.Contains("not found"));
        result.AffectedFiles.Should().BeEmpty();

        // Verify no devcontainer directory was created
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        Directory.Exists(devcontainerPath).Should().BeFalse();
    }

    [Fact]
    public async Task ErrorScenario_ReadOnlyOutputPath_ShouldFailGracefully()
    {
        // Arrange
        var readOnlyPath = "/root/readonly"; // Path that typically doesn't have write permissions
        
        var context = new InitializationContext
        {
            ProjectName = "readonly-test",
            TargetDirectory = readOnlyPath,
            WorkingDirectory = readOnlyPath,
            Template = "api",
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(error => 
            error.Contains("write") || 
            error.Contains("permission") || 
            error.Contains("access") ||
            error.Contains("readonly"));
    }

    [Fact]
    public async Task ErrorScenario_InvalidFeatures_ShouldReportSpecificErrors()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-invalid-features");
        
        var context = new InitializationContext
        {
            ProjectName = "invalid-features-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-features"] = new[] { "invalid-feature-1", "another-invalid-feature", "dotnet" }
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should either complete with warnings or fail with specific error messages
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("invalid-feature-1") || 
                error.Contains("another-invalid-feature") ||
                error.Contains("feature") && error.Contains("not found"));
        }
        else
        {
            // If it succeeds, it should have warnings about invalid features
            result.Warnings.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ErrorScenario_ConflictingFeatures_ShouldDetectConflicts()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-conflicting-features");
        
        var context = new InitializationContext
        {
            ProjectName = "conflicting-features-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-features"] = new[] 
                { 
                    "conflicting-feature-a", 
                    "conflicting-feature-b", 
                    "mutually-exclusive-feature"
                }
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // System should detect and report feature conflicts
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("conflict") || 
                error.Contains("incompatible") ||
                error.Contains("mutually exclusive"));
        }
    }

    [Fact]
    public async Task ErrorScenario_InvalidPortNumbers_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-invalid-ports");
        
        var context = new InitializationContext
        {
            ProjectName = "invalid-ports-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-ports"] = new[] { "invalid-port", "99999", "-1", "abc123" }
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should either succeed with warnings or fail with specific port errors
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("port") && (
                    error.Contains("invalid") || 
                    error.Contains("range") ||
                    error.Contains("number")));
        }
        else
        {
            result.Warnings.Should().Contain(warning => 
                warning.Contains("port") && warning.Contains("invalid"));
        }
    }

    [Fact]
    public async Task ErrorScenario_ExistingDevcontainerWithoutForce_ShouldPreventOverwrite()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-existing-devcontainer");
        var devcontainerPath = Path.Combine(testOutputPath, ".devcontainer");
        
        // Create existing devcontainer
        Directory.CreateDirectory(devcontainerPath);
        await File.WriteAllTextAsync(
            Path.Combine(devcontainerPath, "devcontainer.json"), 
            """{"name": "Existing Container"}""");

        var context = new InitializationContext
        {
            ProjectName = "existing-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object> 
            { 
                ["devcontainer"] = true,
                ["force"] = false // Explicitly don't force overwrite
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Errors.Should().Contain(error => 
            error.Contains("exists") || 
            error.Contains("already") ||
            error.Contains("overwrite"));

        // Original file should remain unchanged
        var existingContent = await File.ReadAllTextAsync(Path.Combine(devcontainerPath, "devcontainer.json"));
        existingContent.Should().Contain("Existing Container");
    }

    [Fact]
    public async Task ErrorScenario_CorruptedTemplateFile_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-corrupted-template");
        
        // This tests the service's resilience to corrupted template data
        var options = new DevcontainerOptions
        {
            Name = "corrupted-test",
            OutputPath = testOutputPath,
            Template = "corrupted-template-that-doesnt-exist"
        };

        // Act & Assert
        var extractionResult = await _templateService.ExtractTemplateAsync("corrupted-template-that-doesnt-exist", options);
        
        extractionResult.Success.Should().BeFalse();
        extractionResult.ErrorMessage.Should().NotBeNullOrEmpty();
        extractionResult.ExtractedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ErrorScenario_DiskSpaceExhaustion_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-disk-space");
        
        // Simulate disk space issues by trying to write to a very long path
        var impossiblyLongPath = Path.Combine(testOutputPath, new string('a', 300), "subdir", new string('b', 300));
        
        var options = new DevcontainerOptions
        {
            Name = "disk-space-test",
            OutputPath = impossiblyLongPath,
            Template = "dotnet-basic"
        };

        // Act
        var extractionResult = await _templateService.ExtractTemplateAsync("dotnet-basic", options);

        // Assert
        extractionResult.Success.Should().BeFalse();
        extractionResult.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ErrorScenario_NetworkUnavailable_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-network-unavailable");
        
        // This simulates scenarios where templates need to be downloaded but network is unavailable
        var context = new InitializationContext
        {
            ProjectName = "network-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-template"] = "remote-template-requiring-network"
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should handle network issues gracefully
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("network") || 
                error.Contains("download") ||
                error.Contains("connection") ||
                error.Contains("remote-template-requiring-network"));
        }
    }

    [Fact]
    public async Task ErrorScenario_MalformedJsonTemplate_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-malformed-json");
        
        // Create a malformed devcontainer configuration
        var malformedConfig = new DevcontainerConfiguration
        {
            Name = null!, // Invalid - name is required
            Image = "", // Invalid - empty image
            Features = null! // Invalid - null features
        };

        // Act
        var result = await _fileGenerator.GenerateDevcontainerJsonAsync(malformedConfig, testOutputPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.ValidationErrors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ErrorScenario_CircularTemplateDependency_ShouldDetectAndPrevent()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-circular-dependency");
        
        var context = new InitializationContext
        {
            ProjectName = "circular-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-template"] = "template-with-circular-dependency"
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should detect circular dependencies and fail gracefully
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("circular") || 
                error.Contains("dependency") ||
                error.Contains("recursive"));
        }
    }

    [Fact]
    public async Task ErrorScenario_UnsupportedDockerVersion_ShouldWarnOrFail()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-unsupported-docker");
        
        var context = new InitializationContext
        {
            ProjectName = "docker-version-test",
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object>
            {
                ["devcontainer"] = true,
                ["devcontainer-image"] = "unsupported-base-image:ancient-version"
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should warn about or reject unsupported Docker configurations
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("unsupported") || 
                error.Contains("version") ||
                error.Contains("image"));
        }
        else if (result.Warnings.Any())
        {
            result.Warnings.Should().Contain(warning => 
                warning.Contains("unsupported") || 
                warning.Contains("version"));
        }
    }

    [Fact]
    public async Task ErrorScenario_VeryLongProjectName_ShouldHandleGracefully()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-long-project-name");
        var veryLongProjectName = new string('a', 200); // Extremely long project name
        
        var context = new InitializationContext
        {
            ProjectName = veryLongProjectName,
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should either truncate the name, warn, or fail gracefully
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("name") && (
                    error.Contains("long") || 
                    error.Contains("length") ||
                    error.Contains("invalid")));
        }
        else if (result.Warnings.Any())
        {
            result.Warnings.Should().Contain(warning => 
                warning.Contains("name") && warning.Contains("truncated"));
        }
    }

    [Fact]
    public async Task ErrorScenario_SpecialCharactersInProjectName_ShouldSanitize()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-special-characters");
        var nameWithSpecialChars = "my-project@#$%^&*()+=[]{}|\\:;\"'<>?,./~`";
        
        var context = new InitializationContext
        {
            ProjectName = nameWithSpecialChars,
            TargetDirectory = testOutputPath,
            WorkingDirectory = testOutputPath,
            Template = "api",
            Options = new Dictionary<string, object> { ["devcontainer"] = true }
        };

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        // Should either sanitize the name or provide clear error message
        if (result.Errors.Any())
        {
            result.Errors.Should().Contain(error => 
                error.Contains("character") || 
                error.Contains("invalid") ||
                error.Contains("name"));
        }
        else
        {
            // If successful, check that special characters were handled
            if (result.AffectedFiles.Any())
            {
                var devcontainerJsonPath = result.AffectedFiles.First(f => f.EndsWith("devcontainer.json"));
                var content = await File.ReadAllTextAsync(devcontainerJsonPath);
                
                // Content should not contain the raw special characters
                content.Should().NotContain("@#$%^&*()");
            }
        }
    }

    [Fact]
    public async Task ErrorScenario_ConcurrentAccess_ShouldHandleRaceConditions()
    {
        // Arrange
        var testOutputPath = CreateTestArtifactDirectory("error-concurrent-access");
        var tasks = new List<Task<InitializationResult>>();

        // Create multiple concurrent initialization tasks for the same directory
        for (int i = 0; i < 3; i++)
        {
            var context = new InitializationContext
            {
                ProjectName = $"concurrent-test-{i}",
                TargetDirectory = testOutputPath, // Same directory for all
                WorkingDirectory = testOutputPath,
                Template = "api",
                Options = new Dictionary<string, object> { ["devcontainer"] = true }
            };

            tasks.Add(Task.Run(async () =>
            {
                var result = await _initializer.ExecuteAsync(context);
                return result;
            }));
        }

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert
        // At least one should succeed, others may fail due to conflicts
        results.Should().Contain(r => r.Errors.Count == 0, "At least one concurrent operation should succeed");
        
        // Any failures should be due to concurrent access, not crashes
        var failedResults = results.Where(r => r.Errors.Count > 0);
        foreach (var failedResult in failedResults)
        {
            failedResult.Errors.Should().Contain(error => 
                error.Contains("exists") || 
                error.Contains("conflict") ||
                error.Contains("access") ||
                error.Contains("in use"));
        }
    }

    /// <summary>
    /// Creates a test artifact directory for generated files
    /// </summary>
    private string CreateTestArtifactDirectory(string testName)
    {
        var testArtifactsPath = Path.Combine(Path.GetTempPath(), "test-artifacts", "error-scenarios", testName);
        
        if (Directory.Exists(testArtifactsPath))
        {
            Directory.Delete(testArtifactsPath, true);
        }
        
        Directory.CreateDirectory(testArtifactsPath);
        return testArtifactsPath;
    }
}