using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Base;

/// <summary>
/// Base class for code-based initializers that perform initialization through pure C# logic
/// </summary>
public abstract class CodeInitializer : BaseInitializer
{
    /// <summary>
    /// Executes the code-based initialization logic
    /// </summary>
    protected override async Task<InitializationResult> ExecuteInternalAsync(InitializationContext context)
    {
        var result = InitializationResult.CreateSuccess($"Executed {Name} logic");
        
        try
        {
            // Pre-execution hook
            await PreExecuteAsync(context, result);
            
            // Main execution logic
            await ExecuteCodeLogicAsync(context, result);
            
            // Post-execution hook
            await PostExecuteAsync(context, result);
            
            return result;
        }
        catch (Exception ex)
        {
            return InitializationResult.CreateFailure($"Code execution failed: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// Override this method to implement the main initialization logic
    /// </summary>
    protected abstract Task ExecuteCodeLogicAsync(InitializationContext context, InitializationResult result);

    /// <summary>
    /// Override this method to perform setup before main execution
    /// </summary>
    protected virtual Task PreExecuteAsync(InitializationContext context, InitializationResult result)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Override this method to perform cleanup after main execution
    /// </summary>
    protected virtual Task PostExecuteAsync(InitializationContext context, InitializationResult result)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Helper method to create a directory structure
    /// </summary>
    protected void CreateDirectoryStructure(string basePath, params string[] directories)
    {
        foreach (var directory in directories)
        {
            var fullPath = Path.Combine(basePath, directory);
            EnsureDirectoryExists(fullPath);
        }
    }

    /// <summary>
    /// Helper method to create a file with content
    /// </summary>
    protected async Task CreateFileAsync(string filePath, string content, InitializationContext context, InitializationResult result)
    {
        await WriteFileAsync(filePath, content, context);
        result.AffectedFiles.Add(filePath);
    }

    /// <summary>
    /// Helper method to create a file from an embedded resource
    /// </summary>
    protected async Task CreateFileFromResourceAsync(string resourceName, string targetPath, InitializationContext context, InitializationResult result)
    {
        var assembly = GetType().Assembly;
        var resourceStream = assembly.GetManifestResourceStream(resourceName);
        
        if (resourceStream == null)
        {
            result.Warnings.Add($"Embedded resource not found: {resourceName}");
            return;
        }

        using var reader = new StreamReader(resourceStream);
        var content = await reader.ReadToEndAsync();
        var processedContent = ReplacePlaceholders(content, context);
        
        await CreateFileAsync(targetPath, processedContent, context, result);
    }

    /// <summary>
    /// Helper method to execute a shell command
    /// </summary>
    protected async Task<(bool Success, string Output, string Error)> ExecuteCommandAsync(string command, string arguments, string? workingDirectory = null)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = command;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        process.Start();
        
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        
        await process.WaitForExitAsync();
        
        var output = await outputTask;
        var error = await errorTask;
        
        return (process.ExitCode == 0, output, error);
    }

    /// <summary>
    /// Helper method to download a file from a URL
    /// </summary>
    protected async Task<bool> DownloadFileAsync(string url, string targetPath, InitializationContext context, InitializationResult result)
    {
        try
        {
            using var httpClient = new HttpClient();
            var content = await httpClient.GetStringAsync(url);
            var processedContent = ReplacePlaceholders(content, context);
            
            await CreateFileAsync(targetPath, processedContent, context, result);
            return true;
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Failed to download {url}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Helper method to modify an existing file
    /// </summary>
    protected async Task ModifyFileAsync(string filePath, Func<string, string> modifier, InitializationContext context, InitializationResult result)
    {
        if (!File.Exists(filePath))
        {
            result.Warnings.Add($"File not found for modification: {filePath}");
            return;
        }

        var content = await File.ReadAllTextAsync(filePath);
        var modifiedContent = modifier(content);
        
        if (content != modifiedContent)
        {
            await File.WriteAllTextAsync(filePath, modifiedContent);
            result.AffectedFiles.Add(filePath);
        }
    }

    /// <summary>
    /// Helper method to append content to a file
    /// </summary>
    protected async Task AppendToFileAsync(string filePath, string content, InitializationContext context, InitializationResult result)
    {
        var processedContent = ReplacePlaceholders(content, context);
        
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            EnsureDirectoryExists(directory);
        }

        await File.AppendAllTextAsync(filePath, processedContent);
        result.AffectedFiles.Add(filePath);
    }

    /// <summary>
    /// Helper method to validate that required tools are available
    /// </summary>
    protected async Task<bool> ValidateToolAsync(string toolName, string? versionCommand = null)
    {
        try
        {
            var (success, output, error) = await ExecuteCommandAsync(toolName, versionCommand ?? "--version");
            return success;
        }
        catch
        {
            return false;
        }
    }
}