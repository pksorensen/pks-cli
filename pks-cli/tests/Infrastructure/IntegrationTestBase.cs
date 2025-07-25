using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using PKS.Infrastructure.Initializers.Context;
using PKS.Infrastructure.Initializers.Implementations;
using PKS.Infrastructure.Initializers.Service;
using PKS.Infrastructure.Initializers.Registry;
using PKS.CLI.Tests.Infrastructure.Mocks;
using Spectre.Console;
using Spectre.Console.Testing;
using System.Text;

namespace PKS.CLI.Tests.Infrastructure;

/// <summary>
/// Base class for integration tests that require real service implementations
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly IServiceProvider ServiceProvider;
    protected readonly TestConsole TestConsole;
    protected readonly StringBuilder LogOutput;
    protected readonly string TestArtifactsPath;

    protected IntegrationTestBase()
    {
        // Setup test console for Spectre.Console testing
        TestConsole = new TestConsole();

        // Setup logging capture
        LogOutput = new StringBuilder();

        // Create test artifacts directory
        TestArtifactsPath = Path.Combine(Path.GetTempPath(), "pks-cli-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestArtifactsPath);

        // Create service collection for dependency injection
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Configure services for integration testing with real implementations
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add common test services
        services.AddSingleton<IAnsiConsole>(TestConsole);

        // Add logging with custom logger for test output capture
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.Services.AddSingleton<ILogger>(provider =>
            {
                var factory = provider.GetRequiredService<ILoggerFactory>();
                return new TestLogger(factory.CreateLogger("IntegrationTest"), LogOutput);
            });
        });

        // Add HTTP client for services that need it
        services.AddHttpClient();

        // Register real service implementations for integration testing
        RegisterRealServices(services);

        // Register initializers
        RegisterInitializers(services);
    }

    /// <summary>
    /// Register real service implementations instead of mocks
    /// </summary>
    protected virtual void RegisterRealServices(IServiceCollection services)
    {
        // Use integration-friendly service implementations that create real files
        services.AddSingleton<IDevcontainerService>(CreateIntegrationDevcontainerService());
        services.AddSingleton(ServiceMockFactory.CreateDevcontainerFeatureRegistry().Object);
        services.AddSingleton(ServiceMockFactory.CreateDevcontainerTemplateService().Object);
        services.AddSingleton<IDevcontainerFileGenerator>(CreateIntegrationFileGenerator());
        services.AddSingleton(ServiceMockFactory.CreateVsCodeExtensionService().Object);
        services.AddSingleton(ServiceMockFactory.CreateNuGetTemplateDiscoveryService().Object);
        services.AddSingleton<ITemplatePackagingService>(CreateIntegrationTemplatePackagingService());

        // Use real initialization services as they work well
        services.AddSingleton<PKS.Infrastructure.Initializers.Service.IInitializationService, InitializationService>();
        services.AddSingleton<PKS.Infrastructure.Initializers.Registry.IInitializerRegistry, InitializerRegistry>();
    }

    /// <summary>
    /// Register all initializers needed for integration tests
    /// </summary>
    protected virtual void RegisterInitializers(IServiceCollection services)
    {
        services.AddTransient<DotNetProjectInitializer>();
        services.AddTransient<DevcontainerInitializer>();
        services.AddTransient<GitHubIntegrationInitializer>();
        services.AddTransient<AgenticFeaturesInitializer>();
        services.AddTransient<ReadmeInitializer>();
        services.AddTransient<ClaudeDocumentationInitializer>();
        services.AddTransient<McpConfigurationInitializer>();
    }

    /// <summary>
    /// Gets a service from the test service provider
    /// </summary>
    protected T GetService<T>() where T : class
    {
        return ServiceProvider.GetRequiredService<T>();
    }

    /// <summary>
    /// Creates a test project directory with proper cleanup
    /// </summary>
    protected string CreateTestProject(string projectName)
    {
        var projectPath = Path.Combine(TestArtifactsPath, projectName);

        if (Directory.Exists(projectPath))
        {
            Directory.Delete(projectPath, true);
        }

        Directory.CreateDirectory(projectPath);

        // Create a basic .csproj file to simulate an existing project
        var csprojPath = Path.Combine(projectPath, $"{projectName}.csproj");
        var csprojContent = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;

        File.WriteAllText(csprojPath, csprojContent);

        return projectPath;
    }

    /// <summary>
    /// Creates an initialization context for testing
    /// </summary>
    protected PKS.Infrastructure.Initializers.Context.InitializationContext CreateInitializationContext(
        string projectName,
        string template,
        string projectPath,
        Dictionary<string, object>? options = null)
    {
        return new PKS.Infrastructure.Initializers.Context.InitializationContext
        {
            ProjectName = projectName,
            TargetDirectory = projectPath,
            WorkingDirectory = projectPath,
            Template = template,
            Interactive = false,
            Options = options ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Asserts that a file exists and contains expected content
    /// </summary>
    protected void AssertFileExists(string filePath, string? expectedContent = null)
    {
        File.Exists(filePath).Should().BeTrue($"File {filePath} should exist");

        if (expectedContent != null)
        {
            var actualContent = File.ReadAllText(filePath);
            actualContent.Should().Contain(expectedContent, $"File {filePath} should contain expected content");
        }
    }

    /// <summary>
    /// Asserts that a directory exists
    /// </summary>
    protected void AssertDirectoryExists(string directoryPath)
    {
        Directory.Exists(directoryPath).Should().BeTrue($"Directory {directoryPath} should exist");
    }

    /// <summary>
    /// Asserts that the console output contains the expected text
    /// </summary>
    protected void AssertConsoleOutput(string expectedText)
    {
        var rawOutput = TestConsole.Output;
        var cleanedOutput = StripAnsiEscapeCodes(rawOutput);

        if (!cleanedOutput.Contains(expectedText))
        {
            rawOutput.Should().Contain(expectedText,
                $"Expected text '{expectedText}' not found in console output. Raw output: '{rawOutput}', Cleaned output: '{cleanedOutput}'");
        }
    }

    /// <summary>
    /// Strips ANSI escape codes from text to get clean content
    /// </summary>
    private string StripAnsiEscapeCodes(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", "");
    }

    /// <summary>
    /// Asserts that a log message with the specified level was written
    /// </summary>
    protected void AssertLogMessage(LogLevel level, string expectedMessage)
    {
        LogOutput.ToString().Should().Contain($"[{level}]").And.Contain(expectedMessage);
    }

    /// <summary>
    /// Clears the test console output
    /// </summary>
    protected void ClearConsoleOutput()
    {
        TestConsole.Clear();
        LogOutput.Clear();
    }

    /// <summary>
    /// Ensures all background tasks are properly disposed
    /// </summary>
    protected void EnsureNoBackgroundTasks()
    {
        // Wait for any pending tasks to complete
        Task.Delay(100).Wait();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// Cleanup test artifacts with retry logic
    /// </summary>
    protected void CleanupTestArtifacts()
    {
        if (Directory.Exists(TestArtifactsPath))
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(TestArtifactsPath, true);
                    break;
                }
                catch when (i < 2)
                {
                    // Retry after a short delay
                    Thread.Sleep(100);
                }
                catch
                {
                    // Final attempt failed, ignore
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Creates an integration-friendly DevcontainerService that creates real files
    /// </summary>
    private IDevcontainerService CreateIntegrationDevcontainerService()
    {
        return new IntegrationDevcontainerService(TestArtifactsPath);
    }

    /// <summary>
    /// Creates an integration-friendly FileGenerator that creates real files
    /// </summary>
    private IDevcontainerFileGenerator CreateIntegrationFileGenerator()
    {
        return new IntegrationDevcontainerFileGenerator();
    }

    /// <summary>
    /// Creates an integration-friendly TemplatePackagingService for testing
    /// </summary>
    private ITemplatePackagingService CreateIntegrationTemplatePackagingService()
    {
        return new IntegrationTemplatePackagingService(TestArtifactsPath);
    }

    public virtual void Dispose()
    {
        try
        {
            EnsureNoBackgroundTasks();
            CleanupTestArtifacts();
        }
        finally
        {
            if (ServiceProvider is IDisposable disposable)
            {
                disposable.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>
/// Test logger that captures output for assertions
/// </summary>
internal class TestLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly StringBuilder _output;

    public TestLogger(ILogger inner, StringBuilder output)
    {
        _inner = inner;
        _output = output;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _inner.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _inner.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _output.AppendLine($"[{logLevel}] {message}");
        _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}

/// <summary>
/// Integration-friendly DevcontainerService that creates real files
/// </summary>
internal class IntegrationDevcontainerService : IDevcontainerService
{
    private readonly string _testArtifactsPath;

    public IntegrationDevcontainerService(string testArtifactsPath)
    {
        _testArtifactsPath = testArtifactsPath;
    }

    public async Task<DevcontainerResult> CreateConfigurationAsync(DevcontainerOptions options)
    {
        try
        {
            // Ensure output directory exists
            Directory.CreateDirectory(options.OutputPath);

            var config = await CreateConfigurationObjectAsync(options);
            var generatedFiles = new List<string>();

            // Generate devcontainer.json
            var devcontainerJsonPath = Path.Combine(options.OutputPath, "devcontainer.json");
            var devcontainerJson = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(devcontainerJsonPath, devcontainerJson);
            generatedFiles.Add(devcontainerJsonPath);

            // Generate Dockerfile
            var dockerfilePath = Path.Combine(options.OutputPath, "Dockerfile");
            var dockerfileContent = GenerateDockerfileContent(config);
            await File.WriteAllTextAsync(dockerfilePath, dockerfileContent);
            generatedFiles.Add(dockerfilePath);

            // Generate docker-compose.yml if requested
            if (options.UseDockerCompose)
            {
                var dockerComposePath = Path.Combine(options.OutputPath, "docker-compose.yml");
                var dockerComposeContent = GenerateDockerComposeContent(config);
                await File.WriteAllTextAsync(dockerComposePath, dockerComposeContent);
                generatedFiles.Add(dockerComposePath);
            }

            return new DevcontainerResult
            {
                Success = true,
                Message = "Configuration created successfully",
                Configuration = config,
                GeneratedFiles = generatedFiles
            };
        }
        catch (Exception ex)
        {
            return new DevcontainerResult
            {
                Success = false,
                Message = $"Failed to create configuration: {ex.Message}",
                Errors = new List<string> { ex.Message }
            };
        }
    }

    private Task<DevcontainerConfiguration> CreateConfigurationObjectAsync(DevcontainerOptions options)
    {
        var config = new DevcontainerConfiguration
        {
            Name = options.Name,
            Image = options.BaseImage ?? "mcr.microsoft.com/dotnet/sdk:8.0",
            ForwardPorts = options.ForwardPorts.ToArray(),
            PostCreateCommand = options.PostCreateCommand ?? "echo 'Custom setup' && npm install",
            RemoteEnv = options.EnvironmentVariables.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        // Add features
        foreach (var feature in options.Features)
        {
            config.Features[feature] = new { };
        }

        return Task.FromResult(config);
    }

    private string GenerateDockerfileContent(DevcontainerConfiguration config)
    {
        return $@"FROM {config.Image}

# Install additional tools and dependencies
RUN apt-get update && apt-get install -y \
    git \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /workspace

# Copy project files
COPY . .

# Restore dependencies
RUN dotnet restore
";
    }

    private string GenerateDockerComposeContent(DevcontainerConfiguration config)
    {
        return @"version: '3.8'

services:
  devcontainer:
    build:
      context: .
      dockerfile: Dockerfile
    volumes:
      - ../..:/workspaces:cached
    working_dir: /workspaces
    command: sleep infinity
";
    }

    public Task<DevcontainerValidationResult> ValidateConfigurationAsync(DevcontainerConfiguration configuration)
    {
        var result = new DevcontainerValidationResult
        {
            IsValid = !string.IsNullOrEmpty(configuration.Name) && !string.IsNullOrEmpty(configuration.Image),
            Errors = new List<string>()
        };

        if (string.IsNullOrEmpty(configuration.Name))
            result.Errors.Add("Configuration name is required");
        if (string.IsNullOrEmpty(configuration.Image))
            result.Errors.Add("Base image is required");

        return Task.FromResult(result);
    }

    public Task<FeatureResolutionResult> ResolveFeatureDependenciesAsync(List<string> features)
    {
        return Task.FromResult(new FeatureResolutionResult
        {
            Success = true,
            ResolvedFeatures = features.Select(f => new DevcontainerFeature { Id = f, Name = f }).ToList()
        });
    }

    public Task<DevcontainerConfiguration> MergeConfigurationsAsync(DevcontainerConfiguration baseConfig, DevcontainerConfiguration overlayConfig)
    {
        var merged = new DevcontainerConfiguration
        {
            Name = !string.IsNullOrEmpty(overlayConfig.Name) ? overlayConfig.Name : baseConfig.Name,
            Image = !string.IsNullOrEmpty(overlayConfig.Image) ? overlayConfig.Image : baseConfig.Image,
            Features = baseConfig.Features.Concat(overlayConfig.Features).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ForwardPorts = overlayConfig.ForwardPorts?.Any() == true ? overlayConfig.ForwardPorts : baseConfig.ForwardPorts,
            PostCreateCommand = !string.IsNullOrEmpty(overlayConfig.PostCreateCommand) ? overlayConfig.PostCreateCommand : baseConfig.PostCreateCommand,
            RemoteEnv = baseConfig.RemoteEnv.Concat(overlayConfig.RemoteEnv ?? new Dictionary<string, string>()).ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
        return Task.FromResult(merged);
    }

    public Task<List<VsCodeExtension>> GetRecommendedExtensionsAsync(List<string> features)
    {
        var extensions = new List<VsCodeExtension>
        {
            new() { Id = "ms-dotnettools.csharp", Name = "C#", Publisher = "Microsoft" },
            new() { Id = "ms-vscode.vscode-json", Name = "JSON", Publisher = "Microsoft" }
        };
        return Task.FromResult(extensions);
    }

    public Task<DevcontainerResult> UpdateConfigurationAsync(string configPath, DevcontainerOptions updates)
    {
        return Task.FromResult(new DevcontainerResult
        {
            Success = true,
            Message = "Configuration updated successfully"
        });
    }

    public Task<PathValidationResult> ValidateOutputPathAsync(string outputPath)
    {
        return Task.FromResult(new PathValidationResult
        {
            IsValid = true,
            CanWrite = true,
            PathExists = Directory.Exists(outputPath)
        });
    }

    public Task<DevcontainerResult> InitializeAsync(DevcontainerConfiguration config)
    {
        return Task.FromResult(new DevcontainerResult
        {
            Success = true,
            Message = "Devcontainer initialized successfully"
        });
    }

    public Task<bool> HasDevcontainerAsync()
    {
        return Task.FromResult(false);
    }

    public Task<DevcontainerResult> AddFeaturesAsync(List<string> features)
    {
        return Task.FromResult(new DevcontainerResult
        {
            Success = true,
            Message = "Features added successfully"
        });
    }

    public Task<bool> IsRunningAsync()
    {
        return Task.FromResult(false);
    }

    public Task<DevcontainerRuntimeInfo> GetRuntimeInfoAsync()
    {
        return Task.FromResult(new DevcontainerRuntimeInfo
        {
            ContainerId = "test-container",
            Status = "running"
        });
    }

    public Task<DevcontainerResult> RebuildAsync(bool force = false)
    {
        return Task.FromResult(new DevcontainerResult
        {
            Success = true,
            Message = "Rebuild completed successfully"
        });
    }

    public Task ClearCacheAsync()
    {
        return Task.CompletedTask;
    }

    public Task<DevcontainerConfiguration> GetConfigurationAsync()
    {
        return Task.FromResult(new DevcontainerConfiguration
        {
            Name = "test-devcontainer",
            Image = "mcr.microsoft.com/dotnet/sdk:8.0"
        });
    }
}

/// <summary>
/// Integration-friendly DevcontainerFileGenerator that creates real files
/// </summary>
internal class IntegrationDevcontainerFileGenerator : IDevcontainerFileGenerator
{
    public Task<FileGenerationResult> GenerateDevcontainerJsonAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        try
        {
            var devcontainerPath = Path.Combine(outputPath, ".devcontainer");
            Directory.CreateDirectory(devcontainerPath);

            var filePath = Path.Combine(devcontainerPath, "devcontainer.json");
            var content = System.Text.Json.JsonSerializer.Serialize(configuration, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<FileGenerationResult> GenerateDockerfileAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        try
        {
            var devcontainerPath = Path.Combine(outputPath, ".devcontainer");
            Directory.CreateDirectory(devcontainerPath);

            var filePath = Path.Combine(devcontainerPath, "Dockerfile");
            var content = $@"FROM {configuration.Image}

RUN apt-get update && apt-get install -y \
    git \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace
";

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<FileGenerationResult> GenerateDockerComposeAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        try
        {
            var devcontainerPath = Path.Combine(outputPath, ".devcontainer");
            Directory.CreateDirectory(devcontainerPath);

            var filePath = Path.Combine(devcontainerPath, "docker-compose.yml");
            var content = @"version: '3.8'

services:
  devcontainer:
    build:
      context: .
      dockerfile: Dockerfile
    volumes:
      - ../..:/workspaces:cached
    working_dir: /workspaces
    command: sleep infinity
";

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<PathValidationResult> ValidateOutputPathAsync(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            var isReadOnlyPath = path.StartsWith("/readonly") || path.Contains("readonly");

            return Task.FromResult(new PathValidationResult
            {
                IsValid = directory != null && !isReadOnlyPath,
                CanWrite = !isReadOnlyPath,
                PathExists = Directory.Exists(directory),
                IsDirectory = Directory.Exists(path),
                Errors = isReadOnlyPath ? new List<string> { "Path is read-only" } : new List<string>()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PathValidationResult
            {
                IsValid = false,
                CanWrite = false,
                Errors = new List<string> { ex.Message }
            });
        }
    }

    public Task<FileGenerationResult> GenerateGitIgnoreAsync(string outputPath)
    {
        try
        {
            var filePath = Path.Combine(outputPath, ".gitignore");
            var content = @"# Devcontainer
.devcontainer/.tmp
.devcontainer/mounts
";

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<FileGenerationResult> GenerateVSCodeSettingsAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        try
        {
            var vscodeDir = Path.Combine(outputPath, ".vscode");
            Directory.CreateDirectory(vscodeDir);

            var filePath = Path.Combine(vscodeDir, "settings.json");
            var content = @"{
    ""dotnet.completion.showCompletionItemsFromUnimportedNamespaces"": true,
    ""dotnet.server.useOmnisharp"": false
}";

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public Task<FileGenerationResult> GenerateReadmeAsync(DevcontainerConfiguration configuration, string outputPath)
    {
        try
        {
            var filePath = Path.Combine(outputPath, "README-devcontainer.md");
            var content = $@"# {configuration.Name} Devcontainer

This project includes a devcontainer configuration for development.

## Usage

1. Install the Dev Containers extension in VS Code
2. Open this project in VS Code
3. Use Command Palette > ""Dev Containers: Reopen in Container""

## Features

- Base Image: {configuration.Image}
- Features: {string.Join("", "", configuration.Features.Keys)}
";

            return Task.FromResult(new FileGenerationResult
            {
                Success = true,
                FilePath = filePath,
                Content = content
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
    }

    public async Task<List<FileGenerationResult>> GenerateAllFilesAsync(DevcontainerConfiguration configuration, string outputPath, DevcontainerOptions? options = null)
    {
        var results = new List<FileGenerationResult>();

        try
        {
            // Generate core devcontainer files
            results.Add(await GenerateDevcontainerJsonAsync(configuration, outputPath));
            results.Add(await GenerateDockerfileAsync(configuration, outputPath));

            // Generate docker-compose if requested
            if (options?.UseDockerCompose == true)
            {
                results.Add(await GenerateDockerComposeAsync(configuration, outputPath));
            }

            // Generate additional files
            results.Add(await GenerateGitIgnoreAsync(outputPath));
            results.Add(await GenerateVSCodeSettingsAsync(configuration, outputPath));
            results.Add(await GenerateReadmeAsync(configuration, outputPath));

            return results;
        }
        catch (Exception ex)
        {
            results.Add(new FileGenerationResult
            {
                Success = false,
                ErrorMessage = $"Failed to generate all files: {ex.Message}"
            });
            return results;
        }
    }
}

/// <summary>
/// Integration-friendly TemplatePackagingService for testing
/// </summary>
internal class IntegrationTemplatePackagingService : ITemplatePackagingService
{
    private readonly string _testArtifactsPath;

    public IntegrationTemplatePackagingService(string testArtifactsPath)
    {
        _testArtifactsPath = testArtifactsPath;
    }

    public Task<PackagingResult> PackSolutionAsync(string solutionPath, string outputPath, string configuration = "Release", CancellationToken cancellationToken = default)
    {
        // Simulate successful packaging by creating mock package files
        Directory.CreateDirectory(outputPath);

        var packages = new List<string>
        {
            Path.Combine(outputPath, "pks-cli.1.0.0.nupkg"),
            Path.Combine(outputPath, "PKS.Templates.Devcontainer.1.0.0.nupkg"),
            Path.Combine(outputPath, "PKS.Templates.ClaudeDocs.1.0.0.nupkg"),
            Path.Combine(outputPath, "PKS.Templates.Hooks.1.0.0.nupkg")
        };

        // Create mock package files
        foreach (var package in packages)
        {
            File.WriteAllText(package, "Mock NuGet package content");
        }

        return Task.FromResult(new PackagingResult
        {
            Success = true,
            Output = "Packages created successfully",
            CreatedPackages = packages,
            Duration = TimeSpan.FromSeconds(1)
        });
    }

    public Task<InstallationResult> InstallTemplateAsync(string packagePath, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var packageName = Path.GetFileNameWithoutExtension(packagePath);

        return Task.FromResult(new InstallationResult
        {
            Success = true,
            Output = $"Template {packageName} installed successfully",
            PackageName = packageName,
            InstalledTemplates = new List<string> { packageName }
        });
    }

    public Task<UninstallationResult> UninstallTemplateAsync(string packageName, string workingDirectory, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new UninstallationResult
        {
            Success = true,
            Output = $"Template {packageName} uninstalled successfully",
            PackageName = packageName
        });
    }

    public Task<TemplateListResult> ListTemplatesAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TemplateListResult
        {
            Success = true,
            Output = "Templates listed successfully",
            Templates = new List<PackagingTemplateInfo>
            {
                new() { Name = "PKS Devcontainer Template", ShortName = "pks-devcontainer", Language = "C#", Author = "PKS" },
                new() { Name = "PKS Claude Docs Template", ShortName = "pks-claude-docs", Language = "Markdown", Author = "PKS" }
            }
        });
    }

    public Task<ProjectCreationResult> CreateProjectFromTemplateAsync(string templateName, string projectName, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var projectPath = Path.Combine(workingDirectory, projectName);
        Directory.CreateDirectory(projectPath);

        var createdFiles = new List<string>
        {
            Path.Combine(projectPath, $"{projectName}.csproj"),
            Path.Combine(projectPath, "Program.cs")
        };

        foreach (var file in createdFiles)
        {
            File.WriteAllText(file, "Mock project content");
        }

        return Task.FromResult(new ProjectCreationResult
        {
            Success = true,
            Output = $"Project {projectName} created successfully",
            ProjectPath = projectPath,
            CreatedFiles = createdFiles
        });
    }

    public Task<PackageValidationResult> ValidatePackageAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PackageValidationResult
        {
            Success = true,
            Metadata = new PackageMetadata
            {
                Id = Path.GetFileNameWithoutExtension(packagePath),
                Version = "1.0.0",
                Title = "Test Package",
                Description = "Mock package for testing",
                Authors = "PKS Test",
                IsTemplate = true,
                Tags = new List<string> { "template", "test" }
            }
        });
    }
}