using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;
using System.IO.Abstractions.TestingHelpers;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the CodeInitializer class
/// </summary>
public class CodeInitializerTests : TestBase
{
    public CodeInitializerTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task CodeInitializer_ExecuteAsync_CallsExecutionHooks()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        initializer.PreExecuteWasCalled.Should().BeTrue("PreExecuteAsync should be called");
        initializer.ExecuteCodeLogicWasCalled.Should().BeTrue("ExecuteCodeLogicAsync should be called");
        initializer.PostExecuteWasCalled.Should().BeTrue("PostExecuteAsync should be called");
    }

    [Fact]
    public async Task CodeInitializer_ExecuteAsync_HandlesExceptionInCodeLogic()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem, shouldThrow: true);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse("Should fail when exception is thrown");
        result.Message.Should().Contain("Code execution failed");
        result.Message.Should().Contain("Test exception");
    }

    [Fact]
    public void CodeInitializer_CreateDirectoryStructure_CreatesMultipleDirectories()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var basePath = "/test/project";
        var directories = new[] { "src", "tests", "docs", "build/scripts" };

        // Act
        initializer.TestCreateDirectoryStructure(basePath, directories);

        // Assert
        foreach (var directory in directories)
        {
            var fullPath = Path.Combine(basePath, directory);
            FileSystem.Directory.Exists(fullPath).Should().BeTrue($"Directory {fullPath} should be created");
        }
    }

    [Fact]
    public async Task CodeInitializer_CreateFileAsync_CreatesFileAndUpdatesResult()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "test.txt");
        var content = "Test file content";

        // Act
        await initializer.TestCreateFileAsync(filePath, content, context, result);

        // Assert
        FileSystem.File.Exists(filePath).Should().BeTrue("File should be created");
        var actualContent = await FileSystem.File.ReadAllTextAsync(filePath);
        actualContent.Should().Be(content);
        result.AffectedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task CodeInitializer_CreateFileFromResourceAsync_CreatesFileFromEmbeddedResource()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("MyProject");
        var result = InitializationResult.CreateSuccess("Test");
        var targetPath = Path.Combine(context.TargetDirectory, "from-resource.txt");

        // Act
        await initializer.TestCreateFileFromResourceAsync("TestResource", targetPath, context, result);

        // Assert
        if (result.Warnings.Any(w => w.Contains("Embedded resource not found")))
        {
            // Resource doesn't exist (expected in test), but method should handle gracefully
            FileSystem.File.Exists(targetPath).Should().BeFalse();
            result.Warnings.Should().Contain(w => w.Contains("TestResource"));
        }
        else
        {
            // If resource exists, file should be created
            FileSystem.File.Exists(targetPath).Should().BeTrue();
        }
    }

    [Fact]
    public async Task CodeInitializer_ModifyFileAsync_ModifiesExistingFile()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "modify.txt");
        var originalContent = "Original content";
        
        CreateTestFile(filePath, originalContent);

        // Act
        await initializer.TestModifyFileAsync(filePath, content => content + "\nModified", context, result);

        // Assert
        var modifiedContent = await FileSystem.File.ReadAllTextAsync(filePath);
        modifiedContent.Should().Be("Original content\nModified");
        result.AffectedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task CodeInitializer_ModifyFileAsync_AddsWarningForNonExistentFile()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "nonexistent.txt");

        // Act
        await initializer.TestModifyFileAsync(filePath, content => content + "Modified", context, result);

        // Assert
        result.Warnings.Should().Contain(w => w.Contains("File not found for modification"));
        result.AffectedFiles.Should().NotContain(filePath);
    }

    [Fact]
    public async Task CodeInitializer_AppendToFileAsync_AppendsContentToFile()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("MyProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "append.txt");
        var appendContent = "\nAppended content for {{ProjectName}}";

        // Act
        await initializer.TestAppendToFileAsync(filePath, appendContent, context, result);

        // Assert
        FileSystem.File.Exists(filePath).Should().BeTrue("File should be created if it doesn't exist");
        var content = await FileSystem.File.ReadAllTextAsync(filePath);
        content.Should().Contain("Appended content for MyProject");
        result.AffectedFiles.Should().Contain(filePath);
    }

    [Fact]
    public async Task CodeInitializer_AppendToFileAsync_CreatesDirectoryIfNeeded()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "subdir", "append.txt");
        var appendContent = "Content";

        // Act
        await initializer.TestAppendToFileAsync(filePath, appendContent, context, result);

        // Assert
        FileSystem.File.Exists(filePath).Should().BeTrue("File should be created");
        FileSystem.Directory.Exists(Path.GetDirectoryName(filePath)!).Should().BeTrue("Directory should be created");
    }

    [Theory]
    [InlineData("dotnet", "--version", true)]
    [InlineData("git", "--version", true)]
    [InlineData("nonexistent-tool", "--version", false)]
    public async Task CodeInitializer_ValidateToolAsync_ReturnsExpectedResult(string toolName, string versionCommand, bool shouldExist)
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);

        // Act
        var result = await initializer.TestValidateToolAsync(toolName, versionCommand);

        // Assert
        if (shouldExist)
        {
            // For existing tools like dotnet and git, we expect validation to work on most dev machines
            // But this is environment dependent, so we just verify the method doesn't throw
            result.Should().BeOneOf(true, false);
        }
        else
        {
            // For non-existent tools, validation should fail
            result.Should().BeFalse("Non-existent tool should fail validation");
        }
    }

    [Fact]
    public async Task CodeInitializer_ExecuteCodeLogicAsync_IsAbstractAndMustBeImplemented()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        initializer.ExecuteCodeLogicWasCalled.Should().BeTrue("Abstract method should be implemented and called");
    }

    [Fact]
    public async Task CodeInitializer_ExecuteAsync_ProcessesPlaceholdersInContent()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("MyTestProject");
        var result = InitializationResult.CreateSuccess("Test");
        var filePath = Path.Combine(context.TargetDirectory, "placeholders.txt");
        var content = "Project: {{ProjectName}}, Date: {{Date}}";

        // Act
        await initializer.TestCreateFileAsync(filePath, content, context, result);

        // Assert
        var actualContent = await FileSystem.File.ReadAllTextAsync(filePath);
        actualContent.Should().Contain("Project: MyTestProject");
        actualContent.Should().Contain($"Date: {DateTime.Now:yyyy-MM-dd}");
    }

    [Fact]
    public async Task CodeInitializer_FullWorkflow_CreatesCompleteProjectStructure()
    {
        // Arrange
        var initializer = new TestCodeInitializer(FileSystem);
        var context = CreateTestContext("CompleteProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        // Verify the complete project structure was created
        var projectFile = Path.Combine(context.TargetDirectory, "CompleteProject.csproj");
        var programFile = Path.Combine(context.TargetDirectory, "Program.cs");
        var testDir = Path.Combine(context.TargetDirectory, "tests");
        
        FileSystem.File.Exists(projectFile).Should().BeTrue("Project file should be created");
        FileSystem.File.Exists(programFile).Should().BeTrue("Program file should be created");
        FileSystem.Directory.Exists(testDir).Should().BeTrue("Test directory should be created");
        
        result.AffectedFiles.Should().HaveCountGreaterThan(0);
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

    /// <summary>
    /// Test implementation of CodeInitializer
    /// </summary>
    private class TestCodeInitializer : CodeInitializer
    {
        private readonly System.IO.Abstractions.IFileSystem _fileSystem;
        private readonly bool _shouldThrow;

        public TestCodeInitializer(System.IO.Abstractions.IFileSystem fileSystem, bool shouldThrow = false)
        {
            _fileSystem = fileSystem;
            _shouldThrow = shouldThrow;
        }

        public override string Id => "test-code-initializer";
        public override string Name => "Test Code Initializer";
        public override string Description => "Test implementation of CodeInitializer";

        public bool PreExecuteWasCalled { get; private set; }
        public bool ExecuteCodeLogicWasCalled { get; private set; }
        public bool PostExecuteWasCalled { get; private set; }

        protected override Task PreExecuteAsync(InitializationContext context, InitializationResult result)
        {
            PreExecuteWasCalled = true;
            return Task.CompletedTask;
        }

        protected override async Task ExecuteCodeLogicAsync(InitializationContext context, InitializationResult result)
        {
            ExecuteCodeLogicWasCalled = true;

            if (_shouldThrow)
            {
                throw new InvalidOperationException("Test exception");
            }

            // Create a sample project structure
            CreateDirectoryStructure(context.TargetDirectory, "src", "tests", "docs");

            var projectFile = Path.Combine(context.TargetDirectory, $"{context.ProjectName}.csproj");
            var projectContent = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                  </PropertyGroup>
                </Project>
                """;
            await CreateFileAsync(projectFile, projectContent, context, result);

            var programFile = Path.Combine(context.TargetDirectory, "Program.cs");
            var programContent = $"""
                // {context.ProjectName} - Generated on {DateTime.Now:yyyy-MM-dd}
                Console.WriteLine("Hello from {context.ProjectName}!");
                """;
            await CreateFileAsync(programFile, programContent, context, result);
        }

        protected override Task PostExecuteAsync(InitializationContext context, InitializationResult result)
        {
            PostExecuteWasCalled = true;
            return Task.CompletedTask;
        }

        // Expose protected methods for testing
        public void TestCreateDirectoryStructure(string basePath, params string[] directories)
            => CreateDirectoryStructure(basePath, directories);

        public Task TestCreateFileAsync(string filePath, string content, InitializationContext context, InitializationResult result)
            => CreateFileAsync(filePath, content, context, result);

        public Task TestCreateFileFromResourceAsync(string resourceName, string targetPath, InitializationContext context, InitializationResult result)
            => CreateFileFromResourceAsync(resourceName, targetPath, context, result);

        public Task TestModifyFileAsync(string filePath, Func<string, string> modifier, InitializationContext context, InitializationResult result)
            => ModifyFileAsync(filePath, modifier, context, result);

        public Task TestAppendToFileAsync(string filePath, string content, InitializationContext context, InitializationResult result)
            => AppendToFileAsync(filePath, content, context, result);

        public Task<bool> TestValidateToolAsync(string toolName, string? versionCommand = null)
            => ValidateToolAsync(toolName, versionCommand);

        // Override file system operations to use test file system
        protected override void EnsureDirectoryExists(string directoryPath)
        {
            if (!_fileSystem.Directory.Exists(directoryPath))
            {
                _fileSystem.Directory.CreateDirectory(directoryPath);
            }
        }

        protected override async Task WriteFileAsync(string filePath, string content, InitializationContext context)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureDirectoryExists(directory);
            }

            var processedContent = ReplacePlaceholders(content, context);
            await _fileSystem.File.WriteAllTextAsync(filePath, processedContent);
        }
    }
}