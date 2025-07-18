using Microsoft.Extensions.DependencyInjection;
using PKS.Infrastructure.Initializers.Context;
using Spectre.Console;

namespace PKS.Infrastructure.Initializers.Registry;

/// <summary>
/// Default implementation of the initializer registry
/// </summary>
public class InitializerRegistry : IInitializerRegistry
{
    private readonly List<IInitializer> _instances = new();
    private readonly List<Type> _types = new();
    private readonly IServiceProvider _serviceProvider;

    public InitializerRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void Register(IInitializer initializer)
    {
        _instances.Add(initializer);
    }

    public void Register<T>() where T : class, IInitializer
    {
        _types.Add(typeof(T));
    }

    public async Task<IEnumerable<IInitializer>> GetAllAsync()
    {
        var allInitializers = new List<IInitializer>(_instances);
        
        // Resolve types from DI container
        foreach (var type in _types)
        {
            var initializer = (IInitializer)_serviceProvider.GetRequiredService(type);
            allInitializers.Add(initializer);
        }

        return allInitializers.OrderBy(i => i.Order).ThenBy(i => i.Name);
    }

    public async Task<IEnumerable<IInitializer>> GetApplicableAsync(InitializationContext context)
    {
        var allInitializers = await GetAllAsync();
        var applicable = new List<IInitializer>();

        foreach (var initializer in allInitializers)
        {
            try
            {
                if (await initializer.ShouldRunAsync(context))
                {
                    applicable.Add(initializer);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Error checking if {initializer.Name} should run: {ex.Message}[/]");
            }
        }

        return applicable;
    }

    public async Task<IInitializer?> GetByIdAsync(string id)
    {
        var allInitializers = await GetAllAsync();
        return allInitializers.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<InitializerOption> GetAllOptions()
    {
        var options = new List<InitializerOption>();
        
        // Get options from instance initializers
        foreach (var initializer in _instances)
        {
            options.AddRange(initializer.GetOptions());
        }

        // Get options from type initializers (create temporary instances)
        foreach (var type in _types)
        {
            try
            {
                var initializer = (IInitializer)_serviceProvider.GetRequiredService(type);
                options.AddRange(initializer.GetOptions());
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not get options from {type.Name}: {ex.Message}[/]");
            }
        }

        return options.DistinctBy(o => o.Name);
    }

    public async Task<IEnumerable<InitializationResult>> ExecuteAllAsync(InitializationContext context)
    {
        var applicableInitializers = await GetApplicableAsync(context);
        var results = new List<InitializationResult>();

        AnsiConsole.MarkupLine($"[cyan]Running {applicableInitializers.Count()} initializers...[/]");
        AnsiConsole.WriteLine();

        foreach (var initializer in applicableInitializers)
        {
            try
            {
                var result = await initializer.ExecuteAsync(context);
                results.Add(result);

                // If an initializer fails and it's critical, stop execution
                if (!result.Success && IsCriticalInitializer(initializer))
                {
                    AnsiConsole.MarkupLine($"[red]Critical initializer {initializer.Name} failed. Stopping execution.[/]");
                    break;
                }
            }
            catch (Exception ex)
            {
                var errorResult = InitializationResult.CreateFailure($"Exception in {initializer.Name}: {ex.Message}", ex.ToString());
                results.Add(errorResult);
                
                AnsiConsole.MarkupLine($"[red]Exception in initializer {initializer.Name}: {ex.Message}[/]");
                
                if (IsCriticalInitializer(initializer))
                {
                    AnsiConsole.MarkupLine($"[red]Critical initializer failed. Stopping execution.[/]");
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Determines if an initializer is critical (failure should stop execution)
    /// </summary>
    private bool IsCriticalInitializer(IInitializer initializer)
    {
        // Initializers with lower order numbers are considered more critical
        return initializer.Order < 50;
    }

    /// <summary>
    /// Discovers and registers initializers from assemblies
    /// </summary>
    public void DiscoverAndRegister()
    {
        var initializerTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IInitializer).IsAssignableFrom(type) && 
                          !type.IsInterface && 
                          !type.IsAbstract)
            .ToList();

        foreach (var type in initializerTypes)
        {
            try
            {
                // Try to register with DI if possible
                var serviceDescriptor = _serviceProvider.GetService(type);
                if (serviceDescriptor != null)
                {
                    _types.Add(type);
                }
                else
                {
                    // Fallback to direct instantiation if no parameterless constructor
                    try
                    {
                        var instance = (IInitializer)Activator.CreateInstance(type);
                        if (instance != null)
                        {
                            Register(instance);
                        }
                    }
                    catch
                    {
                        // Skip types that can't be instantiated
                        AnsiConsole.MarkupLine($"[dim]Skipping initializer {type.Name} - cannot instantiate[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Could not register initializer {type.Name}: {ex.Message}[/]");
            }
        }

        var totalCount = _instances.Count + _types.Count;
        if (totalCount > 0)
        {
            AnsiConsole.MarkupLine($"[dim]Discovered and registered {totalCount} initializers[/]");
        }
    }
}