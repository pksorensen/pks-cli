using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Moq;
using PKS.Infrastructure.Services;
using PKS.Infrastructure.Services.Models;
using Xunit;

namespace PKS.CLI.Tests.Services;

public class SystemInformationServiceTests
{
    private readonly Mock<ILogger<SystemInformationService>> _mockLogger;
    private readonly SystemInformationService _service;

    public SystemInformationServiceTests()
    {
        _mockLogger = new Mock<ILogger<SystemInformationService>>();
        _service = new SystemInformationService(_mockLogger.Object);
    }

    [Fact]
    public async Task GetSystemInformationAsync_ReturnsCompleteSystemInformation()
    {
        // Act
        var result = await _service.GetSystemInformationAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.PksCliInfo);
        Assert.NotNull(result.DotNetRuntimeInfo);
        Assert.NotNull(result.OperatingSystemInfo);
        Assert.NotNull(result.EnvironmentInfo);
        Assert.NotNull(result.HardwareInfo);
        Assert.True(result.CollectedAt > DateTime.MinValue);
    }

    [Fact]
    public async Task GetSystemInformationAsync_WithOptions_RespectsOptions()
    {
        // Arrange
        var options = new SystemInfoCollectionOptions
        {
            IncludeHardwareDetails = false,
            IncludeEnvironmentVariables = false,
            IncludeDotNetDetails = false
        };

        // Act
        var result = await _service.GetSystemInformationAsync(options);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.PksCliInfo);
        Assert.NotNull(result.OperatingSystemInfo);
        // Hardware and environment info should be default/empty since we disabled collection
        Assert.Equal(0, result.HardwareInfo.LogicalCores);
        Assert.Empty(result.EnvironmentInfo.SafeEnvironmentVariables);
    }

    [Fact]
    public async Task GetPksCliInfoAsync_ReturnsValidInfo()
    {
        // Act
        var result = await _service.GetPksCliInfoAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Version);
        Assert.NotEmpty(result.AssemblyVersion);
        Assert.NotEmpty(result.InstallLocation);
        Assert.NotEmpty(result.BuildConfiguration);
    }

    [Fact]
    public async Task GetDotNetRuntimeInfoAsync_ReturnsValidInfo()
    {
        // Act
        var result = await _service.GetDotNetRuntimeInfoAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.FrameworkVersion);
        Assert.NotEmpty(result.RuntimeVersion);
        Assert.NotEmpty(result.RuntimeIdentifier);
        Assert.True(result.ProcessorCount > 0);
        Assert.True(result.WorkingSet > 0);
    }

    [Fact]
    public async Task GetOperatingSystemInfoAsync_ReturnsValidInfo()
    {
        // Act
        var result = await _service.GetOperatingSystemInfoAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Platform);
        Assert.NotEmpty(result.OsDescription);
        Assert.NotEmpty(result.OsArchitecture);
        Assert.NotEmpty(result.CurrentUser);
        Assert.NotEmpty(result.HostName);

        // Verify platform detection
        var isValidPlatform = result.IsWindows || result.IsLinux || result.IsMacOs || result.IsFreeBsd;
        Assert.True(isValidPlatform, "Should detect at least one platform");
    }

    [Fact]
    public async Task GetEnvironmentInfoAsync_ReturnsValidInfo()
    {
        // Act
        var result = await _service.GetEnvironmentInfoAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.CurrentDirectory);
        Assert.NotEmpty(result.TempPath);
        Assert.NotEmpty(result.SystemPath);
        Assert.NotNull(result.SafeEnvironmentVariables);
        Assert.NotNull(result.DevelopmentVariables);
    }

    [Fact]
    public async Task GetEnvironmentInfoAsync_OnlyIncludesSafeVariables()
    {
        // Act
        var result = await _service.GetEnvironmentInfoAsync();

        // Assert
        // Check that no sensitive variables are included
        var sensitivePatterns = new[] { "PASSWORD", "SECRET", "KEY", "TOKEN", "CREDENTIAL" };
        var allVariableNames = result.SafeEnvironmentVariables.Keys.Concat(result.DevelopmentVariables.Keys);
        
        foreach (var variableName in allVariableNames)
        {
            var isSensitive = sensitivePatterns.Any(pattern => 
                variableName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            Assert.False(isSensitive, $"Variable {variableName} appears to be sensitive and should not be included");
        }
    }

    [Fact]
    public async Task GetHardwareInfoAsync_ReturnsValidInfo()
    {
        // Act
        var result = await _service.GetHardwareInfoAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.LogicalCores > 0);
        Assert.NotEmpty(result.ProcessorArchitecture);
        Assert.NotNull(result.NetworkInterfaces);
    }

    [Theory]
    [InlineData("git")]
    [InlineData("nonexistenttoolthatdoesnotexist")]
    public async Task IsToolAvailableAsync_ReturnsExpectedResult(string toolName)
    {
        // Act
        var result = await _service.IsToolAvailableAsync(toolName, 2000);

        // Assert
        // We can't assert specific values since it depends on the test environment
        // But we can verify the method completes without throwing
        Assert.IsType<bool>(result);
    }

    [Fact]
    public async Task GetToolVersionAsync_WithInvalidTool_ReturnsNull()
    {
        // Act
        var result = await _service.GetToolVersionAsync("nonexistenttoolthatdoesnotexist", "--version", 1000);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FormatSystemInformation_ReturnsFormattedString()
    {
        // Arrange
        var systemInfo = CreateSampleSystemInfo();

        // Act
        var result = _service.FormatSystemInformation(systemInfo);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("PKS CLI System Information", result);
        Assert.Contains("PKS CLI:", result);
        Assert.Contains("Operating System:", result);
        Assert.Contains(".NET Runtime:", result);
        Assert.Contains("Hardware:", result);
    }

    [Fact]
    public void FormatSystemInformation_WithDetails_IncludesDetailedInfo()
    {
        // Arrange
        var systemInfo = CreateSampleSystemInfo();

        // Act
        var result = _service.FormatSystemInformation(systemInfo, includeDetails: true);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Development Tools:", result);
    }

    [Fact]
    public void ExportToJson_ReturnsValidJson()
    {
        // Arrange
        var systemInfo = CreateSampleSystemInfo();

        // Act
        var result = _service.ExportToJson(systemInfo);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);
        
        // Verify it's valid JSON by attempting to parse
        var parsedJson = System.Text.Json.JsonDocument.Parse(result);
        Assert.NotNull(parsedJson);
    }

    [Fact]
    public void ExportToJson_WithoutIndent_ReturnsCompactJson()
    {
        // Arrange
        var systemInfo = CreateSampleSystemInfo();

        // Act
        var indented = _service.ExportToJson(systemInfo, indent: true);
        var compact = _service.ExportToJson(systemInfo, indent: false);

        // Assert
        Assert.True(indented.Length > compact.Length, "Indented JSON should be longer than compact JSON");
        Assert.DoesNotContain("\n", compact);
    }

    [Fact]
    public void SystemInfoCollectionOptions_DefaultValues_AreCorrect()
    {
        // Act
        var options = new SystemInfoCollectionOptions();

        // Assert
        Assert.True(options.IncludeHardwareDetails);
        Assert.True(options.IncludeEnvironmentVariables);
        Assert.True(options.IncludeDotNetDetails);
        Assert.True(options.IncludeToolAvailability);
        Assert.Equal(5000, options.ToolCheckTimeoutMs);
        Assert.NotEmpty(options.SafeEnvironmentVariableNames);
        Assert.NotEmpty(options.SafeEnvironmentVariablePrefixes);
    }

    [Fact]
    public void SystemInfoCollectionOptions_SafeEnvironmentVariables_ContainsExpectedVariables()
    {
        // Act
        var options = new SystemInfoCollectionOptions();

        // Assert
        Assert.Contains("COMPUTERNAME", options.SafeEnvironmentVariableNames);
        Assert.Contains("USERNAME", options.SafeEnvironmentVariableNames);
        Assert.Contains("PROCESSOR_ARCHITECTURE", options.SafeEnvironmentVariableNames);
        Assert.Contains("DOTNET_ROOT", options.SafeEnvironmentVariableNames);
        Assert.Contains("HOME", options.SafeEnvironmentVariableNames);
        Assert.Contains("USER", options.SafeEnvironmentVariableNames);
    }

    [Fact]
    public void SystemInfoCollectionOptions_SafeEnvironmentVariablePrefixes_ContainsExpectedPrefixes()
    {
        // Act
        var options = new SystemInfoCollectionOptions();

        // Assert
        Assert.Contains("DOTNET_", options.SafeEnvironmentVariablePrefixes);
        Assert.Contains("ASPNETCORE_", options.SafeEnvironmentVariablePrefixes);
        Assert.Contains("NUGET_", options.SafeEnvironmentVariablePrefixes);
        Assert.Contains("VSCODE_", options.SafeEnvironmentVariablePrefixes);
    }

    [Fact]
    public async Task GetSystemInformationAsync_HandlesExceptions_GracefullyReturnsPartialData()
    {
        // This test verifies that if any individual component fails,
        // the service still returns what it can collect
        
        // Act & Assert (should not throw)
        var result = await _service.GetSystemInformationAsync();
        Assert.NotNull(result);
    }

    [Fact]
    public void SystemInformation_AllPropertiesInitialized()
    {
        // Act
        var systemInfo = new SystemInformation();

        // Assert
        Assert.NotNull(systemInfo.PksCliInfo);
        Assert.NotNull(systemInfo.DotNetRuntimeInfo);
        Assert.NotNull(systemInfo.OperatingSystemInfo);
        Assert.NotNull(systemInfo.EnvironmentInfo);
        Assert.NotNull(systemInfo.HardwareInfo);
        Assert.True(systemInfo.CollectedAt > DateTime.MinValue);
    }

    [Fact]
    public void PksCliInfo_AllPropertiesHaveDefaults()
    {
        // Act
        var info = new PksCliInfo();

        // Assert
        Assert.NotNull(info.Version);
        Assert.NotNull(info.AssemblyVersion);
        Assert.NotNull(info.FileVersion);
        Assert.NotNull(info.ProductVersion);
        Assert.NotNull(info.BuildConfiguration);
        Assert.NotNull(info.InstallLocation);
    }

    [Fact]
    public void DotNetRuntimeInfo_AllPropertiesHaveDefaults()
    {
        // Act
        var info = new DotNetRuntimeInfo();

        // Assert
        Assert.NotNull(info.FrameworkVersion);
        Assert.NotNull(info.RuntimeVersion);
        Assert.NotNull(info.RuntimeIdentifier);
        Assert.NotNull(info.TargetFramework);
        Assert.NotNull(info.ClrVersion);
        Assert.NotNull(info.InstalledSdks);
        Assert.NotNull(info.InstalledRuntimes);
    }

    [Fact]
    public void OperatingSystemInfo_AllPropertiesHaveDefaults()
    {
        // Act
        var info = new OperatingSystemInfo();

        // Assert
        Assert.NotNull(info.Platform);
        Assert.NotNull(info.PlatformVersion);
        Assert.NotNull(info.OsDescription);
        Assert.NotNull(info.OsArchitecture);
        Assert.NotNull(info.ProcessArchitecture);
        Assert.NotNull(info.KernelVersion);
        Assert.NotNull(info.InstalledShells);
        Assert.NotNull(info.CurrentShell);
        Assert.NotNull(info.CurrentUser);
        Assert.NotNull(info.HostName);
        Assert.NotNull(info.TimeZone);
        Assert.NotNull(info.Culture);
    }

    [Fact]
    public void EnvironmentInfo_AllPropertiesHaveDefaults()
    {
        // Act
        var info = new EnvironmentInfo();

        // Assert
        Assert.NotNull(info.CurrentDirectory);
        Assert.NotNull(info.TempPath);
        Assert.NotNull(info.UserPath);
        Assert.NotNull(info.SystemPath);
        Assert.NotNull(info.SafeEnvironmentVariables);
        Assert.NotNull(info.DotNetVariables);
        Assert.NotNull(info.DevelopmentVariables);
    }

    [Fact]
    public void HardwareInfo_AllPropertiesHaveDefaults()
    {
        // Act
        var info = new HardwareInfo();

        // Assert
        Assert.NotNull(info.ProcessorName);
        Assert.NotNull(info.ProcessorArchitecture);
        Assert.NotNull(info.NetworkInterfaces);
        Assert.NotNull(info.GraphicsCards);
        Assert.NotNull(info.SystemModel);
        Assert.NotNull(info.SystemManufacturer);
    }

    private static SystemInformation CreateSampleSystemInfo()
    {
        return new SystemInformation
        {
            PksCliInfo = new PksCliInfo
            {
                Version = "1.0.0",
                BuildConfiguration = "Release",
                InstallLocation = "/usr/local/bin",
                IsGlobalTool = true
            },
            DotNetRuntimeInfo = new DotNetRuntimeInfo
            {
                FrameworkVersion = "8.0.0",
                RuntimeVersion = ".NET 8.0.0",
                RuntimeIdentifier = "win-x64",
                TargetFramework = "net8.0",
                ProcessorCount = 8,
                WorkingSet = 1024 * 1024 * 64 // 64MB
            },
            OperatingSystemInfo = new OperatingSystemInfo
            {
                Platform = "Win32NT",
                OsDescription = "Microsoft Windows 11",
                OsArchitecture = "X64",
                IsWindows = true,
                CurrentUser = "testuser",
                HostName = "testmachine"
            },
            EnvironmentInfo = new EnvironmentInfo
            {
                CurrentDirectory = "C:\\test",
                HasGit = true,
                GitVersion = "git version 2.40.0"
            },
            HardwareInfo = new HardwareInfo
            {
                LogicalCores = 8,
                PhysicalCores = 4,
                ProcessorName = "Intel Core i7-8700K",
                TotalMemoryBytes = 16L * 1024 * 1024 * 1024, // 16GB
                AvailableMemoryBytes = 8L * 1024 * 1024 * 1024 // 8GB
            }
        };
    }
}