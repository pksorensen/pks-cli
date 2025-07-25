using PKS.Infrastructure.Initializers.Context;
using Spectre.Console;

namespace PKS.Infrastructure.Initializers.Base;

/// <summary>
/// Base class for template-based initializers that work with file templates
/// </summary>
public abstract class TemplateInitializer : BaseInitializer
{
    /// <summary>
    /// Gets the base path for templates (relative to the executable)
    /// </summary>
    protected virtual string TemplateBasePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates");

    /// <summary>
    /// Gets the template directory name for this initializer
    /// </summary>
    protected abstract string TemplateDirectory { get; }

    /// <summary>
    /// Gets the full path to the template directory
    /// </summary>
    protected string TemplatePath => Path.Combine(TemplateBasePath, TemplateDirectory);

    /// <summary>
    /// File extensions that should be treated as templates (have placeholders replaced)
    /// </summary>
    protected virtual HashSet<string> TemplateExtensions => new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".json", ".xml", ".yml", ".yaml", ".md", ".txt", ".config", ".props", ".targets"
    };

    /// <summary>
    /// File patterns that should be ignored during template processing
    /// </summary>
    protected virtual HashSet<string> IgnorePatterns => new()
    {
        ".git", ".vs", "bin", "obj", "node_modules", ".idea"
    };

    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        if (!Directory.Exists(TemplatePath))
        {
            return InitializationResult.CreateFailure($"Template directory not found: {TemplatePath}");
        }

        var result = InitializationResult.CreateSuccess($"Applied template from {TemplateDirectory}");

        try
        {
            await ProcessTemplateDirectoryAsync(TemplatePath, context.TargetDirectory, context, result);

            // Allow derived classes to perform additional processing
            await PostProcessTemplateAsync(context, result);

            return result;
        }
        catch (Exception ex)
        {
            return InitializationResult.CreateFailure($"Template processing failed: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// Recursively processes a template directory
    /// </summary>
    private async Task ProcessTemplateDirectoryAsync(string templateDir, string targetDir, InitializationContext context, InitializationResult result)
    {
        foreach (var file in Directory.GetFiles(templateDir))
        {
            await ProcessTemplateFileAsync(file, templateDir, targetDir, context, result);
        }

        foreach (var directory in Directory.GetDirectories(templateDir))
        {
            var dirName = Path.GetFileName(directory);
            if (IgnorePatterns.Contains(dirName))
            {
                continue;
            }

            var processedDirName = ReplacePlaceholders(dirName, context);
            var targetSubDir = Path.Combine(targetDir, processedDirName);

            EnsureDirectoryExists(targetSubDir);
            await ProcessTemplateDirectoryAsync(directory, targetSubDir, context, result);
        }
    }

    /// <summary>
    /// Processes a single template file
    /// </summary>
    private async Task ProcessTemplateFileAsync(string templateFile, string templateBaseDir, string targetDir, InitializationContext context, InitializationResult result)
    {
        var relativePath = Path.GetRelativePath(templateBaseDir, templateFile);
        var processedPath = ReplacePlaceholders(relativePath, context);
        var targetFile = Path.Combine(targetDir, processedPath);

        var extension = Path.GetExtension(templateFile);

        if (TemplateExtensions.Contains(extension))
        {
            // Process as template with placeholder replacement
            var content = await File.ReadAllTextAsync(templateFile);
            var processedContent = ReplacePlaceholders(content, context);

            // Allow derived classes to further process content
            processedContent = await ProcessTemplateContentAsync(processedContent, templateFile, targetFile, context);

            await WriteFileAsync(targetFile, processedContent, context);
        }
        else
        {
            // Copy binary file as-is
            await CopyFileAsync(templateFile, targetFile, context);
        }

        result.AffectedFiles.Add(targetFile);
    }

    /// <summary>
    /// Override this method to perform additional content processing
    /// </summary>
    protected virtual Task<string> ProcessTemplateContentAsync(string content, string templateFile, string targetFile, InitializationContext context)
    {
        return Task.FromResult(content);
    }

    /// <summary>
    /// Override this method to perform additional processing after template application
    /// </summary>
    protected virtual Task PostProcessTemplateAsync(InitializationContext context, InitializationResult result)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Helper method to add custom placeholder replacements
    /// </summary>
    protected string ReplacePlaceholdersWithCustom(string content, InitializationContext context, Dictionary<string, string> customPlaceholders)
    {
        var result = ReplacePlaceholders(content, context);

        foreach (var placeholder in customPlaceholders)
        {
            result = result.Replace(placeholder.Key, placeholder.Value);
        }

        return result;
    }

    /// <summary>
    /// Validates that the template directory exists and contains required files
    /// </summary>
    public override async Task<bool> ShouldRunAsync(InitializationContext context)
    {
        var shouldRun = await base.ShouldRunAsync(context);
        if (!shouldRun)
        {
            return false;
        }

        if (!Directory.Exists(TemplatePath))
        {
            AnsiConsole.MarkupLine($"[yellow]Warning: Template directory not found: {TemplatePath}[/]");
            return false;
        }

        return true;
    }
}