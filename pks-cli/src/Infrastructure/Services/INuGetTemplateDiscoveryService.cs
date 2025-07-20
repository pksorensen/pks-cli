using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for discovering devcontainer templates from NuGet packages
/// </summary>
public interface INuGetTemplateDiscoveryService
{
    /// <summary>
    /// Discovers templates from NuGet packages with the specified tag
    /// </summary>
    /// <param name="tag">Tag to search for (default: "pks-devcontainers")</param>
    /// <param name="sources">Custom NuGet sources to use</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered templates</returns>
    Task<List<NuGetDevcontainerTemplate>> DiscoverTemplatesAsync(
        string tag = "pks-devcontainers",
        IEnumerable<string>? sources = null,
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
}