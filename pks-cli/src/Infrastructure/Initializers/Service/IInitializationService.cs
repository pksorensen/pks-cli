using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Service;

/// <summary>
/// Service for managing the initialization process
/// </summary>
public interface IInitializationService
{
    /// <summary>
    /// Initializes a project with the given context
    /// </summary>
    Task<InitializationSummary> InitializeProjectAsync(InitializationContext context);

    /// <summary>
    /// Gets available templates that can be used for initialization
    /// </summary>
    Task<IEnumerable<TemplateInfo>> GetAvailableTemplatesAsync();

    /// <summary>
    /// Validates that the target directory is suitable for initialization
    /// </summary>
    Task<ValidationResult> ValidateTargetDirectoryAsync(string targetDirectory, bool force);

    /// <summary>
    /// Validates that the project name is valid for use
    /// </summary>
    ValidationResult ValidateProjectName(string projectName);

    /// <summary>
    /// Creates an initialization context from command-line settings
    /// </summary>
    InitializationContext CreateContext(string projectName, string template, string targetDirectory, bool force, Dictionary<string, object?> options);
}