using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers;

/// <summary>
/// Core interface for all project initializers
/// </summary>
public interface IInitializer
{
    /// <summary>
    /// Unique identifier for this initializer
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Display name for this initializer
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description of what this initializer does
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Priority order for execution (lower numbers execute first)
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Determines if this initializer should run based on the context
    /// </summary>
    /// <param name="context">The initialization context</param>
    /// <returns>True if this initializer should execute</returns>
    Task<bool> ShouldRunAsync(InitializationContext context);

    /// <summary>
    /// Executes the initialization logic
    /// </summary>
    /// <param name="context">The initialization context</param>
    /// <returns>The result of the initialization</returns>
    Task<InitializationResult> ExecuteAsync(InitializationContext context);

    /// <summary>
    /// Gets the command line options this initializer contributes
    /// </summary>
    IEnumerable<InitializerOption> GetOptions();
}