namespace PKS.Infrastructure.Services.Models;

/// <summary>
/// Represents a devcontainer template discovered from NuGet
/// </summary>
public class NuGetDevcontainerTemplate
{
    public string Id { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] Categories { get; set; } = Array.Empty<string>();
    public string[] Languages { get; set; } = Array.Empty<string>();
    public string[] ShortNames { get; set; } = Array.Empty<string>();
    public string ProjectUrl { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string InstallPath { get; set; } = string.Empty;
    public DateTime Published { get; set; }
    public DateTime InstalledDate { get; set; }
    public long DownloadCount { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool IsPrerelease { get; set; }
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Metadata { get; set; } = new();
    
    /// <summary>
    /// Converts to the standard DevcontainerTemplate format
    /// </summary>
    public DevcontainerTemplate ToDevcontainerTemplate()
    {
        return new DevcontainerTemplate
        {
            Id = PackageId,
            Name = Title,
            Description = Description,
            Category = GetCategoryFromTags(),
            BaseImage = GetBaseImageFromMetadata(),
            RequiredFeatures = GetRequiredFeaturesFromMetadata(),
            OptionalFeatures = GetOptionalFeaturesFromMetadata(),
            DefaultCustomizations = GetCustomizationsFromMetadata(),
            DefaultPorts = GetPortsFromMetadata(),
            DefaultPostCreateCommand = GetPostCreateCommandFromMetadata(),
            DefaultEnvVars = GetEnvVarsFromMetadata(),
            RequiredEnvVars = GetRequiredEnvVarsFromMetadata(),
            RequiresDockerCompose = GetDockerComposeRequirementFromMetadata(),
            DockerComposeTemplate = GetDockerComposeTemplateFromMetadata(),
            Version = Version
        };
    }

    private string GetCategoryFromTags()
    {
        var categoryTag = Tags.FirstOrDefault(t => t.StartsWith("category:"));
        return categoryTag?.Substring("category:".Length) ?? "general";
    }

    private string GetBaseImageFromMetadata()
    {
        return Metadata.GetValueOrDefault("baseImage", "mcr.microsoft.com/vscode/devcontainers/base:ubuntu");
    }

    private string[] GetRequiredFeaturesFromMetadata()
    {
        var features = Metadata.GetValueOrDefault("requiredFeatures", string.Empty);
        return string.IsNullOrEmpty(features) 
            ? Array.Empty<string>() 
            : features.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private string[] GetOptionalFeaturesFromMetadata()
    {
        var features = Metadata.GetValueOrDefault("optionalFeatures", string.Empty);
        return string.IsNullOrEmpty(features) 
            ? Array.Empty<string>() 
            : features.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private Dictionary<string, object> GetCustomizationsFromMetadata()
    {
        // Parse customizations from metadata or return default VS Code setup
        var customizations = new Dictionary<string, object>();
        
        var extensions = Metadata.GetValueOrDefault("vscodeExtensions", string.Empty);
        if (!string.IsNullOrEmpty(extensions))
        {
            customizations["vscode"] = new
            {
                extensions = extensions.Split(',', StringSplitOptions.RemoveEmptyEntries)
            };
        }

        return customizations;
    }

    private string[] GetPortsFromMetadata()
    {
        var ports = Metadata.GetValueOrDefault("defaultPorts", string.Empty);
        return string.IsNullOrEmpty(ports) 
            ? Array.Empty<string>() 
            : ports.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    private string GetPostCreateCommandFromMetadata()
    {
        return Metadata.GetValueOrDefault("postCreateCommand", string.Empty);
    }

    private Dictionary<string, string> GetEnvVarsFromMetadata()
    {
        var envVars = new Dictionary<string, string>();
        foreach (var kvp in Metadata.Where(m => m.Key.StartsWith("env:")))
        {
            var envName = kvp.Key.Substring("env:".Length);
            envVars[envName] = kvp.Value;
        }
        return envVars;
    }

    private Dictionary<string, string> GetRequiredEnvVarsFromMetadata()
    {
        var requiredEnvVars = new Dictionary<string, string>();
        foreach (var kvp in Metadata.Where(m => m.Key.StartsWith("requiredEnv:")))
        {
            var envName = kvp.Key.Substring("requiredEnv:".Length);
            requiredEnvVars[envName] = kvp.Value; // Value is the description/prompt for the env var
        }
        return requiredEnvVars;
    }

    private bool GetDockerComposeRequirementFromMetadata()
    {
        return bool.TryParse(Metadata.GetValueOrDefault("requiresDockerCompose", "false"), out var requires) && requires;
    }

    private string GetDockerComposeTemplateFromMetadata()
    {
        return Metadata.GetValueOrDefault("dockerComposeTemplate", string.Empty);
    }
}

/// <summary>
/// Result of template extraction from NuGet package
/// </summary>
public class NuGetTemplateExtractionResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ExtractedPath { get; set; } = string.Empty;
    public List<string> ExtractedFiles { get; set; } = new();
    public string[] ExtractedFilesArray => ExtractedFiles.ToArray(); // Alias
    public NuGetTemplateManifest? Manifest { get; set; }
    public TimeSpan ExtractionTime { get; set; }
    public List<TemplateInfo> InstalledTemplates { get; set; } = new();
    public int TemplateCount { get; set; }
    public long TotalSize { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Detailed information about a NuGet template package
/// </summary>
public class NuGetTemplateDetails
{
    public string Id { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
    public string[] Languages { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string ProjectUrl { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string RepositoryUrl { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
    public bool RequiresLicenseAcceptance { get; set; }
    public string IconUrl { get; set; } = string.Empty;
    public DateTime Published { get; set; }
    public DateTime PublishedDate => Published; // Alias for consistency
    public DateTime LastUpdated { get; set; }
    public long DownloadCount { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public bool IsPrerelease { get; set; }
    public bool IsPreRelease => IsPrerelease; // Alias for consistency
    public string[] Dependencies { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Metadata { get; set; } = new();
    public string ReleaseNotes { get; set; } = string.Empty;
    public string[] Owners { get; set; } = Array.Empty<string>();
    public string MinClientVersion { get; set; } = string.Empty;
    public long PackageSize { get; set; }
    public string[] Vulnerabilities { get; set; } = Array.Empty<string>();
    public string? Usage { get; set; }
    public string[] Prerequisites { get; set; } = Array.Empty<string>();
    public IEnumerable<TemplateOption>? Options { get; set; }
    public IEnumerable<RelatedTemplate>? RelatedTemplates { get; set; }
}

// Supporting classes for template details
public class TemplateOption
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
    public string[]? Choices { get; set; }
    public bool IsRequired { get; set; }
    public string? DisplayName { get; set; }
}

public class RelatedTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Relationship { get; set; } = string.Empty; // "similar", "extends", "part-of", etc.
}

/// <summary>
/// Auto-completion search result for templates
/// </summary>
public class NuGetTemplateSearchResult
{
    public string Id { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public long DownloadCount { get; set; }
    public bool IsPrerelease { get; set; }
    public bool IsPreRelease => IsPrerelease; // Alias for consistency
    public float RelevanceScore { get; set; }
    public string Authors { get; set; } = string.Empty;
    public string[] Categories { get; set; } = Array.Empty<string>();
    public string[] Languages { get; set; } = Array.Empty<string>();
    public string ProjectUrl { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
}

/// <summary>
/// NuGet source validation result
/// </summary>
public class NuGetSourceValidationResult
{
    public bool IsValid { get; set; }
    public List<string> ValidSources { get; set; } = new();
    public List<string> InvalidSources { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TimeSpan ValidationTime { get; set; }
}

/// <summary>
/// Template manifest from NuGet package
/// </summary>
public class NuGetTemplateManifest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string BaseImage { get; set; } = string.Empty;
    public string[] RequiredFeatures { get; set; } = Array.Empty<string>();
    public string[] OptionalFeatures { get; set; } = Array.Empty<string>();
    public string[] VsCodeExtensions { get; set; } = Array.Empty<string>();
    public string[] DefaultPorts { get; set; } = Array.Empty<string>();
    public string PostCreateCommand { get; set; } = string.Empty;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public bool RequiresDockerCompose { get; set; }
    public string DockerComposeFile { get; set; } = string.Empty;
    public Dictionary<string, object> CustomSettings { get; set; } = new();
    public string[] TemplateFiles { get; set; } = Array.Empty<string>();
    public string[] SampleFiles { get; set; } = Array.Empty<string>();
    public string DocumentationUrl { get; set; } = string.Empty;
    public string[] Prerequisites { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Represents a NuGet template package with its templates
/// </summary>
public class NuGetTemplatePackage
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Authors { get; set; } = string.Empty;
    public List<TemplateInfo> Templates { get; set; } = new();
}

/// <summary>
/// Represents a template within a NuGet template package
/// </summary>
public class TemplateInfo
{
    public string ShortName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string[] Classifications { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Configuration for NuGet template discovery
/// </summary>
public class NuGetDiscoveryConfiguration
{
    public List<string> Sources { get; set; } = new();
    public string Tag { get; set; } = "pks-devcontainers";
    public int MaxResults { get; set; } = 50;
    public bool IncludePrerelease { get; set; } = false;
    public bool EnablePrerelease { get; set; } = false;
    public string? ApiKey { get; set; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public int TimeoutSeconds { get; set; } = 30;
    public bool UseLocalCache { get; set; } = true;
    public string? CacheDirectory { get; set; }
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}