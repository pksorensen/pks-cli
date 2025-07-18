using PKS.Infrastructure.Initializers.Context;
using Spectre.Console;

namespace PKS.Infrastructure.Initializers.Base;

/// <summary>
/// Base class for all initializers providing common functionality
/// </summary>
public abstract class BaseInitializer : IInitializer
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public virtual int Order => 100; // Default order, can be overridden

    /// <summary>
    /// Default implementation always runs, override for conditional logic
    /// </summary>
    public virtual Task<bool> ShouldRunAsync(InitializationContext context)
    {
        return Task.FromResult(true);
    }

    /// <summary>
    /// Executes the initializer with error handling and logging
    /// </summary>
    public async Task<InitializationResult> ExecuteAsync(InitializationContext context)
    {
        try
        {
            LogStart(context);
            var result = await ExecuteInternalAsync(context);
            LogResult(result);
            return result;
        }
        catch (Exception ex)
        {
            var errorResult = InitializationResult.CreateFailure($"Failed to execute {Name}: {ex.Message}", ex.ToString());
            LogResult(errorResult);
            return errorResult;
        }
    }

    /// <summary>
    /// Override this method to implement the actual initialization logic
    /// </summary>
    protected abstract Task<InitializationResult> ExecuteInternalAsync(InitializationContext context);

    /// <summary>
    /// Override this method to provide command-line options
    /// </summary>
    public virtual IEnumerable<InitializerOption> GetOptions()
    {
        return Enumerable.Empty<InitializerOption>();
    }

    /// <summary>
    /// Helper method to check if a file exists and handle force/interactive modes
    /// </summary>
    protected async Task<bool> ShouldOverwriteFileAsync(string filePath, InitializationContext context)
    {
        if (!File.Exists(filePath))
        {
            return true;
        }

        if (context.Force)
        {
            return true;
        }

        if (!context.Interactive)
        {
            // In non-interactive mode, don't overwrite unless forced
            return false;
        }

        return AnsiConsole.Confirm($"File [yellow]{Path.GetFileName(filePath)}[/] already exists. Overwrite?");
    }

    /// <summary>
    /// Helper method to ensure a directory exists
    /// </summary>
    protected void EnsureDirectoryExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    /// <summary>
    /// Helper method to write a file with proper directory creation
    /// </summary>
    protected async Task WriteFileAsync(string filePath, string content, InitializationContext context)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsureDirectoryExists(directory);
        }

        if (await ShouldOverwriteFileAsync(filePath, context))
        {
            await File.WriteAllTextAsync(filePath, content);
        }
    }

    /// <summary>
    /// Helper method to copy a file with proper handling
    /// </summary>
    protected async Task<bool> CopyFileAsync(string sourcePath, string destinationPath, InitializationContext context)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsureDirectoryExists(directory);
        }

        if (await ShouldOverwriteFileAsync(destinationPath, context))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Logs the start of initialization
    /// </summary>
    protected virtual void LogStart(InitializationContext context)
    {
        AnsiConsole.MarkupLine($"[dim]Running initializer: {Name}[/]");
    }

    /// <summary>
    /// Logs the result of initialization
    /// </summary>
    protected virtual void LogResult(InitializationResult result)
    {
        if (result.Success)
        {
            if (result.Warnings.Any())
            {
                AnsiConsole.MarkupLine($"[yellow]⚠ {Name}: {result.Message ?? "Completed with warnings"}[/]");
                foreach (var warning in result.Warnings)
                {
                    AnsiConsole.MarkupLine($"[dim]  Warning: {warning}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]✓ {Name}: {result.Message ?? "Completed successfully"}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ {Name}: {result.Message ?? "Failed"}[/]");
            foreach (var error in result.Errors)
            {
                AnsiConsole.MarkupLine($"[dim]  Error: {error}[/]");
            }
        }
    }

    /// <summary>
    /// Helper method to replace placeholders in content
    /// </summary>
    protected string ReplacePlaceholders(string content, InitializationContext context)
    {
        var replacements = new Dictionary<string, string>
        {
            { "{{ProjectName}}", context.ProjectName },
            { "{{Project.Name}}", context.ProjectName },
            { "{{PROJECT_NAME}}", context.ProjectName.ToUpperInvariant() },
            { "{{project_name}}", context.ProjectName.ToLowerInvariant() },
            { "{{Description}}", context.Description ?? "" },
            { "{{Project.Description}}", context.Description ?? "" },
            { "{{Template}}", context.Template },
            { "{{Project.Template}}", context.Template },
            { "{{Date}}", DateTime.Now.ToString("yyyy-MM-dd") },
            { "{{DateTime}}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
            { "{{Year}}", DateTime.Now.Year.ToString() }
        };

        var result = content;
        foreach (var replacement in replacements)
        {
            result = result.Replace(replacement.Key, replacement.Value);
        }

        return result;
    }
}