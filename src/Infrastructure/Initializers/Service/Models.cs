using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Service;

/// <summary>
/// Summary of an initialization process
/// </summary>
public class InitializationSummary
{
    public required string ProjectName { get; init; }
    public required string Template { get; init; }
    public required string TargetDirectory { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<InitializationResult> Results { get; set; } = new();
    public int FilesCreated { get; set; }
    public int WarningsCount { get; set; }
    public int ErrorsCount { get; set; }

    public TimeSpan Duration => EndTime?.Subtract(StartTime) ?? TimeSpan.Zero;
}

/// <summary>
/// Information about an available template
/// </summary>
public class TemplateInfo
{
    public required string Name { get; init; }
    public required string DisplayName { get; set; }
    public required string Description { get; set; }
    public string? Path { get; set; }
    public List<string> Tags { get; set; } = new();
    public string? Author { get; set; }
    public string? Version { get; set; }
    public bool IsBuiltIn => string.IsNullOrEmpty(Path);
}

/// <summary>
/// Template metadata loaded from template.json
/// </summary>
public class TemplateMetadata
{
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public List<string>? Tags { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public Dictionary<string, object?>? DefaultOptions { get; set; }
}

/// <summary>
/// Result of directory validation
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static ValidationResult Valid() => new() { IsValid = true };
    public static ValidationResult Invalid(string message) => new() { IsValid = false, ErrorMessage = message };
}