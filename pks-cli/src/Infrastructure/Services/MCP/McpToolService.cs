using System.Reflection;
using Microsoft.Extensions.Logging;
using PKS.CLI.Infrastructure.Services.Models;

namespace PKS.CLI.Infrastructure.Services.MCP;

/// <summary>
/// Attribute to mark methods as MCP server tools
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class McpServerToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public bool Enabled { get; }

    public McpServerToolAttribute(string name, string description, string category = "general", bool enabled = true)
    {
        Name = name;
        Description = description;
        Category = category;
        Enabled = enabled;
    }
}

/// <summary>
/// Attribute to define tool parameters with validation
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public class McpToolParameterAttribute : Attribute
{
    public string Description { get; }
    public bool Required { get; }
    public string? DefaultValue { get; }
    public string? ValidationPattern { get; }

    public McpToolParameterAttribute(string description, bool required = false, string? defaultValue = null, string? validationPattern = null)
    {
        Description = description;
        Required = required;
        DefaultValue = defaultValue;
        ValidationPattern = validationPattern;
    }
}

/// <summary>
/// Service for managing MCP tools using attribute-based registration
/// </summary>
public class McpToolService
{
    private readonly ILogger<McpToolService> _logger;
    private readonly Dictionary<string, McpToolDefinition> _tools = new();
    private readonly Dictionary<string, object> _toolServices = new();

    public McpToolService(ILogger<McpToolService> logger)
    {
        _logger = logger;
        RegisterBuiltInTools();
    }

