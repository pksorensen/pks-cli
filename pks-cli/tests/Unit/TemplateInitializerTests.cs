using FluentAssertions;
using PKS.CLI.Tests.Infrastructure;
using PKS.Infrastructure.Initializers.Base;
using PKS.Infrastructure.Initializers.Context;
using System.IO.Abstractions.TestingHelpers;
using Xunit.Abstractions;

namespace PKS.CLI.Tests.Unit;

/// <summary>
/// Unit tests for the TemplateInitializer class
/// </summary>
public class TemplateInitializerTests : TestBase
{
    private const string TemplateDirectory = "test-template";

    public TemplateInitializerTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task TemplateInitializer_ShouldRunAsync_ReturnsFalseWhenTemplateDirectoryNotFound()
    {
        // Arrange
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ShouldRunAsync(context);

        // Assert
        result.Should().BeFalse("Should return false when template directory doesn't exist");
    }

    [Fact]
    public async Task TemplateInitializer_ShouldRunAsync_ReturnsTrueWhenTemplateDirectoryExists()
    {
        // Arrange
        var templatePath = Path.Combine("/test", "Templates", TemplateDirectory);
        CreateTestDirectory(templatePath);
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ShouldRunAsync(context);

        // Assert
        result.Should().BeTrue("Should return true when template directory exists");
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_FailsWhenTemplateDirectoryNotFound()
    {
        // Arrange
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeFalse("Should fail when template directory doesn't exist");
        result.Message.Should().Contain("Template directory not found");
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_ProcessesTextFileWithPlaceholders()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        var templateFile = Path.Combine(templatePath, "Program.cs");
        var templateContent = """
            namespace {{ProjectName}};
            
            class Program
            {
                static void Main()
                {
                    Console.WriteLine("Hello from {{ProjectName}}!");
                    // Created on {{Date}}
                }
            }
            """;
        
        CreateTestFile(templateFile, templateContent);
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("MyTestApp");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue("Should succeed when processing template files");
        
        var outputFile = Path.Combine(context.TargetDirectory, "Program.cs");
        FileSystem.File.Exists(outputFile).Should().BeTrue("Output file should be created");
        
        var content = await FileSystem.File.ReadAllTextAsync(outputFile);
        content.Should().Contain("namespace MyTestApp;");
        content.Should().Contain("Hello from MyTestApp!");
        content.Should().Contain(DateTime.Now.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_CopiesBinaryFilesAsIs()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        var binaryFile = Path.Combine(templatePath, "icon.png");
        var binaryContent = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        
        FileSystem.AddFile(binaryFile, new MockFileData(binaryContent));
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        var outputFile = Path.Combine(context.TargetDirectory, "icon.png");
        FileSystem.File.Exists(outputFile).Should().BeTrue("Binary file should be copied");
        
        var copiedBytes = FileSystem.File.ReadAllBytes(outputFile);
        copiedBytes.Should().BeEquivalentTo(binaryContent);
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_ProcessesDirectoryStructure()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        var subDir = Path.Combine(templatePath, "{{ProjectName}}.Domain");
        var domainFile = Path.Combine(subDir, "Entity.cs");
        
        CreateTestDirectory(subDir);
        CreateTestFile(domainFile, "namespace {{ProjectName}}.Domain;");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("MyApp");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        var outputDir = Path.Combine(context.TargetDirectory, "MyApp.Domain");
        FileSystem.Directory.Exists(outputDir).Should().BeTrue("Directory should be created with placeholder replaced");
        
        var outputFile = Path.Combine(outputDir, "Entity.cs");
        FileSystem.File.Exists(outputFile).Should().BeTrue("File should be created in processed directory");
        
        var content = await FileSystem.File.ReadAllTextAsync(outputFile);
        content.Should().Contain("namespace MyApp.Domain;");
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_IgnoresSpecifiedDirectories()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        var binDir = Path.Combine(templatePath, "bin");
        var objDir = Path.Combine(templatePath, "obj");
        var gitDir = Path.Combine(templatePath, ".git");
        
        CreateTestDirectory(binDir);
        CreateTestDirectory(objDir);
        CreateTestDirectory(gitDir);
        CreateTestFile(Path.Combine(binDir, "output.exe"), "binary content");
        CreateTestFile(Path.Combine(objDir, "temp.obj"), "object file");
        CreateTestFile(Path.Combine(gitDir, "config"), "git config");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        FileSystem.Directory.Exists(Path.Combine(context.TargetDirectory, "bin")).Should().BeFalse();
        FileSystem.Directory.Exists(Path.Combine(context.TargetDirectory, "obj")).Should().BeFalse();
        FileSystem.Directory.Exists(Path.Combine(context.TargetDirectory, ".git")).Should().BeFalse();
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_ReportsAffectedFiles()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        CreateTestFile(Path.Combine(templatePath, "Program.cs"), "// Template");
        CreateTestFile(Path.Combine(templatePath, "README.md"), "# {{ProjectName}}");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        result.AffectedFiles.Should().HaveCount(2);
        result.AffectedFiles.Should().Contain(file => file.EndsWith("Program.cs"));
        result.AffectedFiles.Should().Contain(file => file.EndsWith("README.md"));
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_CallsPostProcessHook()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        CreateTestFile(Path.Combine(templatePath, "test.txt"), "content");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        initializer.PostProcessWasCalled.Should().BeTrue("PostProcessTemplateAsync should be called");
    }

    [Fact]
    public async Task TemplateInitializer_ExecuteAsync_CallsContentProcessHook()
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        CreateTestFile(Path.Combine(templatePath, "test.cs"), "// Original content");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        var outputFile = Path.Combine(context.TargetDirectory, "test.cs");
        var content = await FileSystem.File.ReadAllTextAsync(outputFile);
        content.Should().Contain("// Original content\n// Processed by test initializer");
    }

    [Fact]
    public void TemplateInitializer_ReplacePlaceholdersWithCustom_AppliesCustomPlaceholders()
    {
        // Arrange
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("TestProject");
        var content = "Project: {{ProjectName}}, Custom: {{CustomValue}}, Another: {{AnotherValue}}";
        var customPlaceholders = new Dictionary<string, string>
        {
            ["{{CustomValue}}"] = "MyCustomValue",
            ["{{AnotherValue}}"] = "AnotherCustomValue"
        };

        // Act
        var result = initializer.TestReplacePlaceholdersWithCustom(content, context, customPlaceholders);

        // Assert
        result.Should().Contain("Project: TestProject");
        result.Should().Contain("Custom: MyCustomValue");
        result.Should().Contain("Another: AnotherCustomValue");
    }

    [Theory]
    [InlineData(".cs")]
    [InlineData(".csproj")]
    [InlineData(".json")]
    [InlineData(".md")]
    [InlineData(".txt")]
    public async Task TemplateInitializer_ExecuteAsync_ProcessesTemplateExtensions(string extension)
    {
        // Arrange
        var templatePath = SetupTemplateDirectory();
        var templateFile = Path.Combine(templatePath, $"test{extension}");
        CreateTestFile(templateFile, "{{ProjectName}}");
        
        var initializer = new TestTemplateInitializer(FileSystem, TemplateDirectory);
        var context = CreateTestContext("MyProject");

        // Act
        var result = await initializer.ExecuteAsync(context);

        // Assert
        result.Success.Should().BeTrue();
        
        var outputFile = Path.Combine(context.TargetDirectory, $"test{extension}");
        var content = await FileSystem.File.ReadAllTextAsync(outputFile);
        content.Should().Contain("MyProject", $"Files with {extension} extension should be processed as templates");
    }

    private string SetupTemplateDirectory()
    {
        var templatePath = Path.Combine("/test", "Templates", TemplateDirectory);
        CreateTestDirectory(templatePath);
        return templatePath;
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
    /// Test implementation of TemplateInitializer
    /// </summary>
    private class TestTemplateInitializer : TemplateInitializer
    {
        private readonly System.IO.Abstractions.IFileSystem _fileSystem;
        private readonly string _templateDirectory;

        public TestTemplateInitializer(System.IO.Abstractions.IFileSystem fileSystem, string templateDirectory)
        {
            _fileSystem = fileSystem;
            _templateDirectory = templateDirectory;
        }

        public override string Id => "test-template-initializer";
        public override string Name => "Test Template Initializer";
        public override string Description => "Test implementation of TemplateInitializer";

        protected override string TemplateDirectory => _templateDirectory;
        protected override string TemplateBasePath => "/test/Templates";

        public bool PostProcessWasCalled { get; private set; }

        protected override Task PostProcessTemplateAsync(InitializationContext context, InitializationResult result)
        {
            PostProcessWasCalled = true;
            return Task.CompletedTask;
        }

        protected override Task<string> ProcessTemplateContentAsync(string content, string templateFile, string targetFile, InitializationContext context)
        {
            if (Path.GetExtension(templateFile) == ".cs")
            {
                return Task.FromResult(content + "\n// Processed by test initializer");
            }
            return Task.FromResult(content);
        }

        // Expose protected method for testing
        public string TestReplacePlaceholdersWithCustom(string content, InitializationContext context, Dictionary<string, string> customPlaceholders)
        {
            return ReplacePlaceholdersWithCustom(content, context, customPlaceholders);
        }

        // Override file system operations to use our test file system
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

            await _fileSystem.File.WriteAllTextAsync(filePath, content);
        }

        protected override async Task<bool> CopyFileAsync(string sourcePath, string destinationPath, InitializationContext context)
        {
            if (!_fileSystem.File.Exists(sourcePath))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureDirectoryExists(directory);
            }

            _fileSystem.File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }
    }
}