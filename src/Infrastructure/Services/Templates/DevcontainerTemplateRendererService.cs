using Microsoft.Extensions.Logging;
using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services.Templates;

/// <inheritdoc />
public class DevcontainerTemplateRendererService : IDevcontainerTemplateRendererService
{
    private readonly INuGetTemplateDiscoveryService _nugetService;
    private readonly ILogger<DevcontainerTemplateRendererService> _logger;

    public DevcontainerTemplateRendererService(
        INuGetTemplateDiscoveryService nugetService,
        ILogger<DevcontainerTemplateRendererService> logger)
    {
        _nugetService = nugetService;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>?> RenderAsync(
        DevcontainerTemplateRef template,
        IReadOnlyDictionary<string, string> placeholderValues,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(template?.Id))
        {
            _logger.LogWarning("Template reference has no Id; skipping render.");
            return null;
        }

        var source = string.IsNullOrWhiteSpace(template.Source) ? "nuget" : template.Source!.ToLowerInvariant();
        return source switch
        {
            "nuget" => await RenderFromNuGetAsync(template, placeholderValues, ct),
            _ => LogAndNull($"Unsupported template source '{source}' for '{template.Id}'.")
        };
    }

    private Dictionary<string, string>? LogAndNull(string message)
    {
        _logger.LogWarning("{Message}", message);
        return null;
    }

    private async Task<Dictionary<string, string>?> RenderFromNuGetAsync(
        DevcontainerTemplateRef template,
        IReadOnlyDictionary<string, string> placeholderValues,
        CancellationToken ct)
    {
        var version = template.Version;
        if (string.IsNullOrWhiteSpace(version))
        {
            try
            {
                version = await _nugetService.GetLatestVersionAsync(template.Id, includePrerelease: false, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve latest version for '{Id}' from NuGet.", template.Id);
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            _logger.LogWarning("No version found for template '{Id}' on NuGet.", template.Id);
            return null;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"pks-tpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            _logger.LogInformation("Resolving devcontainer template '{Id}@{Version}' from NuGet...", template.Id, version);

            var result = await _nugetService.ExtractTemplateAsync(template.Id, version!, tempRoot, sources: null, ct);
            if (result == null || result.ExtractedFiles == null || result.ExtractedFiles.Count == 0)
            {
                _logger.LogWarning("Template '{Id}@{Version}' produced no files.", template.Id, version);
                return null;
            }

            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var absolutePath in result.ExtractedFiles)
            {
                if (!File.Exists(absolutePath)) continue;

                var relative = Path.GetRelativePath(tempRoot, absolutePath).Replace('\\', '/');
                // NuGet template packages put real content under content/. Strip that prefix
                // so the runner writes files at the workspace root (e.g. .devcontainer/devcontainer.json).
                if (relative.StartsWith("content/", StringComparison.OrdinalIgnoreCase))
                    relative = relative.Substring("content/".Length);

                // Skip .template.config metadata — only relevant to the .NET template engine.
                if (relative.StartsWith(".template.config/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(absolutePath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping non-text file {Path}", absolutePath);
                    continue;
                }

                files[relative] = ApplyPlaceholders(content, placeholderValues);
            }

            _logger.LogInformation(
                "Rendered template '{Id}@{Version}' into {Count} files.", template.Id, version, files.Count);
            return files;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render template '{Id}@{Version}' from NuGet.", template.Id, version);
            return null;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Mirrors BaseInitializer.ReplacePlaceholders so this service is independent of the
    /// initializer hierarchy. Adds today's date / year / datetime automatically.
    /// </summary>
    private static string ApplyPlaceholders(string content, IReadOnlyDictionary<string, string> values)
    {
        var now = DateTime.Now;
        var projectName = values.TryGetValue("ProjectName", out var pn) ? pn : string.Empty;
        var description = values.TryGetValue("Description", out var d) ? d : string.Empty;
        var templateName = values.TryGetValue("Template", out var t) ? t : string.Empty;

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "{{ProjectName}}", projectName },
            { "{{Project.Name}}", projectName },
            { "{{PROJECT_NAME}}", projectName.ToUpperInvariant() },
            { "{{project_name}}", projectName.ToLowerInvariant() },
            { "{{Description}}", description },
            { "{{Project.Description}}", description },
            { "{{Template}}", templateName },
            { "{{Project.Template}}", templateName },
            { "{{Date}}", now.ToString("yyyy-MM-dd") },
            { "{{DateTime}}", now.ToString("yyyy-MM-dd HH:mm:ss") },
            { "{{Year}}", now.Year.ToString() },
        };

        // Caller-provided extras take precedence — lets us add Owner/Repository later.
        foreach (var kv in values)
            replacements[$"{{{{{kv.Key}}}}}"] = kv.Value;

        var result = content;
        foreach (var kv in replacements)
            result = result.Replace(kv.Key, kv.Value);

        return result;
    }
}
