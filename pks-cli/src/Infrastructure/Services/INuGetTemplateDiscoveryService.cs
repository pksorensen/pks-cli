using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for discovering devcontainer templates from NuGet packages
/// </summary>
public interface INuGetTemplateDiscoveryService
{
    /// <summary>
    /// Current configuration settings
    /// </summary>
    NuGetDiscoveryConfiguration Configuration { get; }
    /// <summary>
    /// Discovers templates from NuGet packages with the specified tag
    /// </summary>
    /// <param name="tag">Tag to search for (default: "pks-devcontainers")</param>
    /// <param name="sources">Custom NuGet sources to use</param>
    /// <param name="includePrerelease">Include prerelease/preview packages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered templates</returns>
    Task<List<NuGetDevcontainerTemplate>> DiscoverTemplatesAsync(
        string tag = "pks-devcontainers",
        IEnumerable<string>? sources = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and extracts a template package
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="version">Package version</param>
    /// <param name="extractPath">Path to extract template files</param>
    /// <param name="sources">Custom NuGet sources to use</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Template extraction result</returns>
    Task<NuGetTemplateExtractionResult> ExtractTemplateAsync(
        string packageId,
        string version,
        string extractPath,
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed information about a specific template package
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="version">Package version</param>
    /// <param name="sources">Custom NuGet sources to use</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed template information</returns>
    Task<NuGetTemplateDetails?> GetTemplateDetailsAsync(
        string packageId,
        string version,
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for templates with auto-completion support
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="tag">Tag to filter by</param>
    /// <param name="sources">Custom NuGet sources to use</param>
    /// <param name="maxResults">Maximum number of results</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Auto-completion suggestions</returns>
    Task<List<NuGetTemplateSearchResult>> SearchTemplatesAsync(
        string query,
        string tag = "pks-devcontainers",
        IEnumerable<string>? sources = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates NuGet sources and connectivity
    /// </summary>
    /// <param name="sources">NuGet sources to validate</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Validation result</returns>
    Task<NuGetSourceValidationResult> ValidateSourcesAsync(
        IEnumerable<string> sources,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed information about a template package including all templates it contains
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Template package information or null if not found</returns>
    Task<NuGetTemplatePackage?> GetTemplatePackageAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Configures the NuGet template discovery service
    /// </summary>
    /// <param name="configuration">Configuration settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Configured service instance</returns>
    Task<INuGetTemplateDiscoveryService> ConfigureAsync(NuGetDiscoveryConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a template package to the specified output path
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="outputPath">Path to install the template</param>
    /// <param name="version">Package version (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Installation result</returns>
    Task<NuGetTemplateExtractionResult> InstallTemplatePackageAsync(string packageId, string outputPath, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets available versions for a template package
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available versions</returns>
    Task<List<string>> GetAvailableVersionsAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the latest version for a template package
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="includePrerelease">Include prerelease versions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Latest version or null if not found</returns>
    Task<string?> GetLatestVersionAsync(string packageId, bool includePrerelease = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks for updates to installed template packages
    /// </summary>
    /// <param name="installedPackages">List of installed packages with their versions</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of package IDs with available updates</returns>
    Task<Dictionary<string, string>> CheckForUpdatesAsync(
        Dictionary<string, string> installedPackages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls a template package
    /// </summary>
    /// <param name="packageId">NuGet package ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    Task<bool> UninstallTemplatePackageAsync(string packageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets installed templates
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of installed templates</returns>
    Task<List<NuGetDevcontainerTemplate>> GetInstalledTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a template
    /// </summary>
    /// <param name="packageId">Package ID to install</param>
    /// <param name="version">Version to install</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Installation result</returns>
    Task<NuGetTemplateExtractionResult> InstallTemplateAsync(string packageId, string? version = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls a template
    /// </summary>
    /// <param name="packageId">Package ID to uninstall</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful</returns>
    Task<bool> UninstallTemplateAsync(string packageId, CancellationToken cancellationToken = default);
}