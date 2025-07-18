using Moq;
using System.IO.Abstractions;
using PKS.Infrastructure.Initializers;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Base;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Factory for creating mock initializers for testing
/// </summary>
public class MockInitializerFactory
{
    private readonly IFileSystem _fileSystem;

    public MockInitializerFactory(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public Mock<IInitializer> CreateBasicInitializer(
        string id = "test-initializer",
        string name = "Test Initializer",
        string description = "Test initializer for unit tests",
        int order = 100,
        bool shouldRun = true,
        bool shouldSucceed = true)
    {
        var mock = new Mock<IInitializer>();
        
        mock.Setup(x => x.Id).Returns(id);
        mock.Setup(x => x.Name).Returns(name);
        mock.Setup(x => x.Description).Returns(description);
        mock.Setup(x => x.Order).Returns(order);
        
        mock.Setup(x => x.ShouldRunAsync(It.IsAny<InitializationContext>()))
            .ReturnsAsync(shouldRun);
            
        mock.Setup(x => x.ExecuteAsync(It.IsAny<InitializationContext>()))
            .Returns<InitializationContext>(context => 
            {
                if (!shouldSucceed)
                    return Task.FromResult(InitializationResult.CreateFailure("Mock failure"));
                    
                var result = InitializationResult.CreateSuccess("Mock initialization completed");
                result.AffectedFiles.Add($"{context.TargetDirectory}/{context.ProjectName}.csproj");
                return Task.FromResult(result);
            });

        mock.Setup(x => x.GetOptions())
            .Returns(Enumerable.Empty<InitializerOption>());

        return mock;
    }

    public TestTemplateInitializer CreateTemplateInitializer(
        string id = "template-initializer",
        string templatePath = "/templates/console",
        Dictionary<string, string>? templateFiles = null)
    {
        // Setup template files in the mock file system if provided
        if (templateFiles != null)
        {
            _fileSystem.AddDirectory(templatePath);
            foreach (var (fileName, content) in templateFiles)
            {
                var filePath = Path.Combine(templatePath, fileName);
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
                {
                    _fileSystem.AddDirectory(directory);
                }
                _fileSystem.AddFile(filePath, content);
            }
        }

        return new TestTemplateInitializer(id, Path.GetFileName(templatePath), templatePath, _fileSystem);
    }

    public TestCodeInitializer CreateCodeInitializer(
        string id = "code-initializer",
        string name = "Code Initializer",
        Action<InitializationContext>? codeAction = null)
    {
        return new TestCodeInitializer(id, name, _fileSystem, codeAction);
    }

    public TestInitializer CreateTestInitializer(
        string id = "test-initializer",
        string name = "Test Initializer", 
        bool shouldRun = true,
        bool shouldSucceed = true,
        int order = 100)
    {
        return new TestInitializer(id, name, shouldRun, shouldSucceed, order, _fileSystem);
    }

    private static string GetMockProjectFile(string projectName)
    {
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
    }
}

/// <summary>
/// Test implementation of IInitializer for unit testing
/// </summary>
public class TestInitializer : BaseInitializer
{
    private readonly bool _shouldRun;
    private readonly bool _shouldSucceed;
    private readonly IFileSystem _fileSystem;

    public TestInitializer(
        string id,
        string name,
        bool shouldRun = true,
        bool shouldSucceed = true,
        int order = 100,
        IFileSystem? fileSystem = null)
    {
        Id = id;
        Name = name;
        Description = $"Test initializer: {name}";
        Order = order;
        _shouldRun = shouldRun;
        _shouldSucceed = shouldSucceed;
        _fileSystem = fileSystem ?? new System.IO.Abstractions.FileSystem();
    }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override int Order { get; }

    public override Task<bool> ShouldRunAsync(InitializationContext context)
    {
        return Task.FromResult(_shouldRun);
    }

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        if (!_shouldSucceed)
        {
            return InitializationResult.CreateFailure("Test failure");
        }

        // Create a simple test file
        var testFile = Path.Combine(context.TargetDirectory, $"{context.ProjectName}-test.txt");
        await _fileSystem.File.WriteAllTextAsync(testFile, $"Test file created by {Name}");

        var result = InitializationResult.CreateSuccess($"Test initialization completed by {Name}");
        result.AffectedFiles.Add(testFile);
        return result;
    }

    public override IEnumerable<InitializerOption> GetOptions()
    {
        return new[]
        {
            new InitializerOption
            {
                Name = "test-option",
                Description = "A test option",
                DefaultValue = "default-value",
                Required = false
            }
        };
    }
}

/// <summary>
/// Test implementation of TemplateInitializer for unit testing
/// </summary>
public class TestTemplateInitializer : TemplateInitializer
{
    private readonly string _id;
    private readonly string _name;
    private readonly string _templatePath;
    private readonly IFileSystem _fileSystem;

    public TestTemplateInitializer(string id, string name, string templatePath, IFileSystem fileSystem)
    {
        _id = id;
        _name = name;
        _templatePath = templatePath;
        _fileSystem = fileSystem;
    }

    public override string Id => _id;
    public override string Name => _name;
    public override string Description => $"Test template initializer: {_name}";
    protected override string TemplateDirectory => Path.GetFileName(_templatePath);
    protected override string TemplateBasePath => Path.GetDirectoryName(_templatePath) ?? "/templates";

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

/// <summary>
/// Test implementation of CodeInitializer for unit testing
/// </summary>
public class TestCodeInitializer : CodeInitializer
{
    private readonly string _id;
    private readonly string _name;
    private readonly IFileSystem _fileSystem;
    private readonly Action<InitializationContext>? _codeAction;

    public TestCodeInitializer(string id, string name, IFileSystem fileSystem, Action<InitializationContext>? codeAction = null)
    {
        _id = id;
        _name = name;
        _fileSystem = fileSystem;
        _codeAction = codeAction;
    }

    public override string Id => _id;
    public override string Name => _name;
    public override string Description => $"Test code initializer: {_name}";

    protected override async Task ExecuteCodeLogicAsync(InitializationContext context, InitializationResult result)
    {
        _codeAction?.Invoke(context);
        
        // Default behavior - create a simple test file
        var testFile = Path.Combine(context.TargetDirectory, $"{context.ProjectName}.test");
        await _fileSystem.File.WriteAllTextAsync(testFile, $"Test file created by {_name}");
        result.AffectedFiles.Add(testFile);
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

        var processedContent = ReplacePlaceholders(content, context);
        await _fileSystem.File.WriteAllTextAsync(filePath, processedContent);
    }
}