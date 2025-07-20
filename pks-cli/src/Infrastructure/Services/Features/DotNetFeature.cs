using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Features;

/// <summary>
/// .NET development feature implementation
/// </summary>
public class DotNetFeature : BaseDevcontainerFeature
{
    public DotNetFeature(ILogger<DotNetFeature> logger) : base(logger)
    {
    }

    public override string Id => "dotnet";
    public override string Name => ".NET";
    public override string Description => "Installs .NET SDK and runtime for C# development";
    public override string Version => "2";
    public override string Category => "runtime";
    public override string[] Tags => new[] { "dotnet", "csharp", "runtime", "sdk" };

    public override Dictionary<string, object> DefaultOptions => new()
    {
        ["version"] = "8.0",
        ["installUsingApt"] = true,
        ["dotnetRuntimeOnly"] = false
    };

    public override Dictionary<string, DevcontainerFeatureOption> AvailableOptions => new()
    {
        ["version"] = CreateStringOption(
            "Select or enter a .NET version to install",
            "8.0",
            new[] { "6.0", "7.0", "8.0", "latest" }
        ),
        ["installUsingApt"] = CreateBooleanOption(
            "Install using apt-get instead of tar.gz",
            true
        ),
        ["dotnetRuntimeOnly"] = CreateBooleanOption(
            "Install runtime only (not SDK)",
            false
        ),
        ["additionalVersions"] = CreateArrayOption(
            "Additional .NET versions to install"
        )
    };

    public override async Task<bool> IsCompatibleWithImageAsync(string baseImage)
    {
        await Task.CompletedTask;
        
        // .NET is compatible with most Linux-based images
        var incompatibleImages = new[] { "alpine", "scratch" };
        return !incompatibleImages.Any(img => baseImage.Contains(img, StringComparison.OrdinalIgnoreCase));
    }

    public override async Task<List<string>> GetRecommendedExtensionsAsync()
    {
        await Task.CompletedTask;
        
        return new List<string>
        {
            "ms-dotnettools.csharp",
            "ms-dotnettools.vscode-dotnet-runtime"
        };
    }

    public override async Task<Dictionary<string, string>> GetEnvironmentVariablesAsync()
    {
        await Task.CompletedTask;
        
        return new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_NOLOGO"] = "1"
        };
    }

    public override async Task<List<int>> GetForwardedPortsAsync()
    {
        await Task.CompletedTask;
        
        return new List<int> { 5000, 5001 }; // Default ASP.NET Core ports
    }

    public override async Task<List<string>> GetPostCreateCommandsAsync()
    {
        await Task.CompletedTask;
        
        return new List<string>
        {
            "dotnet --version",
            "dotnet tool install -g dotnet-ef",
            "dotnet tool install -g dotnet-aspnet-codegenerator"
        };
    }

    public override async Task<Dictionary<string, object>> GenerateConfigurationAsync(Dictionary<string, object>? options = null)
    {
        var config = await base.GenerateConfigurationAsync(options);
        
        // Validate version format
        if (config.TryGetValue("version", out var versionObj) && versionObj is string version)
        {
            if (version != "latest" && !IsValidDotNetVersion(version))
            {
                Logger.LogWarning("Invalid .NET version specified: {Version}. Using default.", version);
                config["version"] = "8.0";
            }
        }

        return config;
    }

    private static bool IsValidDotNetVersion(string version)
    {
        // Basic validation for .NET version format (e.g., "6.0", "7.0", "8.0")
        return System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+$");
    }
}