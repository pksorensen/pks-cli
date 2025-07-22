using System.Diagnostics;
using System.Text.Json;

namespace PKS.CLI.Tests.Integration.Devcontainer;

/// <summary>
/// Manages test artifacts for devcontainer tests, ensuring proper cleanup and organization
/// </summary>
public static class DevcontainerTestArtifactManager
{
    private static readonly string BaseTestArtifactsPath = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                     "test-artifacts", "pks-cli", "devcontainer");

    // Safety patterns to prevent creation of artifacts in source directories
    private static readonly string[] ForbiddenPaths = 
    {
        "src", "/src", "\\src", "pks-cli/src", "pks-cli\\src",
        "workspace", "/workspace", "\\workspace"
    };

    private static readonly string[] ForbiddenNames = 
    {
        "src", "source", "code", "workspace", "pks-cli"
    };

    /// <summary>
    /// Validates that a path is safe for test artifact creation
    /// </summary>
    private static void ValidateTestPath(string path)
    {
        var normalizedPath = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        
        // Check against forbidden path patterns
        foreach (var forbiddenPath in ForbiddenPaths)
        {
            var normalizedForbidden = forbiddenPath.Replace('\\', '/').ToLowerInvariant();
            if (normalizedPath.Contains(normalizedForbidden))
            {
                throw new InvalidOperationException(
                    $"Test artifact path '{path}' contains forbidden pattern '{forbiddenPath}'. " +
                    "Test artifacts must not be created in source directories.");
            }
        }
        
        // Check if any part of the path contains forbidden names
        var pathParts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in pathParts)
        {
            if (ForbiddenNames.Contains(part))
            {
                throw new InvalidOperationException(
                    $"Test artifact path '{path}' contains forbidden directory name '{part}'. " +
                    "Test artifacts must not be created in source directories.");
            }
        }
        
        // Ensure the path is under a recognized test artifacts location
        if (!normalizedPath.Contains("test-artifacts") && 
            !normalizedPath.Contains("temp") && 
            !normalizedPath.Contains("tmp"))
        {
            throw new InvalidOperationException(
                $"Test artifact path '{path}' is not in a recognized test artifacts location. " +
                "Paths must contain 'test-artifacts', 'temp', or 'tmp'.");
        }
    }

    /// <summary>
    /// Creates a test artifact directory with automatic cleanup registration
    /// </summary>
    public static string CreateTestDirectory(string testSuiteName, string testName)
    {
        var testPath = Path.Combine(BaseTestArtifactsPath, testSuiteName, testName, DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss"));
        
        // Validate that this is a safe test path
        ValidateTestPath(testPath);
        
        if (Directory.Exists(testPath))
        {
            Directory.Delete(testPath, true);
        }
        
        Directory.CreateDirectory(testPath);
        
        // Register for cleanup
        RegisterForCleanup(testPath);
        
        return testPath;
    }

    /// <summary>
    /// Creates a temporary test directory that will be automatically cleaned up
    /// </summary>
    public static string CreateTempTestDirectory(string prefix = "temp")
    {
        var tempPath = Path.Combine(BaseTestArtifactsPath, "temp", $"{prefix}-{Guid.NewGuid():N}");
        
        // Validate that this is a safe test path
        ValidateTestPath(tempPath);
        
        Directory.CreateDirectory(tempPath);
        
        // Register for immediate cleanup after test
        RegisterForImmediateCleanup(tempPath);
        
        return tempPath;
    }

    /// <summary>
    /// Copies files to the test artifacts directory for preservation
    /// </summary>
    public static async Task<string> PreserveTestArtifactAsync(string sourceFilePath, string testSuiteName, string testName, string? customName = null)
    {
        var testDir = CreateTestDirectory(testSuiteName, testName);
        var fileName = customName ?? Path.GetFileName(sourceFilePath);
        var destinationPath = Path.Combine(testDir, fileName);
        
        // Use synchronous File.Copy since File.CopyAsync doesn't exist
        await Task.Run(() => File.Copy(sourceFilePath, destinationPath, overwrite: true));
        
        return destinationPath;
    }

    /// <summary>
    /// Saves test result information as JSON for later analysis
    /// </summary>
    public static async Task SaveTestResultAsync(string testSuiteName, string testName, object testResult)
    {
        var testDir = CreateTestDirectory(testSuiteName, testName);
        var resultPath = Path.Combine(testDir, "test-result.json");
        
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        
        var json = JsonSerializer.Serialize(testResult, options);
        await File.WriteAllTextAsync(resultPath, json);
    }

    /// <summary>
    /// Creates a test project structure for devcontainer testing
    /// </summary>
    public static async Task<string> CreateTestProjectAsync(string projectName, string template = "console")
    {
        // Validate project name doesn't contain forbidden patterns
        if (ForbiddenNames.Any(name => projectName.ToLowerInvariant().Contains(name)))
        {
            throw new ArgumentException(
                $"Project name '{projectName}' contains forbidden pattern. " +
                "Test projects should not use names that could conflict with source directories.",
                nameof(projectName));
        }
        
        var projectPath = CreateTestDirectory("test-projects", projectName);
        
        // Create basic project structure
        var csprojContent = GenerateCsprojContent(projectName, template);
        await File.WriteAllTextAsync(Path.Combine(projectPath, $"{projectName}.csproj"), csprojContent);
        
        var programContent = GenerateProgramContent(template);
        await File.WriteAllTextAsync(Path.Combine(projectPath, "Program.cs"), programContent);
        
        // Create additional files based on template
        await CreateTemplateSpecificFilesAsync(projectPath, template);
        
        return projectPath;
    }

    /// <summary>
    /// Validates that a devcontainer was created correctly
    /// </summary>
    public static async Task<DevcontainerValidationResult> ValidateDevcontainerAsync(string projectPath)
    {
        var result = new DevcontainerValidationResult();
        var devcontainerPath = Path.Combine(projectPath, ".devcontainer");
        
        // Check directory exists
        result.DevcontainerDirectoryExists = Directory.Exists(devcontainerPath);
        
        if (!result.DevcontainerDirectoryExists)
        {
            result.Errors.Add("Devcontainer directory does not exist");
            return result;
        }
        
        // Check devcontainer.json exists and is valid
        var devcontainerJsonPath = Path.Combine(devcontainerPath, "devcontainer.json");
        result.DevcontainerJsonExists = File.Exists(devcontainerJsonPath);
        
        if (result.DevcontainerJsonExists)
        {
            try
            {
                var jsonContent = await File.ReadAllTextAsync(devcontainerJsonPath);
                var config = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonContent);
                result.DevcontainerJsonValid = config != null;
                
                if (config != null)
                {
                    result.HasName = config.ContainsKey("name");
                    result.HasImage = config.ContainsKey("image") || config.ContainsKey("build");
                    result.ConfigurationKeys = config.Keys.ToList();
                }
            }
            catch (Exception ex)
            {
                result.DevcontainerJsonValid = false;
                result.Errors.Add($"Invalid devcontainer.json: {ex.Message}");
            }
        }
        else
        {
            result.Errors.Add("devcontainer.json does not exist");
        }
        
        // Check for Dockerfile
        var dockerfilePath = Path.Combine(devcontainerPath, "Dockerfile");
        result.DockerfileExists = File.Exists(dockerfilePath);
        
        // Check for additional files
        result.AdditionalFiles = Directory.GetFiles(devcontainerPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(devcontainerPath, f))
            .ToList();
        
        result.IsValid = result.DevcontainerDirectoryExists && 
                        result.DevcontainerJsonExists && 
                        result.DevcontainerJsonValid &&
                        result.HasName;
        
        return result;
    }

    /// <summary>
    /// Compares two devcontainer configurations
    /// </summary>
    public static async Task<DevcontainerComparisonResult> CompareDevcontainersAsync(string path1, string path2)
    {
        var result = new DevcontainerComparisonResult();
        
        var config1Path = Path.Combine(path1, "devcontainer.json");
        var config2Path = Path.Combine(path2, "devcontainer.json");
        
        if (!File.Exists(config1Path) || !File.Exists(config2Path))
        {
            result.Differences.Add("One or both devcontainer.json files do not exist");
            return result;
        }
        
        var content1 = await File.ReadAllTextAsync(config1Path);
        var content2 = await File.ReadAllTextAsync(config2Path);
        
        var config1 = JsonSerializer.Deserialize<Dictionary<string, object>>(content1);
        var config2 = JsonSerializer.Deserialize<Dictionary<string, object>>(content2);
        
        if (config1 == null || config2 == null)
        {
            result.Differences.Add("Failed to parse one or both configurations");
            return result;
        }
        
        // Compare keys
        var keys1 = config1.Keys.ToHashSet();
        var keys2 = config2.Keys.ToHashSet();
        
        var missingIn2 = keys1.Except(keys2);
        var missingIn1 = keys2.Except(keys1);
        
        foreach (var key in missingIn2)
            result.Differences.Add($"Key '{key}' exists in first config but not in second");
        
        foreach (var key in missingIn1)
            result.Differences.Add($"Key '{key}' exists in second config but not in first");
        
        // Compare common keys (excluding name which is expected to be different)
        var commonKeys = keys1.Intersect(keys2).Where(k => k != "name");
        
        foreach (var key in commonKeys)
        {
            var value1Json = JsonSerializer.Serialize(config1[key]);
            var value2Json = JsonSerializer.Serialize(config2[key]);
            
            if (value1Json != value2Json)
            {
                result.Differences.Add($"Key '{key}' has different values");
            }
        }
        
        result.AreIdentical = result.Differences.Count == 0;
        result.StructurallyEquivalent = result.Differences.Count <= 1 && // Allow name difference
                                      result.Differences.All(d => d.Contains("name"));
        
        return result;
    }

    /// <summary>
    /// Cleans up old test artifacts (older than specified days)
    /// </summary>
    public static void CleanupOldArtifacts(int olderThanDays = 7)
    {
        if (!Directory.Exists(BaseTestArtifactsPath))
            return;
        
        var cutoffDate = DateTime.UtcNow.AddDays(-olderThanDays);
        
        var directories = Directory.GetDirectories(BaseTestArtifactsPath, "*", SearchOption.AllDirectories);
        
        foreach (var dir in directories)
        {
            try
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.CreationTimeUtc < cutoffDate)
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Gets a summary of all test artifacts
    /// </summary>
    public static TestArtifactSummary GetArtifactSummary()
    {
        var summary = new TestArtifactSummary();
        
        if (!Directory.Exists(BaseTestArtifactsPath))
        {
            return summary;
        }
        
        var allDirectories = Directory.GetDirectories(BaseTestArtifactsPath, "*", SearchOption.AllDirectories);
        var allFiles = Directory.GetFiles(BaseTestArtifactsPath, "*", SearchOption.AllDirectories);
        
        summary.TotalDirectories = allDirectories.Length;
        summary.TotalFiles = allFiles.Length;
        summary.TotalSizeBytes = allFiles.Sum(f => new FileInfo(f).Length);
        
        summary.TestSuites = Directory.GetDirectories(BaseTestArtifactsPath)
            .Select(d => Path.GetFileName(d))
            .ToList();
        
        summary.OldestArtifact = allDirectories
            .Select(d => new DirectoryInfo(d).CreationTimeUtc)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Min();
        
        summary.NewestArtifact = allDirectories
            .Select(d => new DirectoryInfo(d).CreationTimeUtc)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Max();
        
        return summary;
    }

    private static void RegisterForCleanup(string path)
    {
        // Register path for cleanup - in a real implementation this might use a cleanup service
        // For now, we'll just ensure the directory is marked for temp cleanup
        var cleanupFile = Path.Combine(path, ".cleanup");
        File.WriteAllText(cleanupFile, DateTime.UtcNow.ToString("O"));
    }

    private static void RegisterForImmediateCleanup(string path)
    {
        // Register for cleanup when process exits
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        };
    }

    private static string GenerateCsprojContent(string projectName, string template)
    {
        var outputType = template switch
        {
            "web" => "Library",
            "api" => "Library",
            _ => "Exe"
        };

        var packageReferences = template switch
        {
            "web" => """
                <PackageReference Include="Microsoft.AspNetCore.App" />
                """,
            "api" => """
                <PackageReference Include="Microsoft.AspNetCore.App" />
                <PackageReference Include="Swashbuckle.AspNetCore" Version="6.4.0" />
                """,
            _ => ""
        };

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <OutputType>{outputType}</OutputType>
              </PropertyGroup>
              <ItemGroup>
                {packageReferences}
              </ItemGroup>
            </Project>
            """;
    }

    private static string GenerateProgramContent(string template)
    {
        return template switch
        {
            "web" => """
                var builder = WebApplication.CreateBuilder(args);
                var app = builder.Build();
                
                app.MapGet("/", () => "Hello World!");
                
                app.Run();
                """,
            "api" => """
                var builder = WebApplication.CreateBuilder(args);
                
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();
                
                var app = builder.Build();
                
                if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI();
                }
                
                app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));
                
                app.Run();
                """,
            _ => """
                Console.WriteLine("Hello, World!");
                """
        };
    }

    private static async Task CreateTemplateSpecificFilesAsync(string projectPath, string template)
    {
        switch (template)
        {
            case "web":
            case "api":
                // Create appsettings.json
                var appSettings = """
                    {
                      "Logging": {
                        "LogLevel": {
                          "Default": "Information",
                          "Microsoft.AspNetCore": "Warning"
                        }
                      },
                      "AllowedHosts": "*"
                    }
                    """;
                await File.WriteAllTextAsync(Path.Combine(projectPath, "appsettings.json"), appSettings);
                
                // Create Properties/launchSettings.json
                var propertiesDir = Path.Combine(projectPath, "Properties");
                Directory.CreateDirectory(propertiesDir);
                
                var launchSettings = """
                    {
                      "profiles": {
                        "http": {
                          "commandName": "Project",
                          "dotnetRunMessages": true,
                          "launchBrowser": true,
                          "applicationUrl": "http://localhost:5000",
                          "environmentVariables": {
                            "ASPNETCORE_ENVIRONMENT": "Development"
                          }
                        }
                      }
                    }
                    """;
                await File.WriteAllTextAsync(Path.Combine(propertiesDir, "launchSettings.json"), launchSettings);
                break;
        }
    }
}

/// <summary>
/// Result of devcontainer validation
/// </summary>
public class DevcontainerValidationResult
{
    public bool IsValid { get; set; }
    public bool DevcontainerDirectoryExists { get; set; }
    public bool DevcontainerJsonExists { get; set; }
    public bool DevcontainerJsonValid { get; set; }
    public bool DockerfileExists { get; set; }
    public bool HasName { get; set; }
    public bool HasImage { get; set; }
    public List<string> ConfigurationKeys { get; set; } = new();
    public List<string> AdditionalFiles { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>
/// Result of comparing two devcontainer configurations
/// </summary>
public class DevcontainerComparisonResult
{
    public bool AreIdentical { get; set; }
    public bool StructurallyEquivalent { get; set; }
    public List<string> Differences { get; set; } = new();
}

/// <summary>
/// Summary of test artifacts
/// </summary>
public class TestArtifactSummary
{
    public int TotalDirectories { get; set; }
    public int TotalFiles { get; set; }
    public long TotalSizeBytes { get; set; }
    public List<string> TestSuites { get; set; } = new();
    public DateTime OldestArtifact { get; set; }
    public DateTime NewestArtifact { get; set; }
    
    public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);
    
    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double size = bytes;
        
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }
        
        return $"{size:F1} {suffixes[suffixIndex]}";
    }
}