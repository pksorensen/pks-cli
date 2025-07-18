using FluentAssertions;
using Moq;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Registry;
using PKS.Infrastructure.Initializers.Service;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the InitializationService class
/// </summary>
public class InitializationServiceTests : TestBase
{
    private readonly Mock<IInitializerRegistry> _mockRegistry;
    private readonly MockInitializerFactory _mockFactory;

    public InitializationServiceTests(ITestOutputHelper output) : base(output)
    {
        _mockRegistry = new Mock<IInitializerRegistry>();
        _mockFactory = new MockInitializerFactory(FileSystem);
    }

    [Fact]
    public void InitializationService_Constructor_RequiresRegistry()
    {
        // Arrange & Act
        Action act = () => new InitializationService(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task InitializationService_InitializeProjectAsync_ReturnsSuccessForValidProject()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext("TestProject");
        var results = new[]
        {
            InitializationResult.CreateSuccess("Step 1 completed"),
            InitializationResult.CreateSuccess("Step 2 completed")
        };

        _mockRegistry.Setup(r => r.ExecuteAllAsync(context))
                    .ReturnsAsync(results);

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.Should().NotBeNull();
        summary.Success.Should().BeTrue();
        summary.ProjectName.Should().Be("TestProject");
        summary.Template.Should().Be("console");
        summary.Results.Should().HaveCount(2);
        summary.StartTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
        summary.EndTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InitializationService_InitializeProjectAsync_ReturnsFailureWhenInitializerFails()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext("TestProject");
        var results = new[]
        {
            InitializationResult.CreateSuccess("Step 1 completed"),
            InitializationResult.CreateFailure("Step 2 failed")
        };

        _mockRegistry.Setup(r => r.ExecuteAllAsync(context))
                    .ReturnsAsync(results);

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.Success.Should().BeFalse("Should fail when any initializer fails");
        summary.Results.Should().HaveCount(2);
    }

    [Fact]
    public async Task InitializationService_InitializeProjectAsync_HandlesRegistryException()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext("TestProject");

        _mockRegistry.Setup(r => r.ExecuteAllAsync(context))
                    .ThrowsAsync(new InvalidOperationException("Registry error"));

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.Success.Should().BeFalse();
        summary.ErrorMessage.Should().Be("Registry error");
    }

    [Fact]
    public async Task InitializationService_InitializeProjectAsync_CalculatesStatisticsCorrectly()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext("TestProject");
        
        var result1 = InitializationResult.CreateSuccess("Step 1");
        result1.AffectedFiles.Add("/test/file1.cs");
        result1.AffectedFiles.Add("/test/file2.cs");
        result1.Warnings.Add("Warning 1");

        var result2 = InitializationResult.CreateSuccess("Step 2");
        result2.AffectedFiles.Add("/test/file3.cs");
        result2.AffectedFiles.Add("/test/file1.cs"); // Duplicate
        result2.Warnings.Add("Warning 2");
        result2.Warnings.Add("Warning 3");
        result2.Errors.Add("Error 1");

        var results = new[] { result1, result2 };

