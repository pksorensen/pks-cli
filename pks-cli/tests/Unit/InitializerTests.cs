using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers;
using PKS.Infrastructure.Initializers.Context;
using Xunit;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the IInitializer interface and implementations
/// </summary>
public class InitializerTests : TestBase
{
    private readonly MockInitializerFactory _mockFactory;

    public InitializerTests(ITestOutputHelper output) : base(output)
    {
        _mockFactory = new MockInitializerFactory(FileSystem);
    }

    [Fact]
    public async Task IInitializer_ShouldRunAsync_ReturnsTrueByDefault()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ShouldRunAsync(context);

        // Assert
        result.Should().BeTrue("Default behavior should be to run the initializer");
    }

    [Fact]
    public async Task IInitializer_ShouldRunAsync_ReturnsFalseWhenConfigured()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer(shouldRun: false);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ShouldRunAsync(context);

        // Assert
        result.Should().BeFalse("Should respect the configured shouldRun parameter");
    }

    [Fact]
    public async Task IInitializer_ExecuteAsync_ReturnsSuccessWhenConfigured()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer(shouldSucceed: true);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue("Should succeed when configured to do so");
        result.Message.Should().Contain("Test initialization completed");
        result.AffectedFiles.Should().NotBeEmpty("Should create test files");
    }

    [Fact]
    public async Task IInitializer_ExecuteAsync_ReturnsFailureWhenConfigured()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer(shouldSucceed: false);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse("Should fail when configured to do so");
        result.Message.Should().Contain("Test failure");
    }

    [Fact]
    public void IInitializer_GetOptions_ReturnsExpectedOptions()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();

        // Act
        var options = initializer.GetOptions().ToList();

        // Assert
        options.Should().NotBeEmpty("Should return at least one test option");
        options.Should().Contain(opt => opt.Name == "test-option");
        
        var testOption = options.First(opt => opt.Name == "test-option");
        testOption.Description.Should().Be("A test option");
        testOption.DefaultValue.Should().Be("default-value");
        testOption.Required.Should().BeFalse();
    }

    [Fact]
    public void IInitializer_Properties_AreSetCorrectly()
    {
        // Arrange
        const string expectedId = "my-test-id";
        const string expectedName = "My Test Initializer";
        const int expectedOrder = 50;

        // Act
        var initializer = _mockFactory.CreateTestInitializer(
            id: expectedId,
            name: expectedName,
            order: expectedOrder);

        // Assert
        initializer.Id.Should().Be(expectedId);
        initializer.Name.Should().Be(expectedName);
        initializer.Description.Should().Contain(expectedName);
        initializer.Order.Should().Be(expectedOrder);
    }

    [Fact]
    public async Task IInitializer_ExecuteAsync_CreatesExpectedFiles()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();
        var context = CreateTestContext("MyProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        var expectedFile = Path.Combine(context.TargetDirectory, "MyProject-test.txt");
        FileSystem.File.Exists(expectedFile).Should().BeTrue("Test file should be created");
        
        var content = await FileSystem.File.ReadAllTextAsync(expectedFile);
        content.Should().Contain("Test file created by");
    }

    [Theory]
    [InlineData("console")]
    [InlineData("api")]
    [InlineData("web")]
    [InlineData("agent")]
    public async Task IInitializer_WorksWithDifferentTemplates(string template)
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();
        var context = CreateTestContext("TestProject", template: template);

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue($"Should work with {template} template");
        result.AffectedFiles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task IInitializer_ExecuteAsync_HandlesDirectoryCreation()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();
        var context = CreateTestContext("TestProject", targetDirectory: "/non/existent/path");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("Should handle directory creation");
        
        var expectedFile = Path.Combine(context.TargetDirectory, "TestProject-test.txt");
        FileSystem.File.Exists(expectedFile).Should().BeTrue("File should be created in new directory");
    }

    [Fact]
    public async Task IInitializer_ExecuteAsync_WithOptionsInContext()
    {
        // Arrange
        var initializer = _mockFactory.CreateTestInitializer();
        var context = CreateTestContext("TestProject");
        context.SetOption("custom-option", "custom-value");
        context.SetMetadata("build-info", new { Version = "1.0.0" });

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("Should handle context with options and metadata");
        
        // Verify options are preserved
        context.GetOption<string>("custom-option").Should().Be("custom-value");
        var buildInfo = context.GetMetadata<object>("build-info");
        buildInfo.Should().NotBeNull();
    }

    private InitializationContext CreateTestContext(
        string projectName, 
        string template = "console",
        string targetDirectory = "/test/project")
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