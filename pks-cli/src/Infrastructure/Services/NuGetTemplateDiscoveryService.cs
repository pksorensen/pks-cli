using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using PKS.Infrastructure.Services.Models;
using System.IO.Compression;
using System.Text.Json;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for discovering devcontainer templates from NuGet packages
/// </summary>
public class NuGetTemplateDiscoveryService : INuGetTemplateDiscoveryService
{
    private readonly ILogger<NuGetTemplateDiscoveryService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SourceCacheContext _cacheContext;

    // Default NuGet sources
    private static readonly string[] DefaultSources = new[]
    {
        "https://api.nuget.org/v3/index.json"
    };

    public NuGetTemplateDiscoveryService(
        ILogger<NuGetTemplateDiscoveryService> logger,
        HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _cacheContext = new SourceCacheContext();
    }

    public async Task<List<NuGetDevcontainerTemplate>> DiscoverTemplatesAsync(
        string tag = "pks-devcontainers",
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        var sourcesToUse = sources?.ToArray() ?? DefaultSources;
        var templates = new List<NuGetDevcontainerTemplate>();

        _logger.LogInformation("Discovering NuGet templates with tag '{Tag}' from {SourceCount} sources", tag, sourcesToUse.Length);
        
        foreach (var source in sourcesToUse)
        {
            _logger.LogDebug("Processing source: {Source}", source);
        }

        foreach (var source in sourcesToUse)
        {
            try
            {
                _logger.LogDebug("Starting template discovery from source: {Source}", source);
                var sourceTemplates = await DiscoverTemplatesFromSourceAsync(source, tag, cancellationToken);
                templates.AddRange(sourceTemplates);
                
                _logger.LogDebug("Found {Count} templates from source {Source}", sourceTemplates.Count, source);
                
                foreach (var template in sourceTemplates)
                {
                    _logger.LogDebug("Template: {PackageId} v{Version} - Tags: {Tags}", 
                        template.PackageId, template.Version, string.Join(", ", template.Tags));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover templates from source {Source}. Error: {Message}", source, ex.Message);
                _logger.LogDebug("Full exception: {Exception}", ex);
            }
        }

        // Remove duplicates based on PackageId, keeping the highest version
        var uniqueTemplates = templates
            .GroupBy(t => t.PackageId)
            .Select(g => g.OrderByDescending(t => Version.Parse(t.Version)).First())
            .ToList();

        _logger.LogInformation("Discovered {TotalCount} templates ({UniqueCount} unique packages)", templates.Count, uniqueTemplates.Count);
        return uniqueTemplates;
    }

    public async Task<NuGetTemplateExtractionResult> ExtractTemplateAsync(
        string packageId,
        string version,
        string extractPath,
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        var result = new NuGetTemplateExtractionResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Extracting template {PackageId} v{Version} to {ExtractPath}", packageId, version, extractPath);

            var sourcesToUse = sources?.ToArray() ?? DefaultSources;
            
            foreach (var source in sourcesToUse)
            {
                try
                {
                    var sourceRepository = Repository.Factory.GetCoreV3(source);
                    var downloadResource = await sourceRepository.GetResourceAsync<DownloadResource>(cancellationToken);
                    
                    var packageIdentity = new NuGet.Packaging.Core.PackageIdentity(packageId, NuGet.Versioning.NuGetVersion.Parse(version));
                    
                    using var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                        packageIdentity,
                        new PackageDownloadContext(_cacheContext),
                        Path.GetTempPath(),
                        NullLogger.Instance,
                        cancellationToken);

                    if (downloadResult.Status == DownloadResourceResultStatus.Available && downloadResult.PackageStream != null)
                    {
                        await ExtractPackageContentAsync(downloadResult.PackageStream, extractPath, result);
                        result.Success = true;
                        result.ExtractedPath = extractPath;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download from source {Source}", source);
                    continue;
                }
            }

            if (!result.Success)
            {
                result.ErrorMessage = $"Package {packageId} v{version} not found in any configured source";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting template {PackageId} v{Version}", packageId, version);
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }
        finally
        {
            stopwatch.Stop();
            result.ExtractionTime = stopwatch.Elapsed;
        }

        return result;
    }

    public async Task<NuGetTemplateDetails?> GetTemplateDetailsAsync(
        string packageId,
        string version,
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        var sourcesToUse = sources?.ToArray() ?? DefaultSources;

        foreach (var source in sourcesToUse)
        {
            try
            {
                var sourceRepository = Repository.Factory.GetCoreV3(source);
                var metadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>(cancellationToken);
                
                var identity = new NuGet.Packaging.Core.PackageIdentity(packageId, NuGet.Versioning.NuGetVersion.Parse(version));
                var metadata = await metadataResource.GetMetadataAsync(identity, _cacheContext, NullLogger.Instance, cancellationToken);

                if (metadata != null)
                {
                    return ConvertToTemplateDetails(metadata);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get metadata from source {Source}", source);
            }
        }

        return null;
    }

    public async Task<List<NuGetTemplateSearchResult>> SearchTemplatesAsync(
        string query,
        string tag = "pks-devcontainers",
        IEnumerable<string>? sources = null,
        int maxResults = 10,
        CancellationToken cancellationToken = default)
    {
        var sourcesToUse = sources?.ToArray() ?? DefaultSources;
        var searchResults = new List<NuGetTemplateSearchResult>();

        foreach (var source in sourcesToUse)
        {
            try
            {
                var sourceRepository = Repository.Factory.GetCoreV3(source);
                var searchResource = await sourceRepository.GetResourceAsync<PackageSearchResource>(cancellationToken);
                
                var searchFilter = new SearchFilter(includePrerelease: false)
                {
                    SupportedFrameworks = Array.Empty<string>(),
                    PackageTypes = Array.Empty<string>()
                };

                var combinedQuery = string.IsNullOrEmpty(query) ? $"tags:{tag}" : $"{query} tags:{tag}";
                
                var packages = await searchResource.SearchAsync(
                    combinedQuery,
                    searchFilter,
                    skip: 0,
                    take: maxResults,
                    NullLogger.Instance,
                    cancellationToken);

                foreach (var package in packages)
                {
                    var searchResult = new NuGetTemplateSearchResult
                    {
                        PackageId = package.Identity.Id,
                        Version = package.Identity.Version.ToString(),
                        Title = package.Title,
                        Description = package.Description,
                        Tags = package.Tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                        DownloadCount = package.DownloadCount ?? 0,
                        IsPrerelease = package.Identity.Version.IsPrerelease,
                        RelevanceScore = CalculateRelevanceScore(package, query)
                    };
                    
                    searchResults.Add(searchResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search in source {Source}", source);
            }
        }

        return searchResults
            .OrderByDescending(r => r.RelevanceScore)
            .ThenByDescending(r => r.DownloadCount)
            .Take(maxResults)
            .ToList();
    }

    public async Task<NuGetSourceValidationResult> ValidateSourcesAsync(
        IEnumerable<string> sources,
        CancellationToken cancellationToken = default)
    {
        var result = new NuGetSourceValidationResult();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            foreach (var source in sources)
            {
                try
                {
                    // Try to create a repository and get a resource
                    var sourceRepository = Repository.Factory.GetCoreV3(source);
                    var serviceIndex = await sourceRepository.GetResourceAsync<ServiceIndexResourceV3>(cancellationToken);
                    
                    if (serviceIndex != null)
                    {
                        result.ValidSources.Add(source);
                        _logger.LogDebug("Validated NuGet source: {Source}", source);
                    }
                    else
                    {
                        result.InvalidSources.Add(source);
                        result.Errors.Add($"Unable to access service index for source: {source}");
                    }
                }
                catch (Exception ex)
                {
                    result.InvalidSources.Add(source);
                    result.Errors.Add($"Error validating source {source}: {ex.Message}");
                    _logger.LogWarning(ex, "Failed to validate source {Source}", source);
                }
            }

            result.IsValid = result.ValidSources.Any() && !result.Errors.Any();
            
            if (result.InvalidSources.Any())
            {
                result.Warnings.Add($"{result.InvalidSources.Count} sources could not be validated");
            }
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation failed: {ex.Message}");
            _logger.LogError(ex, "Error during source validation");
        }
        finally
        {
            stopwatch.Stop();
            result.ValidationTime = stopwatch.Elapsed;
        }

        return result;
    }

    private async Task<List<NuGetDevcontainerTemplate>> DiscoverTemplatesFromSourceAsync(
        string source,
        string tag,
        CancellationToken cancellationToken)
    {
        var templates = new List<NuGetDevcontainerTemplate>();
        
        // Check if this is a local folder source
        if (IsLocalFolderSource(source))
        {
            _logger.LogDebug("Detected local folder source: {Source}. Using direct file search.", source);
            return await DiscoverTemplatesFromLocalFolderAsync(source, tag, cancellationToken);
        }
        
        _logger.LogDebug("Creating repository for source: {Source}", source);
        var sourceRepository = Repository.Factory.GetCoreV3(source);
        
        _logger.LogDebug("Getting PackageSearchResource for source: {Source}", source);
        var searchResource = await sourceRepository.GetResourceAsync<PackageSearchResource>(cancellationToken);
        
        var searchFilter = new SearchFilter(includePrerelease: false)
        {
            SupportedFrameworks = Array.Empty<string>(),
            PackageTypes = Array.Empty<string>()
        };

        var searchQuery = $"tags:{tag}";
        _logger.LogDebug("Searching with query: '{SearchQuery}' in source: {Source}", searchQuery, source);
        
        var packages = await searchResource.SearchAsync(
            searchQuery,
            searchFilter,
            skip: 0,
            take: 100, // Reasonable limit for templates
            NullLogger.Instance,
            cancellationToken);
            
        _logger.LogDebug("Search returned {PackageCount} packages from source: {Source}", 
            packages?.Count() ?? 0, source);

        foreach (var package in packages)
        {
            var template = new NuGetDevcontainerTemplate
            {
                PackageId = package.Identity.Id,
                Version = package.Identity.Version.ToString(),
                Title = package.Title,
                Description = package.Description,
                Authors = package.Authors,
                Tags = package.Tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
                ProjectUrl = package.ProjectUrl?.ToString() ?? string.Empty,
                LicenseUrl = package.LicenseUrl?.ToString() ?? string.Empty,
                IconUrl = package.IconUrl?.ToString() ?? string.Empty,
                Published = package.Published?.DateTime ?? DateTime.MinValue,
                DownloadCount = package.DownloadCount ?? 0,
                SourceUrl = source,
                IsPrerelease = package.Identity.Version.IsPrerelease
            };

            // Extract metadata from package properties if available
            ExtractMetadataFromPackage(template, package);

            templates.Add(template);
        }

        return templates;
    }

    private static void ExtractMetadataFromPackage(NuGetDevcontainerTemplate template, IPackageSearchMetadata package)
    {
        // Extract devcontainer-specific metadata from package properties
        // This would typically come from the package's .nuspec file
        
        // For now, we'll use some heuristics based on package properties
        if (package.DependencySets != null)
        {
            template.Dependencies = package.DependencySets
                .SelectMany(ds => ds.Packages)
                .Select(p => p.Id)
                .ToArray();
        }

        // Extract custom metadata if package supports it
        // This could be enhanced to read from package content or additional properties
    }

    private async Task ExtractPackageContentAsync(Stream packageStream, string extractPath, NuGetTemplateExtractionResult result)
    {
        Directory.CreateDirectory(extractPath);

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read);
        
        foreach (var entry in archive.Entries)
        {
            // Skip package metadata files, focus on content
            if (entry.FullName.StartsWith("content/") || entry.FullName.StartsWith("contentFiles/"))
            {
                var relativePath = entry.FullName.StartsWith("content/") 
                    ? entry.FullName.Substring("content/".Length)
                    : entry.FullName.Substring("contentFiles/".Length);
                
                // Skip template engine metadata that shouldn't be in user projects
                if (relativePath.StartsWith(".template.config/", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.Contains("template.json", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.EndsWith("icon.png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                var fullPath = Path.Combine(extractPath, relativePath);
                var directory = Path.GetDirectoryName(fullPath);
                
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!string.IsNullOrEmpty(entry.Name)) // It's a file, not a directory
                {
                    entry.ExtractToFile(fullPath, overwrite: true);
                    result.ExtractedFiles.Add(fullPath);
                }
            }
            
            // Look for template manifest
            if (entry.Name.Equals("pks-template.json", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                var manifestJson = await reader.ReadToEndAsync();
                
                try
                {
                    result.Manifest = JsonSerializer.Deserialize<NuGetTemplateManifest>(manifestJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse template manifest");
                }
            }
        }
    }

    private static NuGetTemplateDetails ConvertToTemplateDetails(IPackageSearchMetadata metadata)
    {
        return new NuGetTemplateDetails
        {
            PackageId = metadata.Identity.Id,
            Version = metadata.Identity.Version.ToString(),
            Title = metadata.Title,
            Description = metadata.Description,
            Authors = metadata.Authors,
            Tags = metadata.Tags?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
            ProjectUrl = metadata.ProjectUrl?.ToString() ?? string.Empty,
            LicenseUrl = metadata.LicenseUrl?.ToString() ?? string.Empty,
            IconUrl = metadata.IconUrl?.ToString() ?? string.Empty,
            Published = metadata.Published?.DateTime ?? DateTime.MinValue,
            DownloadCount = metadata.DownloadCount ?? 0,
            IsPrerelease = metadata.Identity.Version.IsPrerelease,
            Dependencies = metadata.DependencySets?.SelectMany(ds => ds.Packages).Select(p => p.Id).ToArray() ?? Array.Empty<string>(),
            ReleaseNotes = string.Empty, // Not available in IPackageSearchMetadata
            Owners = metadata.Owners?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
            MinClientVersion = string.Empty, // Not available in IPackageSearchMetadata
            PackageSize = 0 // This would need to be calculated from package content
        };
    }

    private static float CalculateRelevanceScore(IPackageSearchMetadata package, string query)
    {
        if (string.IsNullOrEmpty(query))
            return 1.0f;

        var score = 0.0f;
        var lowerQuery = query.ToLowerInvariant();

        // Exact match in package ID gets highest score
        if (package.Identity.Id.ToLowerInvariant().Contains(lowerQuery))
            score += 10.0f;

        // Match in title
        if (package.Title.ToLowerInvariant().Contains(lowerQuery))
            score += 5.0f;

        // Match in description
        if (package.Description.ToLowerInvariant().Contains(lowerQuery))
            score += 2.0f;

        // Match in tags
        if (package.Tags?.ToLowerInvariant().Contains(lowerQuery) == true)
            score += 3.0f;

        // Boost popular packages
        if (package.DownloadCount > 1000)
            score += 1.0f;

        return score;
    }

    private static bool IsLocalFolderSource(string source)
    {
        // Check if it's a local path (not a URL)
        return !source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
               !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
               (Path.IsPathRooted(source) || Directory.Exists(source));
    }

    private async Task<List<NuGetDevcontainerTemplate>> DiscoverTemplatesFromLocalFolderAsync(
        string folderPath,
        string tag,
        CancellationToken cancellationToken)
    {
        var templates = new List<NuGetDevcontainerTemplate>();
        
        try
        {
            _logger.LogDebug("Searching for .nupkg files in folder: {FolderPath}", folderPath);
            
            if (!Directory.Exists(folderPath))
            {
                _logger.LogWarning("Local folder source does not exist: {FolderPath}", folderPath);
                return templates;
            }

            var nupkgFiles = Directory.GetFiles(folderPath, "*.nupkg", SearchOption.TopDirectoryOnly);
            _logger.LogDebug("Found {FileCount} .nupkg files in {FolderPath}", nupkgFiles.Length, folderPath);

            foreach (var nupkgFile in nupkgFiles)
            {
                try
                {
                    var template = await ExtractTemplateFromPackageFileAsync(nupkgFile, tag);
                    if (template != null)
                    {
                        templates.Add(template);
                        _logger.LogDebug("Extracted template from {PackageFile}: {PackageId} v{Version}", 
                            Path.GetFileName(nupkgFile), template.PackageId, template.Version);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract template from {PackageFile}", nupkgFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering templates from local folder: {FolderPath}", folderPath);
        }

        return templates;
    }

    private async Task<NuGetDevcontainerTemplate?> ExtractTemplateFromPackageFileAsync(string nupkgFile, string tag)
    {
        try
        {
            using var fileStream = File.OpenRead(nupkgFile);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            
            // Find the .nuspec file
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry == null)
            {
                _logger.LogDebug("No .nuspec file found in {PackageFile}", nupkgFile);
                return null;
            }

            using var nuspecStream = nuspecEntry.Open();
            using var reader = new StreamReader(nuspecStream);
            var nuspecContent = await reader.ReadToEndAsync();

            // Parse the nuspec XML to extract metadata
            var doc = System.Xml.Linq.XDocument.Parse(nuspecContent);
            var ns = doc.Root?.GetDefaultNamespace();
            var metadata = doc.Root?.Element(ns + "metadata");

            if (metadata == null)
            {
                _logger.LogDebug("No metadata found in .nuspec for {PackageFile}", nupkgFile);
                return null;
            }

            var packageId = metadata.Element(ns + "id")?.Value;
            var version = metadata.Element(ns + "version")?.Value;
            var title = metadata.Element(ns + "title")?.Value ?? packageId;
            var description = metadata.Element(ns + "description")?.Value ?? "";
            var authors = metadata.Element(ns + "authors")?.Value ?? "";
            var tagsValue = metadata.Element(ns + "tags")?.Value ?? "";
            var projectUrl = metadata.Element(ns + "projectUrl")?.Value ?? "";
            var licenseUrl = metadata.Element(ns + "licenseUrl")?.Value ?? "";
            var iconUrl = metadata.Element(ns + "iconUrl")?.Value ?? "";

            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(version))
            {
                _logger.LogDebug("Package ID or version missing in {PackageFile}", nupkgFile);
                return null;
            }

            var tags = tagsValue.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            
            _logger.LogDebug("Package {PackageId} v{Version} has tags: {Tags}", packageId, version, string.Join(", ", tags));

            // Check if the package has the required tag
            if (!tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogDebug("Package {PackageId} v{Version} does not have required tag '{Tag}'", packageId, version, tag);
                return null;
            }

            var template = new NuGetDevcontainerTemplate
            {
                PackageId = packageId,
                Version = version,
                Title = title,
                Description = description,
                Authors = authors,
                Tags = tags,
                ProjectUrl = projectUrl,
                LicenseUrl = licenseUrl,
                IconUrl = iconUrl,
                Published = File.GetCreationTime(nupkgFile),
                DownloadCount = 0, // Not applicable for local files
                SourceUrl = nupkgFile,
                IsPrerelease = version.Contains("-", StringComparison.OrdinalIgnoreCase)
            };

            _logger.LogDebug("Successfully extracted template: {PackageId} v{Version} with matching tag '{Tag}'", 
                packageId, version, tag);

            return template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting template from {PackageFile}", nupkgFile);
            return null;
        }
    }
}