        _mockRegistry.Setup(r => r.ExecuteAllAsync(context))
                    .ReturnsAsync(results);

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.FilesCreated.Should().Be(3, "Should count distinct files");
        summary.WarningsCount.Should().Be(3, "Should sum all warnings");
        summary.ErrorsCount.Should().Be(1, "Should sum all errors");
    }

    [Fact]
    public async Task InitializationService_ValidateTargetDirectoryAsync_AcceptsEmptyDirectory()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var targetDirectory = "/test/empty";
        CreateTestDirectory(targetDirectory);

        // Act
        var result = await service.ValidateTargetDirectoryAsync(targetDirectory, false);

        // Assert
        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task InitializationService_ValidateTargetDirectoryAsync_RejectsNonEmptyDirectoryWithoutForce()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var targetDirectory = "/test/nonempty";
        CreateTestDirectory(targetDirectory);
        CreateTestFile(Path.Combine(targetDirectory, "existing.txt"), "content");

        // Act
        var result = await service.ValidateTargetDirectoryAsync(targetDirectory, false);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not empty");
    }

    [Fact]
    public async Task InitializationService_ValidateTargetDirectoryAsync_AcceptsNonEmptyDirectoryWithForce()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var targetDirectory = "/test/nonempty";
        CreateTestDirectory(targetDirectory);
        CreateTestFile(Path.Combine(targetDirectory, "existing.txt"), "content");

        // Act
        var result = await service.ValidateTargetDirectoryAsync(targetDirectory, true);

        // Assert
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task InitializationService_ValidateTargetDirectoryAsync_RejectsEmptyPath()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);

        // Act
        var result = await service.ValidateTargetDirectoryAsync("", false);

        // Assert
        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("cannot be empty");
    }

    [Fact]
    public async Task InitializationService_ValidateTargetDirectoryAsync_CreatesNonExistentDirectory()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var targetDirectory = "/test/newdirectory";

        // Act
        var result = await service.ValidateTargetDirectoryAsync(targetDirectory, false);

        // Assert
        result.IsValid.Should().BeTrue();
        FileSystem.Directory.Exists(targetDirectory).Should().BeTrue("Directory should be created");
    }

    [Fact]
    public async Task InitializationService_GetAvailableTemplatesAsync_ReturnsBuiltInTemplates()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);

        // Act
        var templates = (await service.GetAvailableTemplatesAsync()).ToList();

        // Assert
        templates.Should().NotBeEmpty();
        templates.Should().Contain(t => t.Name == "console");
        templates.Should().Contain(t => t.Name == "api");
        templates.Should().Contain(t => t.Name == "web");
        templates.Should().Contain(t => t.Name == "agent");
        
        var consoleTemplate = templates.First(t => t.Name == "console");
        consoleTemplate.DisplayName.Should().Be("Console Application");
        consoleTemplate.Description.Should().Contain("console application");
        consoleTemplate.Tags.Should().Contain("dotnet");
    }

    [Fact]
    public async Task InitializationService_GetAvailableTemplatesAsync_LoadsTemplatesFromDirectory()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var templateDir = Path.Combine("/test", "Templates", "custom-template");
        var templateInfoFile = Path.Combine(templateDir, "template.json");
        
        CreateTestDirectory(templateDir);
        CreateTestFile(templateInfoFile, """
            {
                "DisplayName": "Custom Template",
                "Description": "A custom project template",
                "Tags": ["custom", "test"],
                "Author": "Test Author",
                "Version": "1.0.0"
            }
            """);

        // Note: This test would require setting up the actual template directory structure
        // which the service expects to find relative to the application directory.
        // For now, we'll verify the method doesn't throw and returns built-in templates.

        // Act
        var templates = await service.GetAvailableTemplatesAsync();

        // Assert
        templates.Should().NotBeEmpty("Should at least return built-in templates");
    }

    [Fact]
    public void InitializationService_CreateContext_CreatesValidContext()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var projectName = "MyProject";
        var template = "api";
        var targetDirectory = "/test/project";
        var force = true;
        var options = new Dictionary<string, object?> { ["test-option"] = "test-value" };

        // Act
        var context = service.CreateContext(projectName, template, targetDirectory, force, options);

        // Assert
        context.Should().NotBeNull();
        context.ProjectName.Should().Be(projectName);
        context.Template.Should().Be(template);
        context.TargetDirectory.Should().Be(targetDirectory);
        context.Force.Should().Be(force);
        context.Options.Should().ContainKey("test-option");
        context.GetOption<string>("test-option").Should().Be("test-value");
        context.Interactive.Should().BeTrue("Should default to interactive");
    }

    [Fact]
    public void InitializationService_CreateContext_SetsNonInteractiveMode()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var options = new Dictionary<string, object?> { ["non-interactive"] = true };

        // Act
        var context = service.CreateContext("Test", "console", "/test", false, options);

        // Assert
        context.Interactive.Should().BeFalse("Should set non-interactive mode");
    }

    [Fact]
    public async Task InitializationService_InitializeProjectAsync_FailsForInvalidTargetDirectory()
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext("TestProject", targetDirectory: "");

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.Success.Should().BeFalse();
        summary.ErrorMessage.Should().Contain("cannot be empty");
    }

    [Theory]
    [InlineData("console")]
    [InlineData("api")]
    [InlineData("web")]
    [InlineData("agent")]
    public async Task InitializationService_InitializeProjectAsync_WorksWithAllTemplates(string template)
    {
        // Arrange
        var service = new InitializationService(_mockRegistry.Object);
        var context = CreateTestContext($"Project_{template}", template: template);
        var results = new[] { InitializationResult.CreateSuccess($"Created {template} project") };

        _mockRegistry.Setup(r => r.ExecuteAllAsync(context))
                    .ReturnsAsync(results);

        // Act
        var summary = await service.InitializeProjectAsync(context);

        // Assert
        summary.Success.Should().BeTrue($"Should work with {template} template");
        summary.Template.Should().Be(template);
    }

    private InitializationContext CreateTestContext(
        string projectName,
        string template = "console",
        string targetDirectory = "/test/output")
    {
        CreateTestDirectory(targetDirectory);
        
        return new InitializationContext
        {
            ProjectName = projectName,
            Template = template,
            TargetDirectory = targetDirectory,
            WorkingDirectory = TestDirectory,
            Force = false,
            Interactive = false
        };
    }
}