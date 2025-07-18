namespace PKS.Infrastructure.Initializers.Context;

/// <summary>
/// Result of an initializer execution
/// </summary>
public class InitializationResult
{
    /// <summary>
    /// Whether the initialization was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Additional details about what was done
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// List of files that were created or modified
    /// </summary>
    public List<string> AffectedFiles { get; init; } = new();

    /// <summary>
    /// Any warnings that occurred during initialization
    /// </summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>
    /// Any errors that occurred (even if Success is true)
    /// </summary>
    public List<string> Errors { get; init; } = new();

    /// <summary>
    /// Additional data that can be used by subsequent initializers
    /// </summary>
    public Dictionary<string, object?> Data { get; init; } = new();

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static InitializationResult CreateSuccess(string? message = null, string? details = null)
    {
        return new InitializationResult
        {
            Success = true,
            Message = message,
            Details = details
        };
    }

    /// <summary>
    /// Creates a failed result
    /// </summary>
    public static InitializationResult CreateFailure(string message, string? details = null)
    {
        return new InitializationResult
        {
            Success = false,
            Message = message,
            Details = details
        };
    }

    /// <summary>
    /// Creates a result with warnings
    /// </summary>
    public static InitializationResult CreateSuccessWithWarnings(string? message = null, params string[] warnings)
    {
        return new InitializationResult
        {
            Success = true,
            Message = message,
            Warnings = warnings.ToList()
        };
    }
}