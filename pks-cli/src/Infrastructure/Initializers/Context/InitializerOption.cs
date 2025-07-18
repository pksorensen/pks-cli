using System.ComponentModel;

namespace PKS.Infrastructure.Initializers.Context;

/// <summary>
/// Represents a command-line option that an initializer contributes
/// </summary>
public class InitializerOption
{
    /// <summary>
    /// The option name (used in --option-name format)
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Short form of the option (used in -o format)
    /// </summary>
    public string? ShortName { get; init; }

    /// <summary>
    /// Description of what this option does
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The type of value this option expects
    /// </summary>
    public Type ValueType { get; init; } = typeof(bool);

    /// <summary>
    /// Default value for this option
    /// </summary>
    public object? DefaultValue { get; init; }

    /// <summary>
    /// Whether this option is required
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Whether this option can have multiple values
    /// </summary>
    public bool IsArray { get; init; }

    /// <summary>
    /// Validation function for the option value
    /// </summary>
    public Func<object?, string?>? Validator { get; init; }

    /// <summary>
    /// Creates a boolean flag option
    /// </summary>
    public static InitializerOption Flag(string name, string description, string? shortName = null)
    {
        return new InitializerOption
        {
            Name = name,
            ShortName = shortName,
            Description = description,
            ValueType = typeof(bool),
            DefaultValue = false
        };
    }

    /// <summary>
    /// Creates a string option
    /// </summary>
    public static InitializerOption String(string name, string description, string? shortName = null, string? defaultValue = null, bool required = false)
    {
        return new InitializerOption
        {
            Name = name,
            ShortName = shortName,
            Description = description,
            ValueType = typeof(string),
            DefaultValue = defaultValue,
            Required = required
        };
    }

    /// <summary>
    /// Creates an integer option
    /// </summary>
    public static InitializerOption Integer(string name, string description, string? shortName = null, int? defaultValue = null, bool required = false)
    {
        return new InitializerOption
        {
            Name = name,
            ShortName = shortName,
            Description = description,
            ValueType = typeof(int),
            DefaultValue = defaultValue,
            Required = required
        };
    }

    /// <summary>
    /// Creates a string array option
    /// </summary>
    public static InitializerOption StringArray(string name, string description, string? shortName = null, string[]? defaultValue = null)
    {
        return new InitializerOption
        {
            Name = name,
            ShortName = shortName,
            Description = description,
            ValueType = typeof(string[]),
            DefaultValue = defaultValue,
            IsArray = true
        };
    }
}