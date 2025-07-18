using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;
using System.IO.Abstractions.TestingHelpers;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the BaseInitializer class
/// </summary>
public class BaseInitializerTests : TestBase
{
    public BaseInitializerTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task BaseInitializer_ShouldRunAsync_ReturnsTrueByDefault()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ShouldRunAsync(context);

        // Assert
        result.Should().BeTrue("BaseInitializer should return true by default");
    }

    [Fact]
    public async Task BaseInitializer_ExecuteAsync_CallsExecuteInternalAsync()
    {
        // Arrange
        var initializer = new TestBaseInitializer(shouldSucceed: true);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeTrue();
        result.Message.Should().Be("Test execution completed");
        initializer.ExecuteInternalWasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task BaseInitializer_ExecuteAsync_HandlesExceptions()
    {
        // Arrange
        var initializer = new TestBaseInitializer(shouldThrow: true);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Should().NotBeNull();
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Failed to execute Test Initializer");
        result.Message.Should().Contain("Test exception");
    }

    [Fact]
    public void BaseInitializer_GetOptions_ReturnsEmptyByDefault()
    {
        // Arrange
        var initializer = new TestBaseInitializer();

        // Act
        var options = initializer.GetOptions();

        // Assert
        options.Should().BeEmpty("BaseInitializer should return empty options by default");
    }

    [Fact]
    public async Task BaseInitializer_ShouldOverwriteFileAsync_ReturnsTrueForNonExistentFile()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject", interactive: false);
        var filePath = Path.Combine(context.TargetDirectory, "test.txt");

        // Act
        var result = await initializer.TestShouldOverwriteFileAsync(filePath, context);

        // Assert
        result.Should().BeTrue("Should return true for non-existent files");
    }

    [Fact]
    public async Task BaseInitializer_ShouldOverwriteFileAsync_ReturnsTrueWhenForced()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject", force: true);
        var filePath = Path.Combine(context.TargetDirectory, "test.txt");
        
        // Create the file first
        CreateTestFile(filePath, "existing content");

        // Act
        var result = await initializer.TestShouldOverwriteFileAsync(filePath, context);

        // Assert
        result.Should().BeTrue("Should return true when force is enabled");
    }

    [Fact]
    public async Task BaseInitializer_ShouldOverwriteFileAsync_ReturnsFalseInNonInteractiveMode()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject", interactive: false, force: false);
        var filePath = Path.Combine(context.TargetDirectory, "test.txt");
        
        // Create the file first
        CreateTestFile(filePath, "existing content");

        // Act
        var result = await initializer.TestShouldOverwriteFileAsync(filePath, context);

        // Assert
        result.Should().BeFalse("Should return false in non-interactive mode without force");
    }

    [Fact]
    public void BaseInitializer_EnsureDirectoryExists_CreatesDirectory()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var directoryPath = "/test/new/directory";

        // Act
        initializer.TestEnsureDirectoryExists(directoryPath);

        // Assert
        FileSystem.Directory.Exists(directoryPath).Should().BeTrue("Directory should be created");
    }

    [Fact]
    public void BaseInitializer_EnsureDirectoryExists_DoesNotThrowForExistingDirectory()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var directoryPath = "/test/existing";
        CreateTestDirectory(directoryPath);

        // Act
        Action act = () => initializer.TestEnsureDirectoryExists(directoryPath);

        // Assert
        act.Should().NotThrow("Should not throw for existing directories");
    }

    [Fact]
    public async Task BaseInitializer_WriteFileAsync_CreatesFileWithContent()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");
        var filePath = Path.Combine(context.TargetDirectory, "test.txt");
        var content = "Test file content";

        // Act
        await initializer.TestWriteFileAsync(filePath, content, context);

        // Assert
        FileSystem.File.Exists(filePath).Should().BeTrue("File should be created");
        var actualContent = await FileSystem.File.ReadAllTextAsync(filePath);
        actualContent.Should().Be(content);
    }

    [Fact]
    public async Task BaseInitializer_WriteFileAsync_CreatesDirectoryIfNeeded()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");
        var filePath = Path.Combine(context.TargetDirectory, "subdir", "test.txt");
        var content = "Test file content";

        // Act
        await initializer.TestWriteFileAsync(filePath, content, context);

        // Assert
        FileSystem.File.Exists(filePath).Should().BeTrue("File should be created");
        FileSystem.Directory.Exists(Path.GetDirectoryName(filePath)!).Should().BeTrue("Directory should be created");
    }

    [Fact]
    public async Task BaseInitializer_CopyFileAsync_CopiesFile()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");
        var sourcePath = Path.Combine(TestDirectory, "source.txt");
        var destinationPath = Path.Combine(context.TargetDirectory, "destination.txt");
        var content = "Source file content";
        
        CreateTestFile(sourcePath, content);

        // Act
        var result = await initializer.TestCopyFileAsync(sourcePath, destinationPath, context);

        // Assert
        result.Should().BeTrue("Copy should succeed");
        FileSystem.File.Exists(destinationPath).Should().BeTrue("Destination file should exist");
        var copiedContent = await FileSystem.File.ReadAllTextAsync(destinationPath);
        copiedContent.Should().Be(content);
    }

    [Fact]
    public async Task BaseInitializer_CopyFileAsync_ReturnsFalseForNonExistentSource()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");
        var sourcePath = Path.Combine(TestDirectory, "nonexistent.txt");
        var destinationPath = Path.Combine(context.TargetDirectory, "destination.txt");

        // Act
        var result = await initializer.TestCopyFileAsync(sourcePath, destinationPath, context);

        // Assert
        result.Should().BeFalse("Copy should fail for non-existent source");
        FileSystem.File.Exists(destinationPath).Should().BeFalse("Destination file should not exist");
    }

    [Fact]
    public void BaseInitializer_ReplacePlaceholders_ReplacesProjectName()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("MyTestProject");
        var content = "Project name: {{ProjectName}}, {{Project.Name}}, {{PROJECT_NAME}}, {{project_name}}";

        // Act
        var result = initializer.TestReplacePlaceholders(content, context);

        // Assert
        result.Should().Contain("MyTestProject");
        result.Should().Contain("MYTESTPROJECT");
        result.Should().Contain("mytestproject");
    }

    [Fact]
    public void BaseInitializer_ReplacePlaceholders_ReplacesDescription()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject", description: "A test project");
        var content = "Description: {{Description}}, {{Project.Description}}";

        // Act
        var result = initializer.TestReplacePlaceholders(content, context);

        // Assert
        result.Should().Contain("A test project");
    }

    [Fact]
    public void BaseInitializer_ReplacePlaceholders_ReplacesDateTimePlaceholders()
    {
        // Arrange
        var initializer = new TestBaseInitializer();
        var context = CreateTestContext("TestProject");
        var content = "Date: {{Date}}, DateTime: {{DateTime}}, Year: {{Year}}";

        // Act
        var result = initializer.TestReplacePlaceholders(content, context);

        // Assert
        result.Should().Contain(DateTime.Now.ToString("yyyy-MM-dd"));
        result.Should().Contain(DateTime.Now.Year.ToString());
    }

    private InitializationContext CreateTestContext(
        string projectName,
        string template = "console",
        string targetDirectory = "/test/project",
        bool interactive = false,
        bool force = false,
        string? description = null)
    {
        CreateTestDirectory(targetDirectory);
        
        return new InitializationContext
        {
            ProjectName = projectName,
            Description = description,
            Template = template,
            TargetDirectory = targetDirectory,
            WorkingDirectory = TestDirectory,
            Force = force,
            Interactive = interactive
        };
    }

    /// <summary>
    /// Test implementation of BaseInitializer for testing purposes
    /// </summary>
    private class TestBaseInitializer : BaseInitializer
    {
        private readonly bool _shouldSucceed;
        private readonly bool _shouldThrow;

        public TestBaseInitializer(bool shouldSucceed = true, bool shouldThrow = false)
        {
            _shouldSucceed = shouldSucceed;
            _shouldThrow = shouldThrow;
        }

        public override string Id => "test-base-initializer";
        public override string Name => "Test Initializer";
        public override string Description => "Test implementation of BaseInitializer";

        public bool ExecuteInternalWasCalled { get; private set; }

        protected override Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
        {
            ExecuteInternalWasCalled = true;

            if (_shouldThrow)
            {
                throw new InvalidOperationException("Test exception");
            }

            if (!_shouldSucceed)
            {
                return Task.FromResult(InitializationResult.CreateFailure("Test failure"));
            }

            return Task.FromResult(InitializationResult.CreateSuccess("Test execution completed"));
        }

        // Expose protected methods for testing
        public Task<bool> TestShouldOverwriteFileAsync(string filePath, InitializationContext context)
            => ShouldOverwriteFileAsync(filePath, context);

        public void TestEnsureDirectoryExists(string directoryPath)
            => EnsureDirectoryExists(directoryPath);

        public Task TestWriteFileAsync(string filePath, string content, InitializationContext context)
            => WriteFileAsync(filePath, content, context);

        public Task<bool> TestCopyFileAsync(string sourcePath, string destinationPath, InitializationContext context)
            => CopyFileAsync(sourcePath, destinationPath, context);

        public string TestReplacePlaceholders(string content, InitializationContext context)
            => ReplacePlaceholders(content, context);
    }
}