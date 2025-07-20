using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Features;

/// <summary>
/// Docker in Docker feature implementation
/// </summary>
public class DockerInDockerFeature : BaseDevcontainerFeature
{
    public DockerInDockerFeature(ILogger<DockerInDockerFeature> logger) : base(logger)
    {
    }

    public override string Id => "docker-in-docker";
    public override string Name => "Docker in Docker";
    public override string Description => "Enables Docker inside the container for building and running containers";
    public override string Version => "2";
    public override string Category => "tool";
    public override string[] Tags => new[] { "docker", "container", "build", "dind" };

    public override string[] ConflictsWith => new[] { "docker-outside-of-docker" };

    public override Dictionary<string, object> DefaultOptions => new()
    {
        ["version"] = "latest",
        ["moby"] = true,
        ["dockerDashComposeVersion"] = "v2"
    };

    public override Dictionary<string, DevcontainerFeatureOption> AvailableOptions => new()
    {
        ["version"] = CreateStringOption(
            "Select or enter a Docker/Moby Engine version",
            "latest"
        ),
        ["moby"] = CreateBooleanOption(
            "Install Moby CLI instead of Docker CLI",
            true
        ),
        ["dockerDashComposeVersion"] = CreateStringOption(
            "Compose version to use (v1 or v2)",
            "v2",
            new[] { "v1", "v2" }
        ),
        ["azureDnsAutoDetection"] = CreateBooleanOption(
            "Enable Azure DNS auto detection",
            true
        ),
        ["dockerDefaultAddressPool"] = CreateStringOption(
            "Define default address pools for Docker daemon"
        )
    };

    public override async Task<bool> IsCompatibleWithImageAsync(string baseImage)
    {
        await Task.CompletedTask;
        
        // Docker in Docker requires full Linux containers with systemd support
        var incompatibleImages = new[] { "alpine", "scratch", "distroless" };
        return !incompatibleImages.Any(img => baseImage.Contains(img, StringComparison.OrdinalIgnoreCase));
    }

    public override async Task<List<string>> GetRecommendedExtensionsAsync()
    {
        await Task.CompletedTask;
        
        return new List<string>
        {
            "ms-vscode.vscode-docker"
        };
    }

    public override async Task<Dictionary<string, string>> GetEnvironmentVariablesAsync()
    {
        await Task.CompletedTask;
        
        return new Dictionary<string, string>
        {
            ["DOCKER_BUILDKIT"] = "1"
        };
    }

    public override async Task<List<string>> GetPostCreateCommandsAsync()
    {
        await Task.CompletedTask;
        
        return new List<string>
        {
            "docker --version",
            "docker-compose --version"
        };
    }

    public override async Task<FeatureValidationResult> ValidateConfigurationAsync(object configuration)
    {
        var result = await base.ValidateConfigurationAsync(configuration);
        
        var configDict = ConvertToStringObjectDictionary(configuration);
        
        // Additional validation for Docker-specific options
        if (configDict.TryGetValue("dockerDashComposeVersion", out var composeVersionObj) && 
            composeVersionObj is string composeVersion)
        {
            if (composeVersion != "v1" && composeVersion != "v2")
            {
                result.Errors.Add("dockerDashComposeVersion must be 'v1' or 'v2'");
                result.IsValid = false;
            }
        }

        if (configDict.TryGetValue("dockerDefaultAddressPool", out var poolObj) && 
            poolObj is string pool && !string.IsNullOrEmpty(pool))
        {
            // Basic validation for CIDR format
            if (!IsValidCIDR(pool))
            {
                result.Warnings.Add("dockerDefaultAddressPool should be in CIDR format (e.g., 10.0.0.0/8)");
            }
        }

        return result;
    }

    private static bool IsValidCIDR(string cidr)
    {
        // Basic CIDR validation - in a real implementation, you'd use a proper IP address library
        return System.Text.RegularExpressions.Regex.IsMatch(cidr, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}/\d{1,2}$");
    }
}