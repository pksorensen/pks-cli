using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.CLI.Tests.Infrastructure.Fixtures;
using PKS.CLI.Tests.Infrastructure.Mocks;
using PKS.Infrastructure.Initializers.Implementations;
using PKS.Infrastructure.Initializers.Context;
using Xunit;

namespace PKS.CLI.Tests.Infrastructure.Initializers;

/// <summary>
/// Tests for the hooks initializer functionality
/// These tests define the expected behavior for hooks initialization during project setup
/// </summary>
public class HooksInitializerTests : TestBase
{
    private readonly HooksInitializer _initializer;
    private readonly string _tempDirectory;

    public HooksInitializerTests()
    {
        _tempDirectory = CreateTempDirectory();
        _initializer = new HooksInitializer();
    }

    [Fact]
    public void Properties_ShouldHaveCorrectValues()
    {
        // Assert
        _initializer.Id.Should().Be("hooks");
        _initializer.Name.Should().Be("Hooks System");
        _initializer.Description.Should().Contain("hooks");
        _initializer.Order.Should().Be(60); // Should run after basic project setup but before documentation
    }

    [Fact]
    public async Task ShouldRunAsync_ShouldReturnTrue_WhenHooksOptionIsEnabled()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: true);

        // Act
        var shouldRun = await _initializer.ShouldRunAsync(context);

        // Assert
        shouldRun.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldRunAsync_ShouldReturnFalse_WhenHooksOptionIsDisabled()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: false);

        // Act
        var shouldRun = await _initializer.ShouldRunAsync(context);

        // Assert
        shouldRun.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCreateHooksConfiguration_WhenExecuted()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: true);

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.AffectedFiles.Should().Contain(f => f.EndsWith(".hooks.json"));
        result.AffectedFiles.Should().Contain(f => f.EndsWith("hooks/"));

        // Verify hooks configuration file was created
        var hooksConfigPath = Path.Combine(_tempDirectory, ".hooks.json");
        File.Exists(hooksConfigPath).Should().BeTrue();

        // Verify hooks directory was created
        var hooksDirectoryPath = Path.Combine(_tempDirectory, "hooks");
        Directory.Exists(hooksDirectoryPath).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCreateDefaultHooks_WhenTemplateRequiresHooks()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: true, template: "api");

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        var hooksDirectory = Path.Combine(_tempDirectory, "hooks");
        
        // Should create common hooks for API projects
        File.Exists(Path.Combine(hooksDirectory, "pre-build.sh")).Should().BeTrue();
        File.Exists(Path.Combine(hooksDirectory, "post-build.sh")).Should().BeTrue();
        File.Exists(Path.Combine(hooksDirectory, "pre-deploy.sh")).Should().BeTrue();
        File.Exists(Path.Combine(hooksDirectory, "post-deploy.sh")).Should().BeTrue();

        result.AffectedFiles.Should().Contain(f => f.Contains("pre-build.sh"));
        result.AffectedFiles.Should().Contain(f => f.Contains("post-build.sh"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCreateHooksReadme_WhenExecuted()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: true);

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        var readmePath = Path.Combine(_tempDirectory, "hooks", "README.md");
        File.Exists(readmePath).Should().BeTrue();

        var readmeContent = await File.ReadAllTextAsync(readmePath);
        readmeContent.Should().Contain("Hooks System");
        readmeContent.Should().Contain("Available Hooks");
        readmeContent.Should().Contain("Usage");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldConfigureHooksForAgentic_WhenAgenticIsEnabled()
    {
        // Arrange
        var context = CreateInitializationContext(hooks: true, agentic: true);

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        var hooksConfigPath = Path.Combine(_tempDirectory, ".hooks.json");
        var configContent = await File.ReadAllTextAsync(hooksConfigPath);
        
        configContent.Should().Contain("agentic");
        configContent.Should().Contain("ai-integration");
        
        // Should create AI-specific hooks
        var hooksDirectory = Path.Combine(_tempDirectory, "hooks");
        File.Exists(Path.Combine(hooksDirectory, "ai-code-review.ps1")).Should().BeTrue();
        File.Exists(Path.Combine(hooksDirectory, "ai-test-generation.ps1")).Should().BeTrue();
    }

    [Fact]
    public async Task GetOptions_ShouldReturnHooksOption()
    {
        // Act
        var options = _initializer.GetOptions();

        // Assert
        options.Should().ContainSingle(o => o.Name == "hooks");
        var hooksOption = options.First(o => o.Name == "hooks");
        hooksOption.Description.Should().Contain("hooks");
        hooksOption.DefaultValue.Should().Be(false);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleExistingHooksDirectory_WhenForceIsTrue()
    {
        // Arrange
        var hooksDirectory = Path.Combine(_tempDirectory, "hooks");
        Directory.CreateDirectory(hooksDirectory);
        File.WriteAllText(Path.Combine(hooksDirectory, "existing-hook.ps1"), "# Existing hook");

        var context = CreateInitializationContext(hooks: true, force: true);

        // Act
        var result = await _initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(m => m.Contains("existing"));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailGracefully_WhenDirectoryCreationFails()
    {
        // Arrange
        // Create context with invalid path to simulate directory creation failure
        var options = TestDataGenerator.GenerateInitializationOptions(
            template: "console",
            agentic: false);
        
        var invalidContext = new PKS.Infrastructure.Initializers.Context.InitializationContext
        {
            ProjectName = options.ProjectName,
            Description = options.Description,
            Template = "console",
            TargetDirectory = "/invalid/path/that/does/not/exist",
            WorkingDirectory = "/invalid/path/that/does/not/exist",
            Options = new Dictionary<string, object?>
            {
                ["hooks"] = true,
                ["agentic"] = false,
                ["force"] = false
            }
        };

        // Act
        var result = await _initializer.ExecuteAsync(invalidContext);

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(m => m.Contains("Failed") || m.Contains("Error"));
    }

    private PKS.Infrastructure.Initializers.Context.InitializationContext CreateInitializationContext(
        bool hooks = false,
        bool agentic = false,
        string template = "console",
        bool force = false)
    {
        var options = TestDataGenerator.GenerateInitializationOptions(
            template: template,
            agentic: agentic);
        
        return new PKS.Infrastructure.Initializers.Context.InitializationContext
        {
            ProjectName = options.ProjectName,
            Description = options.Description,
            Template = template,
            TargetDirectory = _tempDirectory,
            WorkingDirectory = _tempDirectory,
            Options = new Dictionary<string, object?>
            {
                ["hooks"] = hooks,
                ["agentic"] = agentic,
                ["force"] = force
            }
        };
    }

    public override void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
        base.Dispose();
    }
}

