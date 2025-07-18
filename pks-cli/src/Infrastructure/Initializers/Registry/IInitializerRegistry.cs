using PKS.Infrastructure.Initializers.Context;

namespace PKS.Infrastructure.Initializers.Registry;

/// <summary>
/// Registry for managing and discovering initializers
/// </summary>
public interface IInitializerRegistry
{
    /// <summary>
    /// Registers an initializer
    /// </summary>
    void Register(IInitializer initializer);

    /// <summary>
    /// Registers an initializer type to be resolved via DI
    /// </summary>
    void Register<T>() where T : class, IInitializer;

    /// <summary>
    /// Gets all registered initializers
    /// </summary>
    Task<IEnumerable<IInitializer>> GetAllAsync();

    /// <summary>
    /// Gets initializers that should run for the given context
    /// </summary>
    Task<IEnumerable<IInitializer>> GetApplicableAsync(InitializationContext context);

    /// <summary>
    /// Gets an initializer by ID
    /// </summary>
    Task<IInitializer?> GetByIdAsync(string id);

    /// <summary>
    /// Gets all command-line options from all registered initializers
    /// </summary>
    IEnumerable<InitializerOption> GetAllOptions();

    /// <summary>
    /// Executes all applicable initializers in order
    /// </summary>
    Task<IEnumerable<InitializationResult>> ExecuteAllAsync(InitializationContext context);

    /// <summary>
    /// Discovers and registers initializers from assemblies
    /// </summary>
    void DiscoverAndRegister();
}