namespace PKS.Infrastructure.Initializers.Context;

/// <summary>
/// Context information passed to initializers during execution
/// </summary>
public class InitializationContext
{
    /// <summary>
    /// Name of the project being initialized
    /// </summary>
    public required string ProjectName { get; init; }

    /// <summary>
    /// Optional description of the project
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The template type being used
    /// </summary>
    public required string Template { get; init; }

    /// <summary>
    /// Whether to force overwrite existing files
    /// </summary>
    public bool Force { get; init; }

    /// <summary>
    /// Target directory where the project should be created
    /// </summary>
    public required string TargetDirectory { get; init; }

    /// <summary>
    /// Current working directory
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Collection of user-provided options and their values
    /// </summary>
    public Dictionary<string, object?> Options { get; init; } = new();

    /// <summary>
    /// Whether to run in interactive mode (allow prompts)
    /// </summary>
    public bool Interactive { get; init; } = true;

    /// <summary>
    /// Additional metadata that can be used by initializers
    /// </summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();

    /// <summary>
    /// Gets an option value of the specified type
    /// </summary>
    public T? GetOption<T>(string key, T? defaultValue = default)
    {
        if (Options.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Sets an option value
    /// </summary>
    public void SetOption(string key, object? value)
    {
        Options[key] = value;
    }

    /// <summary>
    /// Gets metadata value of the specified type
    /// </summary>
    public T? GetMetadata<T>(string key, T? defaultValue = default)
    {
        if (Metadata.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Sets a metadata value
    /// </summary>
    public void SetMetadata(string key, object? value)
    {
        Metadata[key] = value;
    }
}