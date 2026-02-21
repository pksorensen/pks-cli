using PKS.Infrastructure.Services.Models;

namespace PKS.Infrastructure.Services;

/// <summary>
/// Service for generating devcontainer files
/// </summary>
public interface IDevcontainerFileGenerator
{
    /// <summary>
    /// Generates devcontainer.json file
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateDevcontainerJsonAsync(DevcontainerConfiguration configuration, string outputPath);

    /// <summary>
    /// Generates Dockerfile for devcontainer
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateDockerfileAsync(DevcontainerConfiguration configuration, string outputPath);

    /// <summary>
    /// Generates docker-compose.yml for devcontainer
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateDockerComposeAsync(DevcontainerConfiguration configuration, string outputPath);

    /// <summary>
    /// Validates that the output path is suitable for file generation
    /// </summary>
    /// <param name="path">Output path to validate</param>
    /// <returns>Path validation result</returns>
    Task<PathValidationResult> ValidateOutputPathAsync(string path);

    /// <summary>
    /// Generates .gitignore entries for devcontainer
    /// </summary>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateGitIgnoreAsync(string outputPath);

    /// <summary>
    /// Generates VS Code settings.json with devcontainer-specific settings
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateVSCodeSettingsAsync(DevcontainerConfiguration configuration, string outputPath);

    /// <summary>
    /// Generates README.md with devcontainer usage instructions
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <returns>File generation result</returns>
    Task<FileGenerationResult> GenerateReadmeAsync(DevcontainerConfiguration configuration, string outputPath);

    /// <summary>
    /// Generates all devcontainer files based on configuration
    /// </summary>
    /// <param name="configuration">Devcontainer configuration</param>
    /// <param name="outputPath">Output directory path</param>
    /// <param name="options">Generation options</param>
    /// <returns>List of generation results</returns>
    Task<List<FileGenerationResult>> GenerateAllFilesAsync(DevcontainerConfiguration configuration, string outputPath, DevcontainerOptions? options = null);
}