    /// <summary>
    /// Register a service containing MCP tools marked with attributes
    /// </summary>
    /// <typeparam name="T">Service type</typeparam>
    /// <param name="service">Service instance</param>
    public void RegisterService<T>(T service) where T : class
    {
        var serviceType = typeof(T);
        _logger.LogInformation("Registering MCP tool service: {ServiceType}", serviceType.Name);

        var methods = serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
            var toolDefinition = CreateToolDefinition(method, attribute, service);
            
            _tools[attribute.Name] = toolDefinition;
            _toolServices[attribute.Name] = service;
            
            _logger.LogDebug("Registered MCP tool: {ToolName} -> {ServiceType}.{MethodName}", 
                attribute.Name, serviceType.Name, method.Name);
        }
    }

    /// <summary>
    /// Get all available MCP tools
    /// </summary>
    /// <returns>Collection of available tools</returns>
    public IEnumerable<McpTool> GetAvailableTools()
    {
        return _tools.Values.Select(td => new McpTool
        {
            Name = td.Name,
            Description = td.Description,
            Category = td.Category,
            Enabled = td.Enabled,
            InputSchema = td.InputSchema
        });
    }

    /// <summary>
    /// Execute a specific tool with the provided arguments
    /// </summary>
    /// <param name="toolName">Name of the tool to execute</param>
    /// <param name="arguments">Tool arguments</param>
    /// <returns>Tool execution result</returns>
    public async Task<McpToolResult> ExecuteToolAsync(string toolName, Dictionary<string, object> arguments)
    {
        var startTime = DateTime.UtcNow;
        
        try
        {
            if (!_tools.TryGetValue(toolName, out var toolDefinition))
            {
                return new McpToolResult
                {
                    Success = false,
                    Message = $"Tool '{toolName}' not found",
                    ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    Error = "Tool not registered"
                };
            }

            if (!toolDefinition.Enabled)
            {
                return new McpToolResult
                {
                    Success = false,
                    Message = $"Tool '{toolName}' is disabled",
                    ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    Error = "Tool disabled"
                };
            }

            // Validate arguments
            var validationResult = ValidateArguments(toolDefinition, arguments);
            if (!validationResult.IsValid)
            {
                return new McpToolResult
                {
                    Success = false,
                    Message = $"Invalid arguments for tool '{toolName}': {validationResult.ErrorMessage}",
                    ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    Error = validationResult.ErrorMessage
                };
            }

            // Prepare method parameters
            var parameters = PrepareMethodParameters(toolDefinition, arguments);
            var serviceInstance = _toolServices[toolName];

            // Execute the tool
            var result = toolDefinition.Method.Invoke(serviceInstance, parameters);
            
            // Handle async results
            if (result is Task task)
            {
                await task;
                if (task.GetType().IsGenericType)
                {
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                }
                else
                {
                    result = null; // Task with no return value
                }
            }

            return new McpToolResult
            {
                Success = true,
                Message = $"Tool '{toolName}' executed successfully",
                Data = result,
                ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute tool: {ToolName}", toolName);
            return new McpToolResult
            {
                Success = false,
                Message = $"Tool execution failed: {ex.Message}",
                ExecutionTimeMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                Error = ex.ToString()
            };
        }
    }

    /// <summary>
    /// Get tool definition by name
    /// </summary>
    /// <param name="toolName">Tool name</param>
    /// <returns>Tool definition or null if not found</returns>
    public McpToolDefinition? GetToolDefinition(string toolName)
    {
        return _tools.TryGetValue(toolName, out var definition) ? definition : null;
    }

    /// <summary>
    /// Check if a tool exists and is enabled
    /// </summary>
    /// <param name="toolName">Tool name</param>
    /// <returns>True if tool exists and is enabled</returns>
    public bool IsToolAvailable(string toolName)
    {
        return _tools.TryGetValue(toolName, out var definition) && definition.Enabled;
    }

    private void RegisterBuiltInTools()
    {
        // This is where we'll prepare for the migration of the 11 existing PKS tools
        // For now, we'll register placeholder definitions
        _logger.LogInformation("Preparing built-in PKS tool placeholders for future migration");
        
        // TODO: Migrate these from the existing hardcoded implementation:
        // 1. pks-init - Project initialization
        // 2. pks-agent - Agent management  
        // 3. pks-deploy - Deployment operations
        // 4. pks-status - Status monitoring
        // 5. pks-ascii - ASCII art generation
        // 6. pks-devcontainer - Dev container management
        // 7. pks-hooks - Git hooks management
        // 8. pks-prd - PRD document operations
        // 9. pks-mcp - MCP server management
        // 10. pks-github - GitHub integration
        // 11. pks-template - Template operations
    }

    private McpToolDefinition CreateToolDefinition(MethodInfo method, McpServerToolAttribute attribute, object service)
    {
        var parameters = method.GetParameters();
        var schema = CreateInputSchema(parameters);

        return new McpToolDefinition
        {
            Name = attribute.Name,
            Description = attribute.Description,
            Category = attribute.Category,
            Enabled = attribute.Enabled,
            Method = method,
            Service = service,
            InputSchema = schema,
            Parameters = parameters.Select(p => new McpToolParameterDefinition
            {
                Name = p.Name ?? "unknown",
                Type = p.ParameterType,
                Required = !p.HasDefaultValue,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue : null,
                Description = p.GetCustomAttribute<McpToolParameterAttribute>()?.Description ?? "",
                ValidationPattern = p.GetCustomAttribute<McpToolParameterAttribute>()?.ValidationPattern
            }).ToArray()
        };
    }

    private object CreateInputSchema(ParameterInfo[] parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var param in parameters)
        {
            var paramAttr = param.GetCustomAttribute<McpToolParameterAttribute>();
            var paramName = param.Name ?? "unknown";

            properties[paramName] = new
            {
                type = GetJsonSchemaType(param.ParameterType),
                description = paramAttr?.Description ?? $"Parameter {paramName}",
                @default = paramAttr?.DefaultValue
            };

            if (paramAttr?.Required == true || !param.HasDefaultValue)
            {
                required.Add(paramName);
            }
        }

        return new
        {
            type = "object",
            properties,
            required
        };
    }

    private string GetJsonSchemaType(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(int) || type == typeof(long)) return "integer";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type == typeof(bool)) return "boolean";
        if (type.IsArray || type.GetInterface("IEnumerable") != null) return "array";
        return "object";
    }

    private (bool IsValid, string? ErrorMessage) ValidateArguments(McpToolDefinition tool, Dictionary<string, object> arguments)
    {
        foreach (var param in tool.Parameters)
        {
            if (param.Required && !arguments.ContainsKey(param.Name))
            {
                return (false, $"Required parameter '{param.Name}' is missing");
            }

            if (arguments.TryGetValue(param.Name, out var value) && param.ValidationPattern != null)
            {
                // Add regex validation if needed
                // For now, basic validation
                if (value?.ToString()?.Length == 0 && param.Required)
                {
                    return (false, $"Required parameter '{param.Name}' cannot be empty");
                }
            }
        }

        return (true, null);
    }

    private object?[] PrepareMethodParameters(McpToolDefinition tool, Dictionary<string, object> arguments)
    {
        var parameters = new object?[tool.Parameters.Length];

        for (int i = 0; i < tool.Parameters.Length; i++)
        {
            var param = tool.Parameters[i];
            
            if (arguments.TryGetValue(param.Name, out var value))
            {
                // Convert value to the expected parameter type
                parameters[i] = ConvertValue(value, param.Type);
            }
            else
            {
                parameters[i] = param.DefaultValue;
            }
        }

        return parameters;
    }

    private object? ConvertValue(object value, Type targetType)
    {
        try
        {
            if (value == null) return null;
            if (targetType.IsAssignableFrom(value.GetType())) return value;

            // Handle common conversions
            if (targetType == typeof(string)) return value.ToString();
            if (targetType == typeof(int) && int.TryParse(value.ToString(), out var intValue)) return intValue;
            if (targetType == typeof(bool) && bool.TryParse(value.ToString(), out var boolValue)) return boolValue;

            return Convert.ChangeType(value, targetType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert value {Value} to type {Type}", value, targetType);
            return null;
        }
    }
}

/// <summary>
/// Definition of an MCP tool with method reflection info
/// </summary>
public class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "general";
    public bool Enabled { get; set; } = true;
    public MethodInfo Method { get; set; } = null!;
    public object Service { get; set; } = null!;
    public object InputSchema { get; set; } = new();
    public McpToolParameterDefinition[] Parameters { get; set; } = Array.Empty<McpToolParameterDefinition>();
}

/// <summary>
/// Definition of an MCP tool parameter
/// </summary>
public class McpToolParameterDefinition
{
    public string Name { get; set; } = string.Empty;
    public Type Type { get; set; } = typeof(object);
    public bool Required { get; set; }
    public object? DefaultValue { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ValidationPattern { get; set; }